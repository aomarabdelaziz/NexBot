using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>Consumables the protection looks for, by the game's own item type IDs.</summary>
public enum Supply { HpPotion, MpPotion, Vigor, UniversalPill, PurificationPill, Arrow, Bolt, SpeedScroll, PetRecoveryKit, PetRevive, PetFeed, PetAbnormal, EnergyOfLife, ResurrectionScroll }

/// <summary>Which item is which supply (type IDs from the game's item data, the same on every vSRO server).</summary>
public static class ProtectionCatalog
{
    /// <summary>
    /// The buff skill a scroll gives, from its server name (pure: tested): SKILL_ + the item's code + _01
    /// (ITEM_MALL_DAMAGE_ABS_20P_SCROLL → SKILL_MALL_DAMAGE_ABS_20P_SCROLL_01); null when the game data has none.
    /// </summary>
    public static Data.SkillInfo? ScrollSkill(Func<string, Data.SkillInfo?> skill, string itemServerName)
    {
        if (!itemServerName.StartsWith("ITEM_", StringComparison.OrdinalIgnoreCase))
            return null;
        string code = "SKILL_" + itemServerName[5..];
        return skill(code + "_01") ?? skill(code);
    }

    /// <summary>A buff's effect (the code after the duration: "hste" for every speed buff ...), or null.</summary>
    public static long? BuffEffect(Data.SkillInfo info)
    {
        var values = info.Params.Split('|');
        return values.Length > 4 && values[2] == "1685418593" // "dura": a buff with a duration
            && long.TryParse(values[4], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long effect) && effect != 0
            ? effect : null;
    }

    /// <summary>
    /// The active buff that makes a scroll useless (pure: tested): the same skill, the same skill group, or the same
    /// effect (any speed buff for a speed scroll). The server refuses the scroll then (0x0002), or it would be wasted.
    /// </summary>
    public static Buff? SameBuff(IEnumerable<Buff> active, Data.SkillInfo skill)
    {
        long? effect = BuffEffect(skill);
        return active.FirstOrDefault(b => b.Id == skill.Id || (skill.GroupId != 0 && b.GroupId == skill.GroupId)
            || (effect is not null && BuffEffect(b.Info) == effect));
    }

    public static bool Is(Item item, Supply supply) => supply switch
    {
        Supply.HpPotion => item.IsType(3, 1, 1),
        Supply.MpPotion => item.IsType(3, 1, 2),
        Supply.Vigor => item.IsType(3, 1, 3),
        Supply.PetRecoveryKit => item.IsType(3, 1, 4),
        Supply.PetRevive => item.IsType(3, 1, 6),
        Supply.PetFeed => item.IsType(3, 1, 9),
        Supply.PurificationPill => item.IsType(3, 2, 1),
        Supply.UniversalPill => item.IsType(3, 2, 6),
        Supply.PetAbnormal => item.IsType(3, 2, 7),
        Supply.Arrow => item.IsType(3, 4, 1),
        Supply.Bolt => item.IsType(3, 4, 2),
        Supply.SpeedScroll => item.IsType(3, 13, 1) && item.ServerName.Contains("SPEED", StringComparison.Ordinal),
        Supply.EnergyOfLife => item.IsType(3, 13, 14) && item.ServerName.Contains("SECOND_HWAN", StringComparison.Ordinal),
        Supply.ResurrectionScroll => item.IsType(3, 13, 6),
        _ => false,
    };

    /// <summary>Any return scroll (type 3/3/1: normal, special, instant, beginner, quest ...), not the bandit den one.</summary>
    public static bool IsReturnScroll(Item item) =>
        !item.ServerName.Contains("THIEFDEN", StringComparison.Ordinal)
        && ((item.IsType(3, 3, 1) && !item.ServerName.Contains("WINGS", StringComparison.Ordinal)) || AutoWalker.IsReturnScroll(item.ServerName));

    /// <summary>The return scroll to use: an instant one first, then any other; -1 when there is none.</summary>
    public static int ReturnScrollSlot(Character c)
    {
        int any = -1;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
        {
            if (c.Inventory[i] is not { } item || !IsReturnScroll(item))
                continue;
            if (item.ServerName.Contains("HIGH_SPEED", StringComparison.Ordinal) || item.ServerName.EndsWith("RETURN_03", StringComparison.Ordinal)
                || item.Name.Contains("Instant", StringComparison.OrdinalIgnoreCase))
                return i;
            if (any < 0)
                any = i;
        }
        return any;
    }

    /// <summary>Arrows / bolts left: the bag plus the equipped stack.</summary>
    public static int AmmoLeft(Character c, Supply ammo) =>
        Count(c, ammo) + (c.Inventory[ShieldSlot] is { } q && Is(q, ammo) ? Math.Max(1, (int)q.Quantity) : 0);

    /// <summary>How many of the supply are in the bag (stacks added up).</summary>
    public static int Count(Character c, Supply supply)
    {
        int count = 0;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is { } item && Is(item, supply))
                count += Math.Max(1, (int)item.Quantity);
        return count;
    }

    /// <summary>The first bag slot holding the supply (not one of the <paramref name="skip"/> items), or -1.</summary>
    public static int Find(Character c, Supply supply, Func<Item, bool>? skip = null)
    {
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is { } item && Is(item, supply) && skip?.Invoke(item) != true)
                return i;
        return -1;
    }

    public static int FreeSlots(Character c)
    {
        int free = 0;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is null)
                free++;
        return free;
    }

    /// <summary>Arrows for a bow, bolts for a crossbow, nothing for other weapons.</summary>
    public static Supply? AmmoFor(Character c) => c.Inventory[WeaponSlot] switch
    {
        { TypeId3: 6, TypeId4: 12 } => Supply.Bolt,
        { TypeId3: 6, TypeId4: 6 } => Supply.Arrow,
        { } w when w.ServerName.Contains("_CROSSBOW_", StringComparison.Ordinal) => Supply.Bolt,
        { } w when w.ServerName.Contains("_BOW_", StringComparison.Ordinal) => Supply.Arrow,
        _ => null,
    };

    public const int WeaponSlot = 6;
    public const int ShieldSlot = 7;
}

/// <summary>
/// phBot's Protection: potions and pills, returning to town, reviving, stat points and the disconnect rules.
/// Potions, pills, stats and disconnects work all the time. Returning to town is checked while the bot is busy (walking a
/// script, trading or, later, hunting) and, with <see cref="ProtectionSettings.ReturnOutsideTown"/>, whenever the
/// character is outside a town (no shop NPC near for <see cref="OutsideTownDelay"/>).
/// </summary>
public sealed class ProtectionManager : IDisposable
{
    private readonly SroClient _client;
    private readonly ProtectionSettings _settings;
    private readonly TrainingAreaSettings _area;
    private readonly Func<bool> _isBotting;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<Supply, DateTime> _lastUse = [];

    /// <summary>Potions the server refused (by server name): not used again until the time (another stack is used).</summary>
    private readonly Dictionary<string, DateTime> _refusedUntil = new(StringComparer.Ordinal);

    private static readonly TimeSpan RefusedWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Extra wait per kind learned from cooldown refusals (0x185B): the server's potion delay is longer than the
    /// Protection page's delay. Grows by 250 ms a refusal, up to 5 s.
    /// </summary>
    private readonly Dictionary<Supply, int> _extraDelayMs = [];
    private readonly DateTime _sessionStart = DateTime.UtcNow;
    private DateTime _botStart, _lastAttacked, _lastReturn, _diedAt, _lastStat, _lastRevive;
    /// <summary>Scroll list notes logged once: "missing:part" (nothing in the bag matches), "refused:server name".</summary>
    private readonly HashSet<string> _scrollNotes = [];
    private string _typeHitName = "";
    private static readonly TimeSpan ScrollsAfterEntering = TimeSpan.FromSeconds(8);
    private Character? _scrollCharacter;
    private DateTime _scrollsFrom;
    private DateTime _outsideSince, _returnDisconnectStart, _lastBerserk, _typeHitAt, _resTriedAt, _lastSwap, _lastThief, _lastScrollCheck;
    private readonly Dictionary<uint, DateTime> _attackers = [];
    private readonly Dictionary<string, uint> _scrollBuff = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _scrollUsed = new(StringComparer.Ordinal);
    private (string Key, HashSet<uint> Before, DateTime At)? _scrollPending;
    private bool _disconnectAfterReturn, _respawnTransport;
    private HashSet<string>? _shopNpcs;
    private bool _wasBotting, _transportDied, _noScrollWarned, _disconnecting;
    private int _deaths, _strGiven, _intGiven, _returnedHour = -1;
    private DateOnly _returnedDay;

    public ProtectionManager(SroClient client, ProtectionSettings settings, TrainingAreaSettings area, Func<bool> isBotting,
        ILogger? logger = null, bool startLoop = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _area = area ?? throw new ArgumentNullException(nameof(area));
        _isBotting = isBotting ?? (() => false);
        _logger = logger ?? NullLogger.Instance;
        _botStart = _lastAttacked = DateTime.UtcNow;
        client.DamageDealt += OnDamage;
        client.CharacterDied += OnDied;
        client.PetDied += OnPetDied;
        if (startLoop)
            _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Stop the running automation (scripts, trade, alchemy), with the reason.</summary>
    public event Action<string>? StopRequested;
    /// <summary>Stop the bot (disconnect), with the reason.</summary>
    public event Action<string>? DisconnectRequested;
    /// <summary>A return scroll was used or the character revived in town (the town loop can start).</summary>
    public event Action? ReturnedToTown;

    /// <summary>
    /// Asks for a return to town (the attack pet died): done by the next pass right away, botting or not (the
    /// player turned the option on); dropped when already in town.
    /// </summary>
    public void RequestReturn(string reason) => _requestedReturn = reason;

    private string? _requestedReturn;

    /// <summary>Deaths this session.</summary>
    public int Deaths => _deaths;

    /// <summary>True while a script is walked (berserk "while walking the script").</summary>
    public Func<bool> IsWalkingScript { get; set; } = () => false;

    /// <summary>A shop NPC within this distance means the character is in a town.</summary>
    public const double TownRadius = 250;

    /// <summary>How long the character must be away from town before the return checks start (teleports load NPCs late).</summary>
    public TimeSpan OutsideTownDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>True when a shop NPC (from the database's shops; any NPC_ model without it) is near the character.</summary>
    public bool InTown()
    {
        if (_client.World.Character is not { } c)
            return false;
        _shopNpcs ??= _client.Data.GetShopCatalog().Select(x => x.Npc).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var here = c.Position;
        foreach (var n in _client.World.Npcs.Values)
        {
            if (n is Mob || n.IsCos)
                continue;
            bool shop = _shopNpcs.Count > 0
                ? _shopNpcs.Contains(n.ServerName)
                : n.ServerName.StartsWith("NPC_", StringComparison.OrdinalIgnoreCase);
            if (shop && n.Position.InDungeon == here.InDungeon && n.Position.DistanceTo(here) < TownRadius)
                return true;
        }
        return false;
    }

    /// <summary>Why the return checks are on or off right now (null = off).</summary>
    private string? ChecksReason(DateTime now, out string offReason)
    {
        offReason = "";
        if (_isBotting())
        {
            _outsideSince = default;
            return "the bot is busy";
        }
        if (!_settings.ReturnOutsideTown)
        {
            offReason = "no script or trade runs";
            return null;
        }
        if (InTown())
        {
            _outsideSince = default;
            offReason = "in town";
            return null;
        }
        if (_outsideSince == default)
            _outsideSince = now;
        if (now - _outsideSince < OutsideTownDelay)
        {
            offReason = "just left town";
            return null;
        }
        return "outside town";
    }

    /// <summary>
    /// The berserk monster types of the Protection page: one or more of General, Champion, Giant, Titan, Strong,
    /// Elite, Unique, Party, comma-separated (see <see cref="MonsterPreferences.IsType"/>).
    /// </summary>
    public static bool IsBerserkType(MobType type, string wanted) => MonsterPreferences.IsAnyType(type, wanted);

    private void OnDamage(object? sender, DamageEventArgs e)
    {
        if (_client.World.Character is not { } c)
            return;
        if (e.SourceId == c.UniqueId && _settings.BerserkOnMonsterType && _client.World.GetEntity(e.TargetId) is Mob target
            && IsBerserkType(target.MobType, _settings.BerserkMonsterType))
            (_typeHitAt, _typeHitName) = (DateTime.UtcNow, $"{target.Name} ({MonsterPreferences.TypeName(target.MobType)})");
        if (e.TargetId != c.UniqueId)
            return;
        _lastAttacked = DateTime.UtcNow;
        if (_client.World.GetEntity(e.SourceId) is Mob mob)
        {
            lock (_attackers)
                _attackers[mob.UniqueId] = _lastAttacked;
            if (_settings.BerserkOnMonsterType && IsBerserkType(mob.MobType, _settings.BerserkMonsterType))
                (_typeHitAt, _typeHitName) = (_lastAttacked, $"{mob.Name} ({MonsterPreferences.TypeName(mob.MobType)})");
        }
        if (_settings.DisconnectWhenAttackedByPlayer && _isBotting() && _client.World.GetEntity(e.SourceId) is Player)
            Disconnect($"attacked by the player {e.SourceName}");
    }

    private void OnDied(object? sender, CharacterDiedEventArgs e)
    {
        _deaths++;
        _diedAt = DateTime.UtcNow;
        _logger.LogWarning("Protection: the character died ({Deaths} this session{Killer})", _deaths,
            e.KillerName is { Length: > 0 } k ? ", killed by " + k : "");
        if (_settings.StopAfterDeaths.On && _deaths > _settings.StopAfterDeaths.Value)
            StopRequested?.Invoke($"died {_deaths} times");
    }

    private void OnPetDied(object? sender, EntityEventArgs e)
    {
        if (e.Entity is Cos { IsTransport: true })
            _transportDied = _respawnTransport = true;
    }

    private void Disconnect(string reason)
    {
        if (_disconnecting)
            return;
        _disconnecting = true;
        _logger.LogWarning("Protection: disconnecting ({Reason})", reason);
        DisconnectRequested?.Invoke(reason);
    }

    /// <summary>One pass (the loop runs it 4 times a second; tests call it directly).</summary>
    public async Task StepAsync(CancellationToken token)
    {
        if (!_client.IsInGame || _client.World.Character is not { } c)
            return;
        var now = DateTime.UtcNow;
        string? on = ChecksReason(now, out string off);
        bool botting = on is not null;
        if (botting != _wasBotting)
        {
            _botStart = now;
            _lastAttacked = now;
            if (botting)
                _logger.LogInformation("Protection: return-to-town checks on ({Reason})", on);
            else
                _logger.LogInformation("Protection: return-to-town checks paused ({Reason})", off);
        }
        else if (!botting)
            _lastAttacked = now; // "not attacked" counts only while the checks are on
        if (botting && !_wasBotting)
            _returnDisconnectStart = now;
        _wasBotting = botting;

        DisconnectChecks(c, now);
        if (_disconnectAfterReturn && c.Scrolling == ScrollingType.None && InTown())
            Disconnect("returned to town to disconnect");
        if (_disconnecting)
            return;

        if (c.LifeState == LifeState.Dead)
        {
            Revive(c, now);
            return;
        }
        _diedAt = default;
        _resTriedAt = default;

        await PotionsAsync(c, now, token).ConfigureAwait(false);
        Stats(c, now);
        Berserk(c, now);
        await ScrollsAsync(c, now, botting, token).ConfigureAwait(false);
        Transport();
        ThiefInTown(now);
        if (await SwapWeaponAsync(c, now, token).ConfigureAwait(false))
            return; // the new weapon arrives with the next packets: check the return reasons after it

        if (Interlocked.Exchange(ref _requestedReturn, null) is { } requested)
        {
            if (InTown())
                _logger.LogInformation("Protection: no return needed ({Reason}): already in town", requested);
            else if (c.Scrolling != ScrollingType.None)
                _logger.LogInformation("Protection: no return needed ({Reason}): already returning", requested);
            else
            {
                await ReturnAsync(c, requested, token).ConfigureAwait(false);
                return;
            }
        }

        if (botting && c.Scrolling == ScrollingType.None && now - _lastReturn > TimeSpan.FromMinutes(1))
        {
            string? reason = ReturnReason(c, now);
            if (reason is not null)
                await ReturnAsync(c, reason, token).ConfigureAwait(false);
        }
    }

    private void DisconnectChecks(Character c, DateTime now)
    {
        if (_settings.DisconnectOnGm && _client.World.Players.Values.FirstOrDefault(SoundAlerts.IsGameMaster) is { } gm)
            Disconnect($"a GM is near ({gm.Name})");
        else if (_settings.DisconnectOnMurderStatus && c.PvpState == PvpState.PlayerKiller)
            Disconnect("murder status");
        else if (_settings.DisconnectEveryMinutes.On && _settings.DisconnectEveryMinutes.Value > 0
                 && now - _sessionStart >= TimeSpan.FromMinutes(_settings.DisconnectEveryMinutes.Value))
            Disconnect($"{_settings.DisconnectEveryMinutes.Value} minutes in game");
    }

    // ---------------------------------------------------------------- potions

    /// <summary>The supply to use now for HP / MP / bad status, or null (pure: tested).</summary>
    public static Supply? PotionNeeded(Character c, ProtectionSettings s)
    {
        if (c.BadStatus != BadStatus.None)
        {
            if (s.UniversalPill && ProtectionCatalog.Find(c, Supply.UniversalPill) >= 0)
                return Supply.UniversalPill;
            if (s.PurificationPill && ProtectionCatalog.Find(c, Supply.PurificationPill) >= 0)
                return Supply.PurificationPill;
        }
        int hp = c.HpPercent, mp = c.MpPercent;
        if (c.HpMax == 0)
            return null;
        if (s.VigorHp.On && hp < s.VigorHp.Value && ProtectionCatalog.Find(c, Supply.Vigor) >= 0)
            return Supply.Vigor;
        if (s.VigorMp.On && c.MpMax > 0 && mp < s.VigorMp.Value && ProtectionCatalog.Find(c, Supply.Vigor) >= 0)
            return Supply.Vigor;
        if (s.HpPotion.On && hp < s.HpPotion.Value && ProtectionCatalog.Find(c, Supply.HpPotion) >= 0)
            return Supply.HpPotion;
        if (s.MpPotion.On && c.MpMax > 0 && mp < s.MpPotion.Value && ProtectionCatalog.Find(c, Supply.MpPotion) >= 0)
            return Supply.MpPotion;
        return null;
    }

    private async Task PotionsAsync(Character c, DateTime now, CancellationToken token)
    {
        // Several kinds can be due at once (HP and MP): use each one that is ready.
        for (int round = 0; round < 3; round++)
        {
            var supply = PotionNeeded(c, _settings);
            if (supply is not { } kind)
                return;
            var delay = TimeSpan.FromMilliseconds(Math.Max(200, _settings.PotionDelayMs) + _extraDelayMs.GetValueOrDefault(kind));
            if (_lastUse.TryGetValue(kind, out var last) && now - last < delay)
                return;
            int slot = ProtectionCatalog.Find(c, kind, item => _refusedUntil.TryGetValue(item.ServerName, out var until) && now < until);
            if (slot < 0)
                return;
            _lastUse[kind] = now;
            string name = c.Inventory[slot]?.ServerName ?? "";
            var result = await _client.UseItemAsync((byte)slot, timeout: TimeSpan.FromSeconds(2), cancellationToken: token).ConfigureAwait(false);
            _logger.LogDebug("Protection: {Kind} ({Result})", kind, result.Ok ? "used" : result.Message);
            if (result.ErrorCode == NexBot.Sessions.ItemErrors.CooldownError)
            {
                // Too soon for the server: wait a little longer between potions of this kind from now on.
                int extra = Math.Min(5000, _extraDelayMs.GetValueOrDefault(kind) + 250);
                if (extra != _extraDelayMs.GetValueOrDefault(kind))
                    _logger.LogDebug("Protection: {Kind} cooldown not over: {Delay} ms between them now", kind, Math.Max(200, _settings.PotionDelayMs) + extra);
                _extraDelayMs[kind] = extra;
                return;
            }
            if (!result.Ok && result.ErrorCode != 0 && name.Length > 0)
            {
                // The server refuses this item (not a lost answer): use other stacks of the kind, try it again later.
                bool first = !_refusedUntil.ContainsKey(name);
                _refusedUntil[name] = now + RefusedWait;
                if (first)
                    _logger.LogWarning("Protection: the server refuses {Item} ({Message}): other {Kind} stacks are used, this one again every {Seconds:0} s",
                        name, result.Message, kind, RefusedWait.TotalSeconds);
                continue;
            }
            if (result.Ok)
                _refusedUntil.Remove(name);
            if (PotionNeeded(c, _settings) == kind)
                return; // the same kind again: wait for its delay
        }
    }

    // ---------------------------------------------------------------- stats

    /// <summary>True when the next stat point goes to STR, for STR points out of every 3 (pure: tested).</summary>
    public static bool NextStatIsStr(int strPerLevel, int strGiven, int intGiven)
    {
        strPerLevel = Math.Clamp(strPerLevel, 0, 3);
        if (strPerLevel == 3)
            return true;
        if (strPerLevel == 0)
            return false;
        // Keep STR : total at strPerLevel : 3.
        return strGiven * 3 < (strGiven + intGiven + 1) * strPerLevel;
    }

    private void Stats(Character c, DateTime now)
    {
        if (!_settings.AutoStats || c.StatPoints == 0 || now - _lastStat < TimeSpan.FromMilliseconds(600))
            return;
        _lastStat = now;
        bool str = NextStatIsStr(_settings.StrPerLevel, _strGiven, _intGiven);
        _logger.LogInformation("Protection: auto stats: +1 {Stat} ({Points} free)", str ? "STR" : "INT", c.StatPoints);
        AddStat(str);
        if (str)
            _strGiven++;
        else
            _intGiven++;
    }

    /// <summary>Adds one stat point to STR or INT (the Protection page's buttons use it too).</summary>
    public void AddStat(bool strength) => _client.Send(ClientPackets.AddStat(strength));

    // ---------------------------------------------------------------- dead

    private void Revive(Character c, DateTime now)
    {
        if (_diedAt == default)
            _diedAt = now;
        if (!_settings.DeadReturn.On || now - _lastRevive < TimeSpan.FromSeconds(15))
            return;
        var pos = c.Position;
        bool outside = _settings.DeadOutsideAreaNow && _area.IsSet && !_area.Contains(pos.PosX, pos.PosY);
        if (!outside && now - _diedAt < TimeSpan.FromMinutes(Math.Max(0, _settings.DeadReturn.Value)))
            return;
        _lastRevive = now;
        if (_settings.UseResurrectionScroll && _resTriedAt == default && ProtectionCatalog.Find(c, Supply.ResurrectionScroll) >= 0)
        {
            _resTriedAt = now;
            _logger.LogInformation("Protection: dead: using a resurrection scroll");
            _client.Send(ClientPackets.ReviveHere());
            return; // still dead 15 s later: town
        }
        _logger.LogInformation("Protection: dead{Outside}: reviving in town", outside ? " outside the training area" : "");
        _client.Send(ClientPackets.ReviveInTown());
        StopRequested?.Invoke("dead: revived in town");
        ReturnedToTown?.Invoke();
    }

    // ---------------------------------------------------------------- return

    /// <summary>Why the character should go back to town now, or null (the supply and gear checks are pure: tested).</summary>
    public static string? SupplyReason(Character c, ProtectionSettings s)
    {
        if (s.HpPotionsLeft.On && ProtectionCatalog.Count(c, Supply.HpPotion) <= s.HpPotionsLeft.Value)
            return $"{s.HpPotionsLeft.Value} or fewer HP potions";
        if (s.MpPotionsLeft.On && ProtectionCatalog.Count(c, Supply.MpPotion) <= s.MpPotionsLeft.Value)
            return $"{s.MpPotionsLeft.Value} or fewer MP potions";
        if (s.OutOfUniversalPills && ProtectionCatalog.Count(c, Supply.UniversalPill) == 0)
            return "out of universal pills";
        if (s.OutOfVigors && ProtectionCatalog.Count(c, Supply.Vigor) == 0)
            return "out of vigor potions";
        if (s.OutOfArrows && ProtectionCatalog.AmmoFor(c) is { } ammo && ProtectionCatalog.AmmoLeft(c, ammo) == 0)
            return ammo == Supply.Arrow ? "out of arrows" : "out of bolts";
        if (s.OutOfEnergyOfLife && ProtectionCatalog.Count(c, Supply.EnergyOfLife) == 0)
            return "out of Energy of Life";
        if (s.OutOfSpeedScrolls && ProtectionCatalog.Count(c, Supply.SpeedScroll) == 0)
            return "out of speed scrolls";
        if (s.InventoryFull.On && ProtectionCatalog.FreeSlots(c) <= s.InventoryFull.Value)
            return "the inventory is full";
        if (s.WeaponDurability.On && c.Inventory[ProtectionCatalog.WeaponSlot] is Equipable w && w.Durability < s.WeaponDurability.Value)
            return $"weapon durability {w.Durability}";
        if (s.ShieldDurability.On && c.Inventory[ProtectionCatalog.ShieldSlot] is Equipable sh && sh.Durability < s.ShieldDurability.Value)
            return $"shield durability {sh.Durability}";
        if (s.ArmorDurability.On)
            for (int i = 0; i < 6; i++)
                if (c.Inventory[i] is Equipable a && a.Durability < s.ArmorDurability.Value)
                    return $"{a.Name} durability {a.Durability}";
        if (s.PetRecoveryKits && ProtectionCatalog.Count(c, Supply.PetRecoveryKit) == 0)
            return "out of pet recovery kits";
        if (s.PetRevive && ProtectionCatalog.Count(c, Supply.PetRevive) == 0)
            return "out of pet revive items";
        if (s.PetFeed && ProtectionCatalog.Count(c, Supply.PetFeed) == 0)
            return "out of pet food";
        if (s.PetAbnormal && ProtectionCatalog.Count(c, Supply.PetAbnormal) == 0)
            return "out of pet abnormal state potions";
        return null;
    }

    private string? ReturnReason(Character c, DateTime now)
    {
        if (SupplyReason(c, _settings) is { } supply)
            return supply;
        var local = DateTime.Now;
        if (_settings.BeforeNextHour.On && local.Minute >= 60 - Math.Clamp(_settings.BeforeNextHour.Value, 1, 59) && _returnedHour != local.Hour)
        {
            _returnedHour = local.Hour;
            return $"{60 - local.Minute} minutes before {local.Hour + 1:00}:00";
        }
        if (_settings.EveryMinutes.On && _settings.EveryMinutes.Value > 0 && now - _botStart >= TimeSpan.FromMinutes(_settings.EveryMinutes.Value))
        {
            _botStart = now;
            return $"every {_settings.EveryMinutes.Value} minutes";
        }
        if (_settings.DailyAt && TimeOnly.TryParse(_settings.DailyTime, out var at) && _returnedDay != DateOnly.FromDateTime(local)
            && TimeOnly.FromDateTime(local) >= at && TimeOnly.FromDateTime(local) < at.AddMinutes(5))
        {
            _returnedDay = DateOnly.FromDateTime(local);
            return $"daily at {_settings.DailyTime}";
        }
        if (_settings.PartyBelow.On && (_client.World.Party?.Members.Count ?? 0) < _settings.PartyBelow.Value)
            return $"fewer than {_settings.PartyBelow.Value} players in the party";
        if (_settings.NotAttackedMinutes.On && _settings.NotAttackedMinutes.Value > 0
            && now - _lastAttacked >= TimeSpan.FromMinutes(_settings.NotAttackedMinutes.Value))
        {
            _lastAttacked = now;
            return $"not attacked for {_settings.NotAttackedMinutes.Value} minutes";
        }
        if (_settings.UniqueNear && _client.World.Mobs.Values.FirstOrDefault(m => m.MobType.IsUnique()) is { } unique)
            return $"the unique {unique.Name} is near";
        if (_settings.TransportDied && _transportDied)
        {
            _transportDied = false;
            return "the job transport died";
        }
        if (_settings.PinkStatus && c.PvpState == PvpState.Assaulter)
            return "pink status";
        if (_settings.ReturnDisconnectMinutes.On && _settings.ReturnDisconnectMinutes.Value > 0
            && now - _returnDisconnectStart >= TimeSpan.FromMinutes(_settings.ReturnDisconnectMinutes.Value))
        {
            _returnDisconnectStart = now;
            _disconnectAfterReturn = true;
            return $"return and disconnect every {_settings.ReturnDisconnectMinutes.Value} minutes";
        }
        return null;
    }

    /// <summary>The Protection page's "Check now": every enabled return option, its value now and whether it matches.</summary>
    public IReadOnlyList<string> Explain()
    {
        var lines = new List<string>();
        if (_client.World.Character is not { } c)
            return ["Not in game."];
        var s = _settings;
        var now = DateTime.UtcNow;
        var local = DateTime.Now;
        string? on = ChecksReason(now, out string off);
        lines.Add(on is not null ? $"Checks are ON ({on})." : $"Checks are PAUSED ({off}).");
        lines.Add(InTown() ? "In town: a shop NPC is near." : "Outside town: no shop NPC near.");
        if (c.Scrolling != ScrollingType.None)
            lines.Add("A scroll is being used.");
        int scrollSlot = ProtectionCatalog.ReturnScrollSlot(c);
        lines.Add(scrollSlot >= 0 ? $"Return scroll: {c.Inventory[scrollSlot]!.Name} (slot {scrollSlot})." : "Return scroll: NONE in the bag (the bot would only stop).");
        lines.Add("");
        int header = lines.Count;
        void Add(bool enabled, string name, bool match, string value)
        {
            if (enabled)
                lines.Add($"{(match ? "[RETURN]" : "[ ok ]")}  {name}: {value}");
        }
        int hp = ProtectionCatalog.Count(c, Supply.HpPotion), mp = ProtectionCatalog.Count(c, Supply.MpPotion);
        Add(s.HpPotionsLeft.On, $"HP potions left <= {s.HpPotionsLeft.Value}", hp <= s.HpPotionsLeft.Value, $"{hp} in the bag");
        Add(s.MpPotionsLeft.On, $"MP potions left <= {s.MpPotionsLeft.Value}", mp <= s.MpPotionsLeft.Value, $"{mp} in the bag");
        int n = ProtectionCatalog.Count(c, Supply.UniversalPill);
        Add(s.OutOfUniversalPills, "Out of universal pills", n == 0, $"{n} in the bag");
        n = ProtectionCatalog.Count(c, Supply.Vigor);
        Add(s.OutOfVigors, "Out of vigor potions", n == 0, $"{n} in the bag");
        if (s.OutOfArrows)
        {
            if (ProtectionCatalog.AmmoFor(c) is { } ammo)
            {
                n = ProtectionCatalog.AmmoLeft(c, ammo);
                Add(true, "Out of arrows / bolts", n == 0, $"{n} left, bag + equipped ({(ammo == Supply.Arrow ? "bow" : "crossbow")})");
            }
            else
                Add(true, "Out of arrows / bolts", false, "no bow or crossbow worn");
        }
        n = ProtectionCatalog.Count(c, Supply.EnergyOfLife);
        Add(s.OutOfEnergyOfLife, "Out of Energy of Life", n == 0, $"{n} in the bag");
        n = ProtectionCatalog.Count(c, Supply.SpeedScroll);
        Add(s.OutOfSpeedScrolls, "Out of speed scrolls", n == 0, $"{n} in the bag");
        n = ProtectionCatalog.FreeSlots(c);
        Add(s.InventoryFull.On, $"Free inventory slots <= {s.InventoryFull.Value}", n <= s.InventoryFull.Value, $"{n} free");
        if (s.WeaponDurability.On)
            Add(true, $"Weapon durability < {s.WeaponDurability.Value}",
                c.Inventory[ProtectionCatalog.WeaponSlot] is Equipable w && w.Durability < s.WeaponDurability.Value,
                c.Inventory[ProtectionCatalog.WeaponSlot] is Equipable w2 ? w2.Durability.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no weapon");
        if (s.ShieldDurability.On)
            Add(true, $"Shield durability < {s.ShieldDurability.Value}",
                c.Inventory[ProtectionCatalog.ShieldSlot] is Equipable h && h.Durability < s.ShieldDurability.Value,
                c.Inventory[ProtectionCatalog.ShieldSlot] is Equipable h2 ? h2.Durability.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no shield");
        if (s.ArmorDurability.On)
        {
            int low = int.MaxValue;
            for (int i = 0; i < 6; i++)
                if (c.Inventory[i] is Equipable a)
                    low = (int)Math.Min(low, a.Durability);
            Add(true, $"Armor durability < {s.ArmorDurability.Value}", low < s.ArmorDurability.Value, low == int.MaxValue ? "no armor" : $"lowest {low}");
        }
        Add(s.BeforeNextHour.On, $"{s.BeforeNextHour.Value} minutes before the next hour",
            local.Minute >= 60 - Math.Clamp(s.BeforeNextHour.Value, 1, 59) && _returnedHour != local.Hour, $"now {local:HH:mm}");
        if (s.EveryMinutes.On)
            Add(true, $"Every {s.EveryMinutes.Value} minutes", on is not null && now - _botStart >= TimeSpan.FromMinutes(s.EveryMinutes.Value),
                on is null ? "not counting (checks paused)" : $"{(now - _botStart).TotalMinutes:0.0} minutes so far");
        Add(s.DailyAt, $"Daily at {s.DailyTime}", TimeOnly.TryParse(s.DailyTime, out var at) && _returnedDay != DateOnly.FromDateTime(local)
            && TimeOnly.FromDateTime(local) >= at && TimeOnly.FromDateTime(local) < at.AddMinutes(5), $"now {local:HH:mm}");
        n = _client.World.Party?.Members.Count ?? 0;
        Add(s.PartyBelow.On, $"Fewer than {s.PartyBelow.Value} in the party", n < s.PartyBelow.Value, $"{n} in the party");
        if (s.NotAttackedMinutes.On)
            Add(true, $"Not attacked for {s.NotAttackedMinutes.Value} minutes", on is not null && now - _lastAttacked >= TimeSpan.FromMinutes(s.NotAttackedMinutes.Value),
                on is null ? "not counting (checks paused)" : $"last hit {(now - _lastAttacked).TotalMinutes:0.0} minutes ago");
        var unique = _client.World.Mobs.Values.FirstOrDefault(m => m.MobType.IsUnique());
        Add(s.UniqueNear, "A unique is near", unique is not null, unique?.Name ?? "none");
        Add(s.TransportDied, "The job transport died", _transportDied, _transportDied ? "yes" : "no");
        Add(s.PinkStatus, "Pink status", c.PvpState == PvpState.Assaulter, c.PvpState.ToString());
        if (s.ReturnDisconnectMinutes.On)
            Add(true, $"Return and disconnect every {s.ReturnDisconnectMinutes.Value} minutes",
                on is not null && now - _returnDisconnectStart >= TimeSpan.FromMinutes(s.ReturnDisconnectMinutes.Value),
                on is null ? "not counting (checks paused)" : $"{(now - _returnDisconnectStart).TotalMinutes:0.0} minutes so far");
        if (lines.Count == header)
            lines.Add("No return option is turned on.");
        return lines;
    }

    // ---------------------------------------------------------------- berserk

    private void Berserk(Character c, DateTime now)
    {
        lock (_attackers)
            foreach (var old in _attackers.Where(a => now - a.Value > TimeSpan.FromSeconds(10)).Select(a => a.Key).ToList())
                _attackers.Remove(old);
        if (c.BerserkPoints < 5 || now - _lastBerserk < TimeSpan.FromSeconds(5))
            return;
        var s = _settings;
        string? why = null;
        if (s.BerserkOnMonsterType && now - _typeHitAt < TimeSpan.FromSeconds(5))
            why = $"fighting {_typeHitName}";
        else if (s.BerserkWhenMonstersAttack.On && AttackerCount() > s.BerserkWhenMonstersAttack.Value)
            why = $"{AttackerCount()} monsters attacking";
        else if (s.BerserkWhileScript && IsWalkingScript())
            why = "walking the script";
        if (why is null)
            return;
        _lastBerserk = now;
        _logger.LogInformation("Protection: berserk ({Reason})", why);
        _client.Send(ClientPackets.Berserk());
    }

    /// <summary>Living monsters that hit the character in the last 10 seconds.</summary>
    public int AttackerCount()
    {
        lock (_attackers)
            return _attackers.Keys.Count(id => _client.World.Mobs.TryGetValue(id, out var m) && m.LifeState != LifeState.Dead);
    }

    // ---------------------------------------------------------------- scrolls

    /// <summary>
    /// Uses the listed scrolls again whenever their buff is gone. Which buff a scroll gives is learned the first time it
    /// is used (the buff that appears within 5 seconds); a scroll without a buff is used again every 10 minutes.
    /// </summary>
    private async Task ScrollsAsync(Character c, DateTime now, bool botting, CancellationToken token)
    {
        var s = _settings;
        if (_scrollPending is { } pending && now - pending.At >= TimeSpan.FromSeconds(1))
        {
            var added = c.Buffs.Values.Select(b => b.Id).Where(id => !pending.Before.Contains(id)).ToList();
            if (added.Count > 0 || now - pending.At > TimeSpan.FromSeconds(5))
            {
                _scrollBuff[pending.Key] = added.Count > 0 ? added[0] : 0;
                _scrollPending = null;
            }
            else
                return;
        }
        // After entering the game (login, teleport), wait for the buffs that are already on to come in first:
        // a scroll whose buff is on is refused (0x0002).
        if (!ReferenceEquals(c, _scrollCharacter))
        {
            _scrollCharacter = c;
            _scrollsFrom = now + ScrollsAfterEntering;
        }
        if (now < _scrollsFrom)
            return;
        if (s.Scrolls.Count == 0 || (botting ? !s.ScrollsWhileBotting : !s.ScrollsWhenNotBotting)
            || now - _lastScrollCheck < TimeSpan.FromSeconds(1) || c.Scrolling != ScrollingType.None)
            return;
        _lastScrollCheck = now;
        var active = c.Buffs.Values.Select(b => b.Id).ToHashSet();
        foreach (var part in s.Scrolls)
        {
            int slot = -1;
            for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
                if (c.Inventory[i] is { TypeId2: 3, TypeId3: 13 } item
                    && (item.Name.Contains(part, StringComparison.OrdinalIgnoreCase) || item.ServerName.Contains(part, StringComparison.OrdinalIgnoreCase)))
                {
                    slot = i;
                    break;
                }
            if (slot < 0)
            {
                if (_scrollNotes.Add("missing:" + part))
                    _logger.LogInformation("Protection: no scroll in the bag matches \"{Part}\" (scroll list)", part);
                continue;
            }
            _scrollNotes.Remove("missing:" + part);
            string key = c.Inventory[slot]!.ServerName;
            _scrollUsed.TryGetValue(key, out var last);
            if (ProtectionCatalog.ScrollSkill(_client.Data.GetSkill, key) is { } skill)
            {
                // Its buff is known: used only while no buff like it is on (also buffs already on at login).
                if (ProtectionCatalog.SameBuff(c.Buffs.Values, skill) is { } on)
                {
                    if (_scrollNotes.Add("on:" + key))
                        _logger.LogInformation("Protection: {Scroll} (for \"{Part}\") is not used while {Buff} is on", c.Inventory[slot]?.Name ?? key, part, on.Name);
                    continue;
                }
                if (now - last < TimeSpan.FromSeconds(5))
                    continue; // just used: its buff is on its way
                _scrollNotes.Remove("on:" + key);
            }
            else if (_scrollBuff.TryGetValue(key, out uint buff))
            {
                if (buff != 0 ? active.Contains(buff) || now - last < TimeSpan.FromSeconds(5) : now - last < TimeSpan.FromMinutes(10))
                    continue;
            }
            else if (now - last < TimeSpan.FromSeconds(30))
                continue;
            _scrollUsed[key] = now;
            var result = await _client.UseItemAsync((byte)slot, timeout: TimeSpan.FromSeconds(2), cancellationToken: token).ConfigureAwait(false);
            if (result.Ok)
            {
                _logger.LogInformation("Protection: used {Scroll}", c.Inventory[slot]?.Name ?? key);
                _scrollNotes.Remove("refused:" + key);
                if (!_scrollBuff.ContainsKey(key))
                    _scrollPending = (key, active, now);
            }
            else
            {
                _scrollUsed[key] = now + TimeSpan.FromSeconds(30); // refused (a stronger buff ...): wait a minute
                // Said once until it works (the same refusal comes back every minute).
                if (_scrollNotes.Add("refused:" + key))
                    _logger.LogInformation("Protection: {Scroll} (for \"{Part}\") was not used: {Message}. Tried again every minute",
                        c.Inventory[slot]?.Name ?? key, part, result.Message);
            }
            return; // one scroll per pass
        }
    }

    // ---------------------------------------------------------------- weapon, transport, thief

    /// <summary>Wears another weapon of the same kind from the bag when the worn one is broken. True when one was worn.</summary>
    private async Task<bool> SwapWeaponAsync(Character c, DateTime now, CancellationToken token)
    {
        if (!_settings.ChangeWeaponIfBroken || now - _lastSwap < TimeSpan.FromSeconds(10)
            || c.Inventory[ProtectionCatalog.WeaponSlot] is not Equipable weapon)
            return false;
        int least = _settings.WeaponDurability.On ? Math.Max(1, _settings.WeaponDurability.Value) : 1;
        if (weapon.Durability >= least)
            return false;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
        {
            if (c.Inventory[i] is not Equipable other || other.TypeId3 != weapon.TypeId3 || other.TypeId4 != weapon.TypeId4 || other.Durability < least)
                continue;
            _lastSwap = now;
            _logger.LogInformation("Protection: {Weapon} is broken: wearing {Other}", weapon.Name, other.Name);
            var result = await _client.UseItemAsync((byte)i, timeout: TimeSpan.FromSeconds(3), cancellationToken: token).ConfigureAwait(false);
            if (!result.Ok)
                _logger.LogWarning("Protection: weapon change: {Message}", result.Message);
            return true;
        }
        return false;
    }

    private void Transport()
    {
        if (!_respawnTransport)
            return;
        _respawnTransport = false;
        if (!_settings.RespawnTransport)
            return;
        int slot = _client.FindMountItemSlot(preferRide: false);
        if (slot < 0 || _client.World.Character?.Inventory[slot] is not { } item || !item.ServerName.StartsWith("ITEM_COS_T_", StringComparison.Ordinal))
        {
            _logger.LogWarning("Protection: the job transport died and there is no transport in the bag");
            return;
        }
        _logger.LogInformation("Protection: the job transport died: summoning {Transport}", item.Name);
        _client.SummonMount((byte)slot);
    }

    private void ThiefInTown(DateTime now)
    {
        if (!_settings.StopIfThiefInTown || now - _lastThief < TimeSpan.FromMinutes(1) || !InTown())
            return;
        var thief = _client.World.Players.Values.FirstOrDefault(p => p.JobType == JobType.Thief && p.HasJobMode());
        if (thief is null)
            return;
        _lastThief = now;
        _logger.LogWarning("Protection: the thief {Name} is in town: stopping", thief.Name);
        StopRequested?.Invoke($"the thief {thief.Name} is in town");
    }

    private async Task ReturnAsync(Character c, string reason, CancellationToken token)
    {
        _lastReturn = DateTime.UtcNow;
        int slot = ProtectionCatalog.ReturnScrollSlot(c);
        StopRequested?.Invoke("returning to town: " + reason);
        if (slot < 0)
        {
            if (_disconnectAfterReturn)
                Disconnect("return and disconnect, without a return scroll");
            if (!_noScrollWarned)
                _logger.LogWarning("Protection: should return to town ({Reason}) but there is no return scroll; the bot was stopped", reason);
            _noScrollWarned = true;
            return;
        }
        _noScrollWarned = false;
        _logger.LogInformation("Protection: returning to town ({Reason}) with {Scroll}", reason, c.Inventory[slot]?.Name);
        var result = await _client.UseItemAsync((byte)slot, timeout: TimeSpan.FromSeconds(3), cancellationToken: token).ConfigureAwait(false);
        if (!result.Ok)
            _logger.LogWarning("Protection: return scroll: {Message}", result.Message);
        else
            ReturnedToTown?.Invoke();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                await StepAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or IndexOutOfRangeException)
            {
                _logger.LogWarning("Protection: {Message}", ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _client.DamageDealt -= OnDamage;
        _client.CharacterDied -= OnDied;
        _client.PetDied -= OnPetDied;
    }
}
