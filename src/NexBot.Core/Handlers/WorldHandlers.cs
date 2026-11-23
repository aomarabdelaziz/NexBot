using Microsoft.Extensions.Logging;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Handlers;

/// <summary>Movement, status/state updates, skills and pets.</summary>
internal static class WorldHandlers
{
    #region Movement

    /// <summary>0xB021: an entity started moving (or sky-walking).</summary>
    public static void Movement(SroClient client, Packet p)
    {
        if (client.World.GetEntity(p.ReadUInt32()) is not Model entity)
            return;

        var current = entity.GetRealtimePosition();
        bool hasDestination = p.ReadBool();
        if (hasDestination)
        {
            var destination = p.ReadMovementTarget(current.InDungeon);
            entity.MovementPosition = destination;
            if (entity == client.World.Character)
                entity.Angle = AngleTowards(current, destination);
        }

        if (p.ReadBool() && !hasDestination)
        {
            // Sky walking: no destination, only a direction. Simulate a long walk to keep interpolating.
            entity.Angle = p.ReadUInt16();
            double angle = entity.RadianAngle;
            double x = Math.Cos(angle) * ushort.MaxValue + current.PosX;
            double y = Math.Sin(angle) * ushort.MaxValue + current.PosY;
            entity.MovementPosition = current.InDungeon
                ? Position.FromWorld(x, y, current.Region, current.Z)
                : new Position(x, y);
        }

        if (entity is Cos { IsHorse: true } horse)
        {
            // Keep the rider's position in sync with the horse.
            var character = client.World.Character;
            Player? owner = character is not null && character.RidingUniqueId == horse.UniqueId
                ? character
                : client.World.Players.Values.FirstOrDefault(o => o.RidingUniqueId == horse.UniqueId);
            if (owner is not null)
                SyncRider(owner, horse);
        }
        else if (entity is Cos { IsTransport: true } transport &&
                 client.World.GetEntity(transport.OwnerUniqueId) is Player { IsRiding: true } rider)
        {
            SyncRider(rider, transport);
        }
    }

    /// <summary>
    /// 0x30D0: [uint unique ID][float walk speed][float run speed]: speed scrolls, speed buffs, berserk end ... The
    /// position so far is committed first, so the estimate continues at the new speed from here.
    /// </summary>
    public static void SpeedUpdate(SroClient client, Packet p)
    {
        if (client.World.GetEntity(p.ReadUInt32()) is not Model entity)
            return;
        float walk = p.ReadSingle(), run = p.ReadSingle();
        entity.GetRealtimePosition();
        entity.SpeedWalking = walk;
        entity.SpeedRunning = run;
    }

    private static void SyncRider(Player rider, Model mount)
    {
        rider.Position = mount.Position;
        rider.MovementPosition = mount.MovementPosition;
    }

    /// <summary>Converts a direction vector to a Silkroad angle (0..65535).</summary>
    internal static ushort AngleTowards(Position from, Position to)
    {
        double dx = to.PosX - from.PosX;
        double dy = to.PosY - from.PosY;
        if (dx == 0 && dy == 0)
            return 0;
        double radians = Math.Atan2(dy, dx);
        if (radians < 0)
            radians += Math.PI * 2;
        return (ushort)Math.Round(radians * ushort.MaxValue / (Math.PI * 2.0));
    }

    /// <summary>0xB023: the server corrected an entity's position.</summary>
    public static void MovementStuck(SroClient client, Packet p)
    {
        var entity = client.World.GetEntity(p.ReadUInt32());
        var position = p.ReadPosition();
        ushort angle = p.ReadUInt16();
        if (entity is null)
            return;
        entity.Position = position;
        entity.Angle = angle;
        if (entity is Model model)
            model.MovementPosition = position;
    }

    /// <summary>0xB024: an entity turned.</summary>
    public static void MovementAngle(SroClient client, Packet p)
    {
        var entity = client.World.GetEntity(p.ReadUInt32());
        ushort angle = p.ReadUInt16();
        if (entity is not null)
            entity.Angle = angle;
    }

    #endregion

    #region Status / state

    /// <summary>0x3057: HP/MP/bad status changed.</summary>
    public static void StatusUpdate(SroClient client, Packet p)
    {
        if (client.World.GetEntity(p.ReadUInt32()) is not Model entity)
            return;

        p.ReadByte();
        p.ReadByte();
        var type = (EntityStatusUpdateType)p.ReadByte();
        switch (type)
        {
            case EntityStatusUpdateType.Hp:
                entity.Hp = p.ReadUInt32();
                break;
            case EntityStatusUpdateType.Mp:
                entity.Mp = p.ReadUInt32();
                break;
            case EntityStatusUpdateType.HpMp:
            case EntityStatusUpdateType.EntityHpMp:
                entity.Hp = p.ReadUInt32();
                entity.Mp = p.ReadUInt32();
                break;
            case EntityStatusUpdateType.BadStatus:
                entity.BadStatus = (BadStatus)p.ReadUInt32();
                break;
        }

        if (client.Options.Features.AutoPetRecovery && entity is Cos cos && IsMyPet(client, cos))
            PetCare.OnPetStatusChanged(client, type);
    }

    private static bool IsMyPet(SroClient client, Cos cos)
    {
        var character = client.World.Character;
        if (character is null)
            return false;
        return cos.IsHorse ? cos.UniqueId == character.RidingUniqueId : cos.OwnerUniqueId == character.UniqueId;
    }

    /// <summary>0x30BF: life/motion/game/pvp/combat state changed.</summary>
    public static void StateUpdate(SroClient client, Packet p)
    {
        if (client.World.GetEntity(p.ReadUInt32()) is not Model entity)
            return;

        byte type = p.ReadByte();
        byte state = p.ReadByte();
        switch (type)
        {
            case 0:
                entity.LifeState = (LifeState)state;
                if (entity == client.World.Character)
                {
                    if (entity.LifeState == LifeState.Dead)
                    {
                        // Died without a reported killing hit (poison, bleeding ...): blame the last attacker.
                        var last = client.LastAttacker;
                        bool recent = last.Id != 0 && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(30);
                        ReportMyDeath(client, recent ? last.Id : 0, recent ? last.Name : null, killingBlow: false);
                    }
                    else if (entity.LifeState == LifeState.Alive)
                    {
                        client.DeathReported = false;
                    }
                }
                else if (entity.LifeState == LifeState.Dead && entity is Mob mob)
                {
                    OnMobDead(client, mob);
                }
                else if (entity.LifeState == LifeState.Dead && entity is Cos pet && client.World.MyPets.ContainsKey(pet.UniqueId))
                {
                    client.RaisePetDied(pet);
                }
                break;
            case 1:
                entity.GetRealtimePosition();
                entity.MotionState = (MotionState)state;
                if (entity.MotionState == MotionState.Running)
                    entity.MovementSpeedType = MovementSpeed.Running;
                else if (entity.MotionState == MotionState.Walking)
                    entity.MovementSpeedType = MovementSpeed.Walking;
                break;
            case 4:
                entity.GameState = (GameState)state;
                break;
            case 7 when entity is Player player:
                player.PvpState = (PvpState)state;
                break;
            case 8 when entity is Player player:
                player.InCombat = state == 1;
                break;
            case 11 when entity is Player player:
                player.Scrolling = (ScrollingType)state;
                break;
        }
    }

    /// <summary>0x300C: the unique notice ("... has appeared" / "... has been killed by ...").</summary>
    public static void UniqueAction(SroClient client, Packet p)
    {
        byte type = p.ReadByte();
        if (type is not (5 or 6))
            return; // other notices (e.g. GM spawns) use other layouts
        p.ReadByte();
        uint refId = p.ReadUInt32();
        string killer = type == 6 && p.Remaining > 0 ? p.ReadAscii() : "";
        string name = client.Data.GetModel(refId)?.Name is { Length: > 0 } n ? n : $"#{refId}";
        client.Logger.LogInformation(type == 5 ? "Unique notice: {Unique} has appeared" : "Unique notice: {Unique} was killed by {Killer}", name, killer);
        client.RaiseUniqueNotice(type == 5, refId, name, killer);
    }

    private static void OnMobDead(SroClient client, Mob mob)
    {
        if (client.World.Mobs.TryRemove(mob.UniqueId, out _))
            client.RaiseMobDied(mob);
    }

    #endregion

    /// <summary>0xB516: answer to our PvP cape request.</summary>
    public static void PvpCapeResponse(SroClient client, Packet p)
    {
        bool success = p.ReadSuccess();
        ushort error = success ? (ushort)0 : p.ReadErrorCode();
        if (success && client.World.Character is { } character)
            character.PvpCape = client.RequestedCape;
        client.RaisePvpCapeChanged(client.RequestedCape, success, error);
    }

    #region Skills

    /// <summary>0xB070: a skill or attack started.</summary>
    public static void SkillStart(SroClient client, Packet p)
    {
        if (!p.ReadSuccess())
        {
            // Our own attack/skill was refused (target unreachable, out of range, blocked ...).
            client.RaiseAttackFailed(p.ReadErrorCode());
            return;
        }

        var type = (SkillCastType)p.ReadByte();
        p.ReadByte();
        uint skillId = p.ReadUInt32();
        uint sourceId = p.ReadUInt32();
        uint skillUniqueId = p.ReadUInt32();
        p.ReadUInt32(); // target unique id

        // Remember the caster: skills with delayed damage report it later in 0xB071 by skill unique ID.
        if (client.ActiveSkills.Count > 2000)
            client.ActiveSkills.Clear();
        client.ActiveSkills[skillUniqueId] = (sourceId, skillId);

        if (client.World.GetEntity(sourceId) is Model source)
            source.GetRealtimePosition();

        var character = client.World.Character;
        if (character is not null && sourceId == character.UniqueId &&
            character.Skills.TryGetValue(skillId, out var skill))
        {
            skill.StartCooldown();
        }
        if (character is not null && sourceId == character.UniqueId)
            UseAmmo(character);

        if (type == SkillCastType.Attack)
            ReadDamage(client, p, sourceId, skillId);
    }

    /// <summary>
    /// The game client counts the equipped arrows / bolts down itself (one per attack with a bow or crossbow); the
    /// server only answers when they run out. Do the same so "out of arrows" can be seen.
    /// </summary>
    private static void UseAmmo(Character c)
    {
        if (c.Inventory[6] is not { TypeId3: 6, TypeId4: 6 or 12 } || c.Inventory[7] is not { } ammo || !ammo.IsType(3, 4, ammo.TypeId4)
            || ammo.TypeId4 is not (1 or 2))
            return;
        if (ammo.Quantity <= 1)
            c.Inventory[7] = null;
        else
            ammo.Quantity--;
    }

    /// <summary>0xB0BD: a buff began on an entity: target unique ID, skill ID, buff token.</summary>
    public static void BuffAdded(SroClient client, Packet p)
    {
        uint targetId = p.ReadUInt32();
        uint skillId = p.ReadUInt32();
        uint token = p.ReadUInt32();
        if (client.Data.GetSkill(skillId) is not { } info)
            return;
        var buff = new Buff(info) { UniqueId = token, TargetUniqueId = targetId, Started = System.Diagnostics.Stopwatch.GetTimestamp() };
        if (info.HasAutoTransferEffect && p.Remaining > 0 && p.ReadBool())
            buff.CasterUniqueId = targetId;
        client.World.Buffs[token] = buff;
        if (client.World.GetEntity(targetId) is Model model)
            model.Buffs[buff.Key] = buff;
    }

    /// <summary>0xB072: a buff ended: result (1), buff token.</summary>
    public static void BuffRemoved(SroClient client, Packet p)
    {
        if (p.ReadByte() != 1 || p.Remaining < 4)
            return;
        uint token = p.ReadUInt32();
        client.World.Buffs.TryRemove(token, out var known);
        // Buffs that were on at login are only in the character's list, so look there too.
        var owner = known is not null ? client.World.GetEntity(known.TargetUniqueId) as Model : null;
        foreach (var model in new[] { owner, client.World.Character }.OfType<Model>().Distinct())
        {
            foreach (var key in model.Buffs.Where(b => b.Value.UniqueId == token).Select(b => b.Key).ToList())
                model.Buffs.Remove(key);
        }
    }

    /// <summary>0xB071: a skill finished (delayed damage).</summary>
    public static void SkillEnd(SroClient client, Packet p)
    {
        if (!p.ReadSuccess())
            return;
        uint skillUniqueId = p.ReadUInt32();
        p.ReadUInt32(); // target unique id
        client.ActiveSkills.Remove(skillUniqueId, out var cast);
        ReadDamage(client, p, cast.SourceId, cast.SkillId);
    }

    /// <summary>
    /// Damage block: hasDamage, hitCount, targetCount, then for every target and every hit:
    /// effect flags, [damage type, damage, 3 unknown bytes], [knock-back position].
    /// </summary>
    private static void ReadDamage(SroClient client, Packet p, uint sourceId, uint skillId)
    {
        if (p.Remaining == 0 || p.ReadByte() != 1)
            return;
        byte hits = p.ReadByte();
        byte targets = p.ReadByte();
        string sourceName = NameOf(client, sourceId);

        for (int t = 0; t < targets; t++)
        {
            uint targetId = p.ReadUInt32();
            for (int h = 0; h < Math.Max((int)hits, 1); h++)
            {
                var effect = (DamageEffect)p.ReadByte();
                var damageType = DamageType.None;
                uint damage = 0;

                if ((effect & (DamageEffect.Block | DamageEffect.Cancel)) == 0)
                {
                    damageType = (DamageType)p.ReadByte();
                    damage = p.ReadUInt32();
                    p.ReadByte();
                    p.ReadUInt16();
                }
                if ((effect & DamageEffect.Position) != 0)
                {
                    // Knocked back / down: new position of the target.
                    p.ReadUInt16();
                    p.ReadUInt32();
                    p.ReadUInt32();
                    p.ReadUInt32();
                }

                OnHit(client, new DamageEventArgs(sourceId, sourceName, targetId, NameOf(client, targetId),
                    skillId, damage, damageType, effect));
            }
        }
    }

    private static void OnHit(SroClient client, DamageEventArgs hit)
    {
        var character = client.World.Character;
        bool hitMe = character is not null && hit.TargetId == character.UniqueId;
        if (hitMe && hit.SourceId != 0)
            client.LastAttacker = (hit.SourceId, hit.SourceName, DateTime.UtcNow);

        client.RaiseDamage(hit);

        if (!hit.IsKill)
            return;
        if (hitMe)
            ReportMyDeath(client, hit.SourceId, hit.SourceName, killingBlow: true);
        OnEntityKilled(client, hit.TargetId);
    }

    /// <summary>Reports the death of our character once (from a killing hit, or a state update).</summary>
    internal static void ReportMyDeath(SroClient client, uint killerId, string? killerName, bool killingBlow)
    {
        if (client.DeathReported)
            return;
        client.DeathReported = true;
        if (client.World.Character is { } character)
            character.LifeState = LifeState.Dead;
        client.RaiseCharacterDied(new CharacterDiedEventArgs(killerId, killerName, killingBlow));
    }

    private static string NameOf(SroClient client, uint uniqueId) =>
        uniqueId == 0 ? "?" : client.World.GetEntity(uniqueId)?.Name ?? $"[UID:{uniqueId}]";

    private static void OnEntityKilled(SroClient client, uint uniqueId)
    {
        if (client.World.GetEntity(uniqueId) is not Model entity)
            return;
        entity.LifeState = LifeState.Dead;
        if (entity is Mob mob)
            OnMobDead(client, mob);
    }

    #endregion

    #region Pets

    /// <summary>0x30C8: data of a pet we summoned.</summary>
    public static void PetData(SroClient client, Packet p)
    {
        uint uniqueId = p.ReadUInt32();
        p.ReadUInt32(); // model id
        if (client.World.GetEntity(uniqueId) is not Cos cos)
            return;

        var data = client.Data;
        if (cos.IsHorse || cos.IsTransport)
        {
            cos.Hp = p.ReadUInt32();
            cos.HpMax = p.ReadUInt32();
            var inventory = new SlotList<Item>(p.ReadByte());
            if (inventory.Capacity > 0)
            {
                byte count = p.ReadByte();
                for (int i = 0; i < count; i++)
                {
                    byte slot = p.ReadByte();
                    var rentable = new Rentable(p.ReadUInt32());
                    var item = ItemFactory.Create(data, p.ReadUInt32(), rentable);
                    item.Quantity = p.ReadUInt16();
                    p.ReadAscii(); // owner name
                    inventory[slot] = item;
                }
                cos.Inventory = inventory;
            }
        }
        else if (cos is AttackPet pet)
        {
            pet.Hp = p.ReadUInt32();
            pet.UnknownUInt1 = p.ReadUInt32();
            pet.Exp = p.ReadUInt64();
            pet.Level = p.ReadByte();
            pet.ExpMax = data.GetPetExpMax(pet.Level);
            pet.Hgp = p.ReadUInt16();
            pet.Settings = (AttackPetSettings)p.ReadUInt32();
            pet.Name = p.ReadAscii();
            pet.UnknownByte3 = p.ReadByte();
            pet.OwnerUniqueId = p.ReadUInt32();
            pet.UnknownByte4 = p.ReadByte();
        }
        else if (cos is PickPet pick)
        {
            pick.UnknownUInt1 = p.ReadUInt32();
            pick.UnknownUInt2 = p.ReadUInt32();
            pick.Settings = (PickPetSettings)p.ReadUInt32();
            pick.Name = p.ReadAscii();
            pick.Inventory = p.ReadInventory(data);
        }

        client.World.MyPets[cos.UniqueId] = cos;
    }

    /// <summary>0x30C9: pet unsummoned / hungry / evolved.</summary>
    public static void PetUpdate(SroClient client, Packet p)
    {
        uint uniqueId = p.ReadUInt32();
        byte type = p.ReadByte();
        if (client.World.GetEntity(uniqueId) is not Cos cos)
            return;

        switch (type)
        {
            case 1: // unsummoned
                client.World.MyPets.TryRemove(cos.UniqueId, out _);
                break;
            case 4 when cos is AttackPet hungry:
                hungry.Hgp = p.ReadUInt16();
                break;
            case 7 when cos is AttackPet evolved:
                if (client.Data.GetModel(p.ReadUInt32()) is { } model)
                    evolved.Evolve(model);
                break;
        }
    }

    /// <summary>0xB420: pet settings changed.</summary>
    public static void PetSettingsChanged(SroClient client, Packet p)
    {
        if (!p.ReadSuccess())
            return;
        uint uniqueId = p.ReadUInt32();
        byte type = p.ReadByte();
        if (type == 1 && client.World.GetEntity(uniqueId) is AttackPet pet)
            pet.Settings = (AttackPetSettings)p.ReadUInt32();
    }

    /// <summary>0xB0CB: a player mounted or dismounted.</summary>
    public static void PlayerMounted(SroClient client, Packet p)
    {
        if (!p.ReadSuccess())
            return;
        uint playerId = p.ReadUInt32();
        bool mounting = p.ReadBool();
        uint mountId = mounting ? p.ReadUInt32() : 0;
        if (client.World.GetEntity(playerId) is Player player)
            player.RidingUniqueId = mountId;
    }

    #endregion
}

/// <summary>Optional automatic pet care (<see cref="Configuration.FeatureOptions.AutoPetRecovery"/>).</summary>
internal static class PetCare
{
    public static void OnPetStatusChanged(SroClient client, EntityStatusUpdateType type)
    {
        switch (type)
        {
            case EntityStatusUpdateType.Hp:
            case EntityStatusUpdateType.HpMp:
            case EntityStatusUpdateType.EntityHpMp:
                UseRecoveryKitIfNeeded(client);
                break;
            case EntityStatusUpdateType.BadStatus:
                UseAbnormalPillIfNeeded(client);
                break;
        }
    }

    private static void UseRecoveryKitIfNeeded(SroClient client)
    {
        var pet = client.World.MyPets.Values.FirstOrDefault(p => p.IsHorse || p.IsTransport);
        if (pet is null || pet.HpPercent > client.Options.Features.PetRecoveryHpPercent)
            return;
        if (client.TryFindInventoryItem(3, 1, 4, out byte slot) && client.World.Character?.Inventory[slot] is { } kit)
            client.UseItem(kit, slot, pet.UniqueId);
    }

    private static void UseAbnormalPillIfNeeded(SroClient client)
    {
        var pets = client.World.MyPets.Values;
        var pet = pets.FirstOrDefault(p => (p.IsHorse || p.IsTransport) && p.BadStatus != BadStatus.None)
                  ?? pets.FirstOrDefault(p => p.IsAttackPet && p.BadStatus != BadStatus.None);
        if (pet is null)
            return;
        if (client.TryFindInventoryItem(3, 2, 7, out byte slot) && client.World.Character?.Inventory[slot] is { } pill)
            client.UseItem(pill, slot, pet.UniqueId);
    }
}
