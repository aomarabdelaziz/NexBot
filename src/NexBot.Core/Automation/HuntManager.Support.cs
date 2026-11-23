using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// The hunting bot's support: resurrect, heal, Force Cure, party buffs, Devil's Spirit, switching weapons for buffs,
/// accepting resurrections, and the items of the Training Area's Settings tab (repair hammer, speed drugs, summon scrolls).
/// </summary>
public sealed partial class HuntManager
{
    private readonly Dictionary<uint, (int Count, DateTime Last)> _resTries = [];
    private readonly Dictionary<string, DateTime> _partySent = [];
    private uint _walkingTo;
    private bool _walkBackAfterBuff;
    private byte _swapSlot;
    private DateTime _swapBackAt, _itemUsedAt, _summonAt, _speedAt;

    // ------------------------------------------------------------------ resurrect

    private void OnPetition(object? sender, PetitionEventArgs e)
    {
        if (e.Type != PlayerPetition.Resurrection)
            return;
        bool party = IsPartyMember(e.InviterName);
        if (!_settings.AutoAcceptResurrect)
            return;
        bool accept = !_settings.OnlyAcceptPartyResurrect || party;
        _logger.LogInformation("Attack: {Name} wants to resurrect you: {Answer}", e.InviterName.Length > 0 ? e.InviterName : "a player",
            accept ? "accepted" : "refused (not in the party)");
        _ = Task.Run(async () =>
        {
            await Task.Delay(800).ConfigureAwait(false);
            _client.RespondToPetition(accept, PlayerPetition.Resurrection);
        });
    }

    /// <summary>Players of the Resurrect tab (and the party with "Resurrect all party members") that are dead near you.</summary>
    private List<Player> DeadToResurrect(Character c, Position here)
    {
        var names = new HashSet<string>(_settings.ResurrectNames, StringComparer.OrdinalIgnoreCase);
        if (_settings.ResurrectAllParty && _client.World.Party is { } party)
            foreach (var m in party.Members.Values)
                names.Add(m.Name);
        names.Remove(c.Name);
        var dead = new List<Player>();
        foreach (string name in names)
            if (_client.World.Players.TryGetValue(name, out var p) && p.LifeState == LifeState.Dead
                && p.EstimatePosition().DistanceTo(here) <= _settings.ResurrectRadius)
                dead.Add(p);
        foreach (uint id in _resTries.Keys.Where(id => dead.All(p => p.UniqueId != id)).ToList())
            _resTries.Remove(id);
        return dead;
    }

    private bool Resurrect(Character c, List<Skill> skills, byte weapon, DateTime now)
    {
        if (_settings.ResurrectNames.Count == 0 && !_settings.ResurrectAllParty)
            return false;
        var here = c.EstimatePosition();
        var dead = DeadToResurrect(c, here).Where(p =>
            !_resTries.TryGetValue(p.UniqueId, out var t) || (t.Count < _settings.ResurrectTryLimit && now - t.Last >= TimeSpan.FromSeconds(_settings.ResurrectDelay))).ToList();
        if (dead.Count == 0)
            return false;
        var res = skills.Where(k => AttackPlan.IsResurrect(k.Info) && AttackPlan.Ready(k, c.Mp, weapon)).ToList();
        if (res.Count == 0)
        {
            if (_notes.Add("no resurrect"))
                _logger.LogInformation("Attack: {Name} is dead but no resurrect skill is ready (or learned)", dead[0].Name);
            return false;
        }
        var group = dead.Count > 1 ? res.Where(k => !k.Info.TargetRequired).MaxBy(k => k.Info.SkillLevel) : null;
        var skill = group ?? res.Where(k => k.Info.TargetRequired).MaxBy(k => k.Info.SkillLevel) ?? res[0];
        var who = dead.OrderBy(p => p.EstimatePosition().DistanceTo(here)).First();
        if (!CastSelf(c, skill, "resurrect", now, skill.Info.TargetRequired ? who.UniqueId : 0))
            return false;
        foreach (var p in group is null ? [who] : dead)
        {
            var t = _resTries.GetValueOrDefault(p.UniqueId);
            _resTries[p.UniqueId] = (t.Count + 1, now);
            if (t.Count + 1 == _settings.ResurrectTryLimit)
                _logger.LogInformation("Attack: {Name} was not resurrected after {Tries} tries; giving up", p.Name, t.Count + 1);
        }
        Status = $"Resurrecting {(group is null ? who.Name : $"{dead.Count} players")} ({skill.Name}).";
        return true;
    }

    // ------------------------------------------------------------------ heal, cure

    private bool Heal(Character c, List<Skill> skills, byte weapon, DateTime now)
    {
        if (_settings.HealSkills.Count == 0 || (_settings.HealSelfBelow == 0 && _settings.GroupHealBelow == 0 && _settings.HealMemberBelow == 0))
            return false;
        var members = _client.World.Party?.Members.Values.Where(m => !string.Equals(m.Name, c.Name, StringComparison.OrdinalIgnoreCase))
            .Select(m => (m.Name, (int)m.HpPercent)).ToList() ?? [];
        var (kind, member) = AttackPlan.WhoToHeal(_settings, c.HpPercent, members);
        if (kind == AttackPlan.HealKind.None)
            return false;
        var ready = _settings.HealSkills.Select(l => AttackPlan.Learned(skills, l)).OfType<Skill>()
            .Where(k => AttackPlan.Ready(k, c.Mp, weapon) && !(_buffSent.TryGetValue(k.Id, out var t) && now - t < TimeSpan.FromMilliseconds(k.Info.CastTime + 600)))
            .ToList();
        switch (kind)
        {
            case AttackPlan.HealKind.Self when (ready.FirstOrDefault(k => AttackPlan.IsSelfHeal(k.Info) || k.Info.TargetRequired) ?? ready.FirstOrDefault()) is { } self:
                return CastSelf(c, self, "heal (you)", now);
            case AttackPlan.HealKind.Group when ready.FirstOrDefault(k => !k.Info.TargetRequired && !AttackPlan.IsSelfHeal(k.Info)) is { } group:
                return CastSelf(c, group, "group heal", now);
            case AttackPlan.HealKind.Member when member is not null && _client.World.Players.TryGetValue(member, out var p)
                && p.EstimatePosition().DistanceTo(c.EstimatePosition()) <= Math.Max(_settings.PartyBuffRadius, 30)
                && ready.FirstOrDefault(k => k.Info.TargetRequired) is { } one:
                return CastSelf(c, one, $"heal ({member})", now, p.UniqueId);
        }
        return false;
    }

    private bool Cure(Character c, List<Skill> skills, byte weapon)
    {
        if (!_settings.ForceCure || c.BadStatus == BadStatus.None)
            return false;
        var now = DateTime.UtcNow;
        var cure = skills.Where(k => AttackPlan.IsCure(k.Info) && AttackPlan.Ready(k, c.Mp, weapon)
                && !(_buffSent.TryGetValue(k.Id, out var t) && now - t < TimeSpan.FromSeconds(2)))
            .MaxBy(k => k.Info.SkillLevel);
        return cure is not null && CastSelf(c, cure, $"Force Cure ({c.BadStatus})", now);
    }

    private bool DevilSpirit(Character c, List<Skill> skills, byte weapon)
    {
        if (!_settings.UseDevilSpirit)
            return false;
        var now = DateTime.UtcNow;
        foreach (var k in skills.Where(k => AttackPlan.IsDevilSpirit(k.Info)))
        {
            if (AttackPlan.IsOn(c, k) || !AttackPlan.Ready(k, c.Mp, weapon) || (_buffSent.TryGetValue(k.Id, out var t) && now - t < TimeSpan.FromSeconds(10))
                || (_buffBlocked.TryGetValue(k.Id, out var until) && now < until))
                continue;
            return CastSelf(c, k, "Devil's Spirit", now);
        }
        if (skills.Any(k => AttackPlan.IsDevilSpirit(k.Info)))
            return false;

        // The usual case: the skill comes with the worn devil avatar and is not in the skill list. Only the skill
        // the game client was seen casting is used: casting one the character does not have disconnects it.
        bool wearing = false;
        for (int i = 0; i < c.AvatarInventory.Capacity; i++)
            if (c.AvatarInventory[i] is { } item && item.ServerName.Contains("_NASRUN", StringComparison.OrdinalIgnoreCase))
                wearing = true;
        if (!wearing)
        {
            if (_notes.Add("no devil"))
                _logger.LogInformation("Attack: Use Devil's Spirit is on but no Devil's Spirit avatar is worn");
            return false;
        }
        if (_settings.DevilSkill.Length == 0 || _client.Data.GetSkill(_settings.DevilSkill) is not { } info)
        {
            if (_notes.Add("devil unknown"))
                _logger.LogInformation("Attack: cast your Devil's Spirit skill once in the game: the bot learns which one it is and keeps it on from then on");
            return false;
        }
        if (c.Buffs.Values.Any(b => b.Id == info.Id || AttackPlan.IsDevilSpirit(b.Info)))
            return false;
        // Not learned, so the game data has no cooldown for it: wait the skill's cooldown after a cast.
        if (_buffSent.TryGetValue(info.Id, out var sent) && now - sent < TimeSpan.FromMilliseconds(Math.Max(10_000, info.Cooldown)))
            return false;
        if (_buffBlocked.TryGetValue(info.Id, out var blocked) && now < blocked)
            return false;
        return CastSelf(c, new Skill(info), "Devil's Spirit", now);
    }

    /// <summary>The game client cast a Devil's Spirit skill (0x7074 [1][4][uint skill]...): remember it.</summary>
    private void OnPacketSent(object? sender, PacketSentEventArgs e)
    {
        var p = e.Packet.Payload;
        if (!e.FromGameClient || e.Packet.Opcode != Opcodes.Client.Action || p.Length < 6 || p[0] != 1 || p[1] != (byte)CharacterAction.SkillCast)
            return;
        uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
        if (_client.Data.GetSkill(id) is not { } info || !AttackPlan.IsDevilSpirit(info) || info.ServerName == _settings.DevilSkill)
            return;
        _settings.DevilSkill = info.ServerName;
        _buffSent[id] = DateTime.UtcNow; // the game just cast it: its cooldown runs
        _logger.LogInformation("Attack: Devil's Spirit skill learned from the game: {Skill} ({Server})", info.Name, info.ServerName);
        DevilSkillLearned?.Invoke();
    }

    /// <summary>The Devil's Spirit skill was learned from the game client (the settings should be saved).</summary>
    public event Action? DevilSkillLearned;

    // ------------------------------------------------------------------ party buffs

    private bool PartyBuffs(Character c, List<Skill> skills, byte weapon, DateTime now)
    {
        if (_settings.PartyBuffs.Count == 0)
        {
            _walkingTo = 0;
            return false;
        }
        var here = c.EstimatePosition();
        if (_settings.PartyBuffOnlyIfNear && _settings.PartyBuffTriggerNames.Count > 0
            && !_settings.PartyBuffTriggerNames.Any(n => _client.World.Players.TryGetValue(n, out var t) && t.EstimatePosition().DistanceTo(here) <= _settings.PartyBuffRadius * 2))
        {
            Status = "Party buffing waits: none of the trigger players is near.";
            return false;
        }
        foreach (var pb in _settings.PartyBuffs)
        {
            if (AttackPlan.Learned(skills, pb.Skill) is not { } skill || !skill.IsCastingEnabled || c.Mp < skill.Info.Mana)
                continue;
            IEnumerable<Player> players = _settings.BuffAllNearbyPlayers
                ? _client.World.Players.Values.Where(p => p is not Character)
                : pb.Players.Select(n => _client.World.Players.TryGetValue(n, out var p) ? p : null).OfType<Player>();
            foreach (var p in players)
            {
                if (p.UniqueId == c.UniqueId || p.LifeState == LifeState.Dead || AttackPlan.IsOn(p, skill))
                    continue;
                string key = $"{skill.Id}:{p.UniqueId}";
                if (_partySent.TryGetValue(key, out var sent) && now - sent < TimeSpan.FromSeconds(6))
                    continue;
                var at = p.EstimatePosition();
                double distance = at.DistanceTo(here);
                if (distance > _settings.PartyBuffRadius)
                    continue;
                if (!AttackPlan.WeaponFits(skill.Info, weapon))
                {
                    if (_settings.SwitchWeaponsForBuffs && SwitchWeaponFor(c, skill, now))
                        return true;
                    continue;
                }
                if (distance > 20)
                {
                    // Walk towards the player first (the buff's range).
                    if (_walkingTo != p.UniqueId || now - _walkSent > TimeSpan.FromSeconds(2))
                    {
                        Send(ClientPackets.MoveTo(at, c.IsRiding ? c.RidingUniqueId : 0));
                        _walkSent = now;
                    }
                    _walkingTo = p.UniqueId;
                    _walkBackAfterBuff = _settings.WalkBackAfterBuffing;
                    Status = $"Walking to {p.Name} to cast {skill.Name} ({distance:0} away).";
                    return true;
                }
                _walkingTo = 0;
                _partySent[key] = now;
                return CastSelf(c, skill, $"party buff ({p.Name})", now, p.UniqueId);
            }
        }
        _walkingTo = 0;
        if (_walkBackAfterBuff)
        {
            _walkBackAfterBuff = false;
            var (x, y) = _area.Center;
            if (_area.IsSet)
                Send(ClientPackets.MoveTo(Position.IsDungeonRegion(_area.Region) ? Position.FromWorld(x, y, _area.Region) : new Position(x, y),
                    c.IsRiding ? c.RidingUniqueId : 0));
        }
        return false;
    }

    // ------------------------------------------------------------------ weapons

    /// <summary>Puts on a weapon of the bag that the buff needs (it is switched back after the cast).</summary>
    private bool SwitchWeaponFor(Character c, Skill skill, DateTime now)
    {
        if (_swapSlot != 0)
            return false;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
        {
            if (c.Inventory[i] is not Equipable { TypeId3: 6 } w || !AttackPlan.WeaponFits(skill.Info, w.TypeId4))
                continue;
            if (!Send(ClientPackets.MoveItem(ItemMovementType.InventoryToInventory, (byte)i, 6)))
                return false;
            _swapSlot = (byte)i;
            _swapBackAt = DateTime.MaxValue;
            _busyUntil = now + TimeSpan.FromMilliseconds(900);
            _logger.LogInformation("Attack: switching to {Weapon} for {Buff}", w.Name, skill.Name);
            Status = $"Switching to {w.Name} for {skill.Name}.";
            return true;
        }
        if (_notes.Add("no weapon " + skill.Id))
            _logger.LogInformation("Attack: {Buff} needs another weapon and none in the bag fits", skill.Name);
        return false;
    }

    /// <summary>After the buffs of the other weapon were cast, the first weapon goes back on.</summary>
    private bool SwitchBackWeapon(Character c, DateTime now)
    {
        if (_swapSlot == 0)
            return false;
        if (_swapBackAt == DateTime.MaxValue)
        {
            // Cast with the switched weapon first: switch back a moment after the last buff was sent.
            if (_lastBuffAt > now - TimeSpan.FromSeconds(5) && _lastBuffAt != default)
                _swapBackAt = _lastBuffAt + TimeSpan.FromSeconds(2);
            else if (now - _busyUntil > TimeSpan.FromSeconds(8))
                _swapBackAt = now;
            return false;
        }
        if (now < _swapBackAt)
            return false;
        byte slot = _swapSlot;
        _swapSlot = 0;
        if (c.Inventory[slot] is Equipable { TypeId3: 6 } back && Send(ClientPackets.MoveItem(ItemMovementType.InventoryToInventory, slot, 6)))
        {
            _logger.LogInformation("Attack: switching back to {Weapon}", back.Name);
            _busyUntil = now + TimeSpan.FromMilliseconds(900);
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ items (Training Area: Settings)

    private bool UseItems(Character c, DateTime now)
    {
        if (now - _itemUsedAt < TimeSpan.FromSeconds(30))
            return SpeedDrugs(c, inScript: false);
        if (_setup.UseRepairHammer)
        {
            bool worn = Enumerable.Range(0, 8).Any(i => c.Inventory[i] is Equipable e && e.Durability < _setup.RepairHammerBelow);
            if (worn && Find(c, i => i.ServerName.Contains("REPAIR_HAMMER", StringComparison.OrdinalIgnoreCase)) is { } hammer)
            {
                _itemUsedAt = now;
                _logger.LogInformation("Training: an item is worn out: using {Hammer}", hammer.Item.Name);
                return Send(ClientPackets.UseItem(hammer.Item, hammer.Slot));
            }
        }
        return SpeedDrugs(c, inScript: false);
    }

    /// <summary>Use speed drugs (Settings tab) when no speed buff is on; "only in the script" uses them only while walking.</summary>
    private bool SpeedDrugs(Character c, bool inScript)
    {
        if (!_setup.UseSpeedDrugs || (_setup.SpeedDrugsOnlyInScript && !inScript))
            return false;
        var now = DateTime.UtcNow;
        if (now - _speedAt < TimeSpan.FromSeconds(10))
            return false;
        foreach (int i in Enumerable.Range(Character.FirstInventorySlot, c.Inventory.Capacity - Character.FirstInventorySlot))
        {
            if (c.Inventory[i] is not { } item || !item.ServerName.Contains("SPEED", StringComparison.OrdinalIgnoreCase)
                || item.ServerName.Contains("RETURN", StringComparison.OrdinalIgnoreCase) || item.ServerName.Contains("ARCHEMY", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ProtectionCatalog.ScrollSkill(_client.Data.GetSkill, item.ServerName) is { } skill && ProtectionCatalog.SameBuff(c.Buffs.Values, skill) is not null)
                return false;
            _speedAt = now;
            _logger.LogInformation("Training: using {Item}", item.Name);
            return Send(ClientPackets.UseItem(item, (byte)i));
        }
        return false;
    }

    /// <summary>Use a monster summon scroll in the area (Settings tab), waiting for the strong monsters to be killed first.</summary>
    private bool SummonScroll(Character c, DateTime now)
    {
        if (!_setup.UseSummonScrolls || now - _summonAt < TimeSpan.FromSeconds(15) || !_area.Contains(c.Position.PosX, c.Position.PosY))
            return false;
        if (_setup.WaitForStrongBeforeSummon && _client.World.Mobs.Values.Any(m => m.LifeState != LifeState.Dead
                && AttackPlan.TypeOf(m.MobType) == "Strong" && _area.Contains(m.Position.PosX, m.Position.PosY)))
            return false;
        if (Find(c, i => i.ServerName.Contains("SUMMON", StringComparison.OrdinalIgnoreCase) && i.ServerName.Contains("SCROLL", StringComparison.OrdinalIgnoreCase)
                && !i.ServerName.Contains("PET", StringComparison.OrdinalIgnoreCase)) is not { } scroll)
            return false;
        _summonAt = now;
        _logger.LogInformation("Training: using {Scroll}", scroll.Item.Name);
        Status = $"Using {scroll.Item.Name}.";
        return Send(ClientPackets.UseItem(scroll.Item, scroll.Slot));
    }

    private static (Item Item, byte Slot)? Find(Character c, Func<Item, bool> wanted)
    {
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is { } item && wanted(item))
                return (item, (byte)i);
        return null;
    }
}
