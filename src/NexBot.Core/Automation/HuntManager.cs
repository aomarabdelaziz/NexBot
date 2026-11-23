using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Navigation;
using NexBot.Network;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// The hunting bot (phBot's Attack tab and Training Area): in the training area it resurrects, heals and buffs, lures,
/// chooses a monster (those attacking you first, then the monster preferences, then the nearest), imbues and casts the
/// skills of the monster's type, and walks around between monsters. It lets the pick filter pick up between monsters,
/// asks for the walk to the area when it is far, and waits while a script, trade route or town loop moves the character.
/// </summary>
public sealed partial class HuntManager : IDisposable
{
    private static readonly TimeSpan AttackerMemory = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OthersMemory = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IgnoreFor = TimeSpan.FromSeconds(60);
    private const int MaxFailures = 4;
    /// <summary>Farther than this from the training area, the walk there is asked for (town loop / map routes).</summary>
    public const double WalkBackLimit = 400;

    private readonly SroClient _client;
    private readonly AttackSettings _settings;
    private readonly ProtectionSettings _protection;
    private readonly TrainingAreaSettings _area;
    private readonly TrainingSetupSettings _setup;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _random = new();
    private readonly ConcurrentDictionary<uint, DateTime> _attackingMe = new();
    private readonly ConcurrentDictionary<uint, DateTime> _attackingParty = new();
    private readonly ConcurrentDictionary<uint, DateTime> _othersFight = new();
    private readonly Dictionary<uint, DateTime> _ignored = [];
    private readonly Dictionary<uint, DateTime> _buffSent = [];
    private readonly Dictionary<uint, DateTime> _buffBlocked = [];
    private readonly HashSet<string> _notes = [];
    private uint _target;
    private int _next, _failures, _dots;
    private DateTime _busyUntil, _progress, _commonSent, _walkSent, _holdUntil, _walkAskedAt;
    private uint _lastBuff;
    private DateTime _lastBuffAt;
    private int _walkAsks;

    public HuntManager(SroClient client, AttackSettings settings, ProtectionSettings protection, TrainingAreaSettings area,
        TrainingSetupSettings? setup = null, ILogger? logger = null, bool startLoop = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _area = area ?? throw new ArgumentNullException(nameof(area));
        _setup = setup ?? new TrainingSetupSettings();
        _logger = logger ?? NullLogger.Instance;
        client.DamageDealt += OnDamage;
        client.AttackFailed += OnAttackFailed;
        client.PetitionReceived += OnPetition;
        client.PacketSent += OnPacketSent;
        if (startLoop)
            _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Hunting was started (it may be paused).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Something else moves the character now (script, trade route, town loop): hunting waits.</summary>
    public Func<bool> Paused { get; set; } = () => false;

    /// <summary>Drops wait for the character to pick them up.</summary>
    public Func<bool> PickWaiting { get; set; } = () => false;

    /// <summary>The pick filter's "Pick items first".</summary>
    public Func<bool> PickItemsFirst { get; set; } = () => false;

    /// <summary>The map data, for collision detection and walking around obstacles (null: not loaded).</summary>
    public Func<IWalkMap?> Map { get; set; } = () => null;

    /// <summary>Starts the walk to the training area (town loop, or the map's routes); false when it cannot.</summary>
    public Func<bool> WalkToArea { get; set; } = () => false;

    /// <summary>Asks Protection to return to town (with the reason).</summary>
    public Action<string> ReturnToTown { get; set; } = _ => { };

    /// <summary>Enables another training area by name (Conditions tab); false when there is no such area.</summary>
    public Func<string, bool> SwitchArea { get; set; } = _ => false;

    /// <summary>The character may walk to a drop now: not hunting, or between monsters.</summary>
    public bool MayPick => !IsRunning || (_target == 0 && _lure == LureState.Idle && _walkingTo == 0);

    /// <summary>The monster attacked now (0: none).</summary>
    public uint Target => _target;

    public string Status { get; private set; } = "Stopped.";
    public int Kills { get; private set; }

    /// <summary>A player who attacked or killed you (phBot's Attack Log).</summary>
    public sealed record AttackEntry(DateTime At, string Player, uint Damage, bool Killed);

    private readonly List<AttackEntry> _attackLog = [];
    private readonly Lock _logLock = new();

    /// <summary>The players who attacked or killed you, newest first (the last 200).</summary>
    public IReadOnlyList<AttackEntry> AttackLog
    {
        get
        {
            lock (_logLock)
                return [.. _attackLog];
        }
    }

    public void ClearAttackLog()
    {
        lock (_logLock)
            _attackLog.Clear();
    }

    public void Start()
    {
        if (IsRunning)
            return;
        _target = 0;
        _ignored.Clear();
        _buffBlocked.Clear();
        _notes.Clear();
        _resTries.Clear();
        _holdUntil = DateTime.MinValue;
        _walkAsks = 0;
        _lure = LureState.Idle;
        IsRunning = true;
        Status = "Starting.";
        _logger.LogInformation("Attack: hunting started");
    }

    public void Stop(string? reason = null)
    {
        if (!IsRunning)
            return;
        IsRunning = false;
        _target = 0;
        _lure = LureState.Idle;
        Status = "Stopped.";
        _logger.LogInformation("Attack: hunting stopped{Reason}", reason is null ? "" : $" ({reason})");
    }

    /// <summary>Don't attack or buff for a while (a return scroll is being used).</summary>
    public void Hold(TimeSpan time)
    {
        _holdUntil = DateTime.UtcNow + time;
        _target = 0;
        _lure = LureState.Idle;
    }

    // ------------------------------------------------------------------ what happens around

    private void OnDamage(object? sender, DamageEventArgs e)
    {
        if (_client.World.Character is not { } c)
            return;
        var now = DateTime.UtcNow;
        var source = _client.World.GetEntity(e.SourceId);
        var target = _client.World.GetEntity(e.TargetId);
        bool mine = e.SourceId == c.UniqueId || _client.World.MyPets.ContainsKey(e.SourceId);
        if (e.TargetId == c.UniqueId && source is Player pk && !mine)
        {
            lock (_logLock)
            {
                _attackLog.Insert(0, new AttackEntry(DateTime.Now, pk.Name, e.Damage, e.IsKill));
                if (_attackLog.Count > 200)
                    _attackLog.RemoveAt(_attackLog.Count - 1);
            }
        }
        if (source is Mob attacker)
        {
            if (e.TargetId == c.UniqueId || _client.World.MyPets.ContainsKey(e.TargetId))
                _attackingMe[attacker.UniqueId] = now;
            else if (target is Player p && IsPartyMember(p.Name))
            {
                if (!_settings.ProtectParty || _settings.ProtectPartyName.Length == 0
                    || string.Equals(_settings.ProtectPartyName, p.Name, StringComparison.OrdinalIgnoreCase))
                    _attackingParty[attacker.UniqueId] = now;
            }
            else if (target is Player)
                _othersFight[attacker.UniqueId] = now;
        }
        if (target is Mob hit)
        {
            if (mine)
            {
                if (hit.UniqueId == _target)
                {
                    _progress = now;
                    _failures = 0;
                }
            }
            else if (source is Player other && !IsPartyMember(other.Name))
                _othersFight[hit.UniqueId] = now;
        }
        if (e.IsKill && e.TargetId == _target && _target != 0)
        {
            Kills++;
            _target = 0;
            _busyUntil = DateTime.MinValue;
        }
    }

    private bool IsPartyMember(string name) =>
        _client.World.Party?.Members.Values.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) == true;

    private void OnAttackFailed(object? sender, AttackFailedEventArgs e)
    {
        if (!IsRunning)
            return;
        var now = DateTime.UtcNow;
        if (_lastBuff != 0 && now - _lastBuffAt < TimeSpan.FromSeconds(3))
        {
            // A buff refused (already on, wrong weapon ...): leave it for a while.
            uint id = _lastBuff;
            _lastBuff = 0;
            _buffBlocked[id] = now + TimeSpan.FromSeconds(30);
            string name = _client.Data.GetSkill(id)?.Name ?? id.ToString(CultureInfo.InvariantCulture);
            if (_notes.Add($"buff {id} {e.ErrorCode}"))
                _logger.LogInformation("Attack: {Buff} was refused (0x{Error:X4}); it is tried again in 30 seconds", name, e.ErrorCode);
            return;
        }
        if (_target == 0)
            return;
        _failures++;
        if (_notes.Add($"attack {e.ErrorCode}"))
            _logger.LogInformation("Attack: an attack was refused (0x{Error:X4})", e.ErrorCode);
        if (_failures >= MaxFailures)
            Ignore(_target, $"refused {_failures} times (0x{e.ErrorCode:X4})");
    }

    private void Ignore(uint mob, string why)
    {
        _ignored[mob] = DateTime.UtcNow + IgnoreFor;
        string name = _client.World.GetEntity(mob)?.Name ?? mob.ToString(CultureInfo.InvariantCulture);
        _logger.LogInformation("Attack: {Monster} is left alone for a minute: {Why}", name, why);
        if (_target == mob)
            _target = 0;
        _failures = 0;
    }

    // ------------------------------------------------------------------ the loop

    private void Step()
    {
        if (!IsRunning)
        {
            Status = "Stopped.";
            AutoSelect();
            return;
        }
        if (!_client.IsInGame || _client.World.Character is not { } c)
        {
            Status = "Not in game.";
            return;
        }
        if (c.LifeState == LifeState.Dead)
        {
            Status = "The character is dead.";
            _target = 0;
            _lure = LureState.Idle;
            return;
        }
        var now = DateTime.UtcNow;
        if (now < _holdUntil)
        {
            Status = "Waiting (returning to town).";
            return;
        }
        if (Paused())
        {
            Status = "Waiting: a script, trade route or town loop moves the character.";
            _target = 0;
            _lure = LureState.Idle;
            SpeedDrugs(c, inScript: true);
            return;
        }
        CheckConditions(c);
        Forget(_attackingMe, now - AttackerMemory);
        Forget(_attackingParty, now - AttackerMemory);
        Forget(_othersFight, now - OthersMemory);
        foreach (uint id in _ignored.Where(i => i.Value < now).Select(i => i.Key).ToList())
            _ignored.Remove(id);
        if (now < _busyUntil)
            return;

        var skills = c.Skills.Values.ToList();
        byte weapon = c.Inventory[6]?.TypeId4 ?? 0;
        if (SwitchBackWeapon(c, now))
            return;

        // Support first: resurrect, heal, cure, buffs.
        if (Resurrect(c, skills, weapon, now) || Heal(c, skills, weapon, now) || Cure(c, skills, weapon))
            return;
        int mpPercent = c.MpMax == 0 ? 100 : (int)(c.Mp * 100L / c.MpMax);
        bool emergency = (_settings.EmergencyHp > 0 && c.HpPercent < _settings.EmergencyHp)
            || (_settings.EmergencyMp > 0 && mpPercent < _settings.EmergencyMp)
            || (_settings.EmergencyMonsters > 0 && _attackingMe.Count > _settings.EmergencyMonsters);
        if (emergency && CastBuff(c, skills, weapon, _settings.EmergencyBuffs, "emergency buff"))
            return;
        bool buffNow = (_target == 0 || _settings.BuffWhileAttacking) && !(_settings.LureEnabled && _settings.LureBuffOnlyAfterScript && _lure != LureState.Idle);
        if (buffNow && (CastBuff(c, skills, weapon, _settings.Buffs, "buff") || DevilSpirit(c, skills, weapon) || PartyBuffs(c, skills, weapon, now)))
            return;
        if (UseItems(c, now))
            return;

        if (!_area.IsSet && !(_area.UsePolygon && _area.HasPolygon))
        {
            Status = "Set the training area first (Training area page, or Map page: right-click → Set training area).";
            return;
        }
        var here = c.EstimatePosition();
        var (cx, cy) = _area.Center;
        double away = here.DistanceTo(cx, cy);
        if (away > WalkBackLimit && _attackingMe.IsEmpty)
        {
            AskWalk(away, cx, cy, now);
            return;
        }
        _walkAsks = 0;

        if (_settings.DontAttack)
        {
            _target = 0;
            if (!LureStep(c, skills, weapon, here, now))
                Status = "Don't attack monsters is on: only supporting.";
            return;
        }

        var monsters = Monsters(here);
        var mob = Current(monsters, here);
        if (mob is null)
        {
            if (PickWaiting() && (PickItemsFirst() || monsters.Count == 0) && _attackingMe.IsEmpty)
            {
                Status = "Picking up items.";
                return;
            }
            mob = AttackPlan.ChooseTarget(_settings, _protection.MonsterPreferences, monsters, here,
                _attackingMe.Keys.ToHashSet(), _attackingParty.Keys.ToHashSet());
            if (mob is null)
            {
                if (!LureStep(c, skills, weapon, here, now) && !SummonScroll(c, now))
                    WalkAround(c, here, now);
                return;
            }
            _lure = LureState.Idle;
            _target = mob.UniqueId;
            _next = 0;
            _dots = 0;
            _failures = 0;
            _progress = now;
            _commonSent = DateTime.MinValue;
        }
        Attack(c, mob, skills, weapon, here, now);
    }

    private void AskWalk(double away, double cx, double cy, DateTime now)
    {
        if (now - _walkAskedAt < TimeSpan.FromSeconds(20))
            return;
        _walkAskedAt = now;
        _walkAsks++;
        if (_walkAsks > 3)
        {
            Status = string.Create(CultureInfo.CurrentCulture,
                $"The training area (X {cx:0}, Y {cy:0}) is {away:0} away and the walk there did not get there: walk there (a script), or click Train here.");
            if (_walkAsks == 4 && _setup.ReturnWhenCannotContinue)
                ReturnToTown("cannot get to the training area");
            return;
        }
        Status = string.Create(CultureInfo.CurrentCulture, $"The training area (X {cx:0}, Y {cy:0}) is {away:0} away: walking there.");
        if (!WalkToArea())
        {
            Status = string.Create(CultureInfo.CurrentCulture,
                $"The training area (X {cx:0}, Y {cy:0}) is {away:0} away: walk there first (a walking script, or the town loop's training script), or click Train here.");
            _walkAsks = 4;
        }
        else
            _logger.LogInformation("Attack: the training area is {Away:0} away: walking there", away);
    }

    /// <summary>Conditions tab: switch to the area of the character's level.</summary>
    private void CheckConditions(Character c)
    {
        if (_setup.Conditions.Count == 0 || AttackPlan.AreaForLevel(_setup.Conditions, c.Level) is not { } name
            || string.Equals(name, _setup.Enabled, StringComparison.OrdinalIgnoreCase))
            return;
        if (SwitchArea(name))
        {
            _logger.LogInformation("Training: level {Level}: switching to the training area {Area}", c.Level, name);
            _target = 0;
            _walkAsks = 0;
        }
        else if (_notes.Add("no area " + name))
            _logger.LogInformation("Training: the condition's training area {Area} is not in the list", name);
    }

    private static void Forget(ConcurrentDictionary<uint, DateTime> seen, DateTime before)
    {
        foreach (var (id, at) in seen)
            if (at < before)
                seen.TryRemove(id, out _);
    }

    /// <summary>The monsters that may be attacked: alive, near, in the area (or attacking you), not someone else's.</summary>
    private List<Mob> Monsters(Position here)
    {
        var list = new List<Mob>();
        var map = _setup.CollisionDetection ? Map() : null;
        foreach (var m in _client.World.Mobs.Values)
        {
            if (m.LifeState == LifeState.Dead || _ignored.ContainsKey(m.UniqueId))
                continue;
            if (_attackingMe.ContainsKey(m.UniqueId) || _attackingParty.ContainsKey(m.UniqueId))
            {
                list.Add(m);
                continue;
            }
            var at = m.EstimatePosition();
            if (at.DistanceTo(here) > _settings.Range || !_area.Contains(at.PosX, at.PosY))
                continue;
            if (!_settings.KillSteal && _othersFight.ContainsKey(m.UniqueId))
                continue;
            if (map is not null && !here.InDungeon && !PathFinder.IsClear(map, here, at))
                continue;
            list.Add(m);
        }
        return list;
    }

    /// <summary>The monster attacked now, when it is still worth it; else null (choose another).</summary>
    private Mob? Current(List<Mob> monsters, Position here)
    {
        if (_target == 0)
            return null;
        if (!_client.World.Mobs.TryGetValue(_target, out var mob) || mob.LifeState == LifeState.Dead)
        {
            _target = 0;
            return null;
        }
        var now = DateTime.UtcNow;
        if (now - _progress > StuckAfter)
        {
            Ignore(mob.UniqueId, $"no damage for {StuckAfter.TotalSeconds:0} seconds");
            return null;
        }
        if (!monsters.Contains(mob))
        {
            _target = 0;
            return null;
        }
        if (_settings.SwitchAfterDots > 0 && _dots >= _settings.SwitchAfterDots
            && monsters.Where(m => m.UniqueId != mob.UniqueId).OrderBy(m => m.Position.DistanceTo(here)).FirstOrDefault() is { } nextOne)
        {
            _target = nextOne.UniqueId;
            _next = 0;
            _dots = 0;
            _progress = now;
            return nextOne;
        }
        if (_protection.SwitchToPreferred && !_attackingMe.ContainsKey(mob.UniqueId)
            && MonsterPreferences.BetterThan(_protection.MonsterPreferences, mob, monsters, here) is { } better)
        {
            _logger.LogInformation("Attack: switching from {Old} to the more preferred {New}", mob.Name, better.Name);
            _target = better.UniqueId;
            _next = 0;
            _dots = 0;
            _failures = 0;
            _progress = now;
            return better;
        }
        return mob;
    }

    private int Slow => _settings.Lagtastic ? 0 : _settings.SlowerAttack ? 700 : 150;

    private void Attack(Character c, Mob mob, List<Skill> skills, byte weapon, Position here, DateTime now)
    {
        string type = AttackPlan.TypeOf(mob.MobType);
        var (listType, lines) = AttackPlan.SkillsFor(_settings, type);
        if (_settings.Imbues.TryGetValue(listType, out string? imbueLine) && imbueLine.Length > 0
            && AttackPlan.Learned(skills, imbueLine) is { } imbue && !AttackPlan.IsOn(c, imbue) && AttackPlan.Ready(imbue, c.Mp, weapon))
        {
            if (Send(ClientPackets.Cast(imbue.Id)))
                _busyUntil = now + TimeSpan.FromMilliseconds(Math.Max(imbue.Info.CastTime, 0) + 250);
            Status = $"Attacking {mob.Name} ({type}): imbue {imbue.Name}.";
            return;
        }
        // Teleport skills: jump next to a far monster.
        var at = mob.EstimatePosition();
        double distance = at.DistanceTo(here);
        if (_settings.UseTeleportSkills && distance > 25
            && skills.Where(k => AttackPlan.IsTeleport(k.Info) && AttackPlan.Ready(k, c.Mp, weapon)).MaxBy(k => k.Info.SkillLevel) is { } jump)
        {
            if (Send(ClientPackets.CastAt(jump.Id, at)))
                _busyUntil = now + TimeSpan.FromMilliseconds(Math.Max(jump.Info.CastTime, 300) + 200);
            if (_notes.Add("teleport"))
                _logger.LogInformation("Attack: {Skill} to {Monster} ({Distance:0} away)", jump.Name, mob.Name, distance);
            Status = $"Attacking {mob.Name} ({type}): {jump.Name} to it.";
            return;
        }
        if (lines.Count > 0 && AttackPlan.NextSkill(lines, skills, c.Mp, weapon, _settings.CastInOrder, ref _next) is { } skill)
        {
            if (Send(ClientPackets.Attack(mob.UniqueId, skill.Id)))
            {
                _busyUntil = now + TimeSpan.FromMilliseconds(Math.Max(skill.Info.CastTime, _settings.Lagtastic ? 150 : 300) + Slow);
                if (AttackPlan.IsDot(skill.Info))
                    _dots++;
            }
            Status = $"Attacking {mob.Name} ({type}): {skill.Name}. Kills: {Kills}.";
            return;
        }
        // No skill ready (or none set): the common attack, which goes on by itself until the target changes.
        if (now - _commonSent > TimeSpan.FromSeconds(_settings.Lagtastic ? 1.5 : 3))
        {
            if (Send(ClientPackets.Attack(mob.UniqueId, 1)))
                _commonSent = now;
            _busyUntil = now + TimeSpan.FromMilliseconds(400 + Slow);
        }
        if (lines.Count == 0 && _notes.Add("no skills " + type))
            _logger.LogInformation("Attack: no skills set for {Type} monsters: the common attack is used", type);
        Status = $"Attacking {mob.Name} ({type}): common attack. Kills: {Kills}.";
    }

    /// <summary>Casts the first buff of the list that is off and ready (switching weapons when it needs another one).</summary>
    private bool CastBuff(Character c, List<Skill> skills, byte weapon, List<string> lines, string what)
    {
        var now = DateTime.UtcNow;
        var learnedOnes = new List<Skill>();
        foreach (string line in lines)
        {
            if (AttackPlan.Learned(skills, line) is not { } skill)
            {
                if (_notes.Add("not learned " + line))
                    _logger.LogInformation("Attack: the {What} {Skill} is not learned by this character", what, _client.Data.GetSkill(line + "_01")?.Name ?? line);
                continue;
            }
            learnedOnes.Add(skill);
        }
        // "Recast all buffs when one buff ends": one that was on is off now -> all of them may be cast again.
        if (_settings.RecastAllWhenOneEnds && what == "buff")
            RecastAll(c, learnedOnes);
        foreach (var skill in learnedOnes)
        {
            if (AttackPlan.IsOn(c, skill)
                || (_buffSent.TryGetValue(skill.Id, out var sent) && now - sent < TimeSpan.FromMilliseconds(skill.Info.CastTime + 4000))
                || (_buffBlocked.TryGetValue(skill.Id, out var until) && now < until)
                || !skill.IsCastingEnabled || c.Mp < skill.Info.Mana)
                continue;
            if (!AttackPlan.WeaponFits(skill.Info, weapon))
            {
                if (_settings.SwitchWeaponsForBuffs && SwitchWeaponFor(c, skill, now))
                    return true;
                continue;
            }
            return CastSelf(c, skill, what, now);
        }
        return false;
    }

    private readonly HashSet<uint> _wasOn = [];

    private void RecastAll(Character c, List<Skill> buffs)
    {
        bool ended = false;
        foreach (var b in buffs)
        {
            bool on = AttackPlan.IsOn(c, b);
            if (on)
                _wasOn.Add(b.Id);
            else if (_wasOn.Remove(b.Id))
                ended = true;
        }
        if (!ended)
            return;
        _logger.LogInformation("Attack: a buff ended: recasting all the buffs");
        foreach (var b in buffs)
            _buffSent.Remove(b.Id);
    }

    private bool CastSelf(Character c, Skill skill, string what, DateTime now, uint target = 0)
    {
        uint on = target != 0 ? target : skill.Info.TargetRequired ? c.UniqueId : 0;
        if (!Send(ClientPackets.Cast(skill.Id, on)))
            return false;
        _buffSent[skill.Id] = now;
        (_lastBuff, _lastBuffAt) = (skill.Id, now);
        _busyUntil = now + TimeSpan.FromMilliseconds(Math.Max(skill.Info.CastTime, 0) + 500);
        Status = $"Casting the {what} {skill.Name}.";
        _logger.LogInformation("Attack: {What} {Skill}", what, skill.Name);
        return true;
    }

    /// <summary>Nothing to attack: walk around in the area (or back to its middle when it was left).</summary>
    private void WalkAround(Character c, Position here, DateTime now)
    {
        var (x, y) = _area.Center;
        bool inside = _area.Contains(here.PosX, here.PosY);
        if (inside && _setup.DontWalkAround)
        {
            Status = $"No monster to attack. Kills: {Kills}.";
            return;
        }
        string status = inside ? $"No monster near: walking around. Kills: {Kills}." : "No monster near: walking back to the training area.";
        if (now - _walkSent < TimeSpan.FromSeconds(inside ? 5 : 3))
        {
            Status = status;
            return;
        }
        if (inside)
            (x, y) = AttackPlan.RandomPoint(_area, _random);
        var to = Position.IsDungeonRegion(_area.Region) ? Position.FromWorld(x, y, _area.Region) : new Position(x, y);
        if (Map() is { } map && _setup.NavigateAroundObstacles && !here.InDungeon && !PathFinder.IsClear(map, here, to)
            && PathFinder.Find(map, here, to) is { Count: > 0 } route)
            to = route[0];
        if (Send(ClientPackets.MoveTo(to, c.IsRiding ? c.RidingUniqueId : 0)))
            _walkSent = now;
        Status = status;
    }

    private DateTime _selectSent;

    /// <summary>"Auto select nearby uniques / titans" (only when not botting): keeps the nearest one selected.</summary>
    private void AutoSelect()
    {
        if (!(_settings.AutoSelectUniques || _settings.AutoSelectTitans) || Paused() || !_client.IsInGame || _client.World.Character is not { } c)
            return;
        var now = DateTime.UtcNow;
        if (now - _selectSent < TimeSpan.FromSeconds(2))
            return;
        var here = c.EstimatePosition();
        var near = _client.World.Mobs.Values.Where(m => m.LifeState != LifeState.Dead && m.EstimatePosition().DistanceTo(here) < 60
                && ((_settings.AutoSelectUniques && AttackPlan.TypeOf(m.MobType) == "Unique") || (_settings.AutoSelectTitans && AttackPlan.TypeOf(m.MobType) == "Titan")))
            .OrderBy(m => m.Position.DistanceTo(here)).FirstOrDefault();
        if (near is null || _client.World.SelectedEntityId == near.UniqueId)
            return;
        _client.Send(ClientPackets.SelectEntity(near.UniqueId));
        _selectSent = now;
    }

    private bool Send(NexBot.Protocol.Packet packet)
    {
        if (_client.Send(packet))
            return true;
        Status = "The bot is not connected.";
        return false;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                Step();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or IndexOutOfRangeException or KeyNotFoundException)
            {
                _logger.LogWarning("Attack: {Message}", ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _client.DamageDealt -= OnDamage;
        _client.AttackFailed -= OnAttackFailed;
        _client.PetitionReceived -= OnPetition;
        _client.PacketSent -= OnPacketSent;
    }
}
