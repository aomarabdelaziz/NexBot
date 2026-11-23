using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Handlers;

/// <summary>Entity spawns, despawns and selection.</summary>
internal static class SpawnHandlers
{
    /// <summary>0x3017: start of a group spawn (type 1 = spawn, otherwise despawn).</summary>
    public static void GroupSpawnBegin(SroClient client, Packet p)
    {
        byte type = p.ReadByte();
        ushort count = p.ReadUInt16();
        client.GroupSpawn = (type, count, new Packet(Opcodes.Server.EntityGroupSpawnData));
    }

    /// <summary>0x3019: group spawn data chunk.</summary>
    public static void GroupSpawnData(SroClient client, Packet p) =>
        client.GroupSpawn.Buffer?.WriteBytes(p.Payload);

    /// <summary>0x3018: end of group spawn; parse every entity.</summary>
    public static void GroupSpawnEnd(SroClient client, Packet p)
    {
        var (type, count, buffer) = client.GroupSpawn;
        client.GroupSpawn = default;
        if (buffer is null)
            return;

        for (int i = 0; i < count; i++)
        {
            if (type == 1)
            {
                // Entities are packed back to back: if one fails to parse, the rest of the stream cannot be read.
                if (!TrySpawn(client, buffer))
                    break;
            }
            else
            {
                Despawn(client, buffer.ReadUInt32());
            }
        }
    }

    /// <summary>0x3015: single entity spawn.</summary>
    public static void Spawn(SroClient client, Packet p) => TrySpawn(client, p);

    /// <summary>0x3016: single entity despawn.</summary>
    public static void Despawn(SroClient client, Packet p) => Despawn(client, p.ReadUInt32());

    private static void Despawn(SroClient client, uint uniqueId)
    {
        var entity = client.World.RemoveSpawn(uniqueId);
        if (entity is not null)
            client.RaiseEntityDespawned(entity);
    }

    private static bool TrySpawn(SroClient client, Packet p)
    {
        try
        {
            var entity = ParseSpawn(client, p);
            client.World.AddSpawn(entity);
            client.RaiseEntitySpawned(entity);
            if (entity is Player player)
                client.RaisePlayerSpawned(player);
            return true;
        }
        catch (Exception ex) when (ex is PacketException or UnknownGameObjectException)
        {
            client.Logger.LogDebug("Could not parse spawn in 0x{Opcode:X4}: {Message}", p.Opcode, ex.Message);
            return false;
        }
    }

    internal static Entity ParseSpawn(SroClient client, Packet p)
    {
        var data = client.Data;
        var entity = EntityFactory.Create(data, p.ReadUInt32());
        bool singleSpawn = p.Opcode == Opcodes.Server.EntitySpawn;

        switch (entity)
        {
            case SkillZone zone:
                zone.UnknownUShort1 = p.ReadUInt16();
                uint skillId = p.ReadUInt32();
                zone.SetSkill(skillId, data.GetSkill(skillId));
                zone.UniqueId = p.ReadUInt32();
                zone.Position = p.ReadPosition();
                zone.Angle = p.ReadUInt16();
                break;

            case Model model:
                ParseModel(client, p, model, singleSpawn);
                break;

            case Drop drop:
                if (drop.IsEquipable)
                    drop.Plus = p.ReadByte();
                else if (drop.IsEtc)
                {
                    if (drop.IsGold)
                        drop.Gold = p.ReadUInt32();
                    else if (drop.IsQuest || drop.IsTradeGoods)
                        drop.OwnerName = p.ReadAscii();
                }
                drop.UniqueId = p.ReadUInt32();
                drop.Position = p.ReadPosition();
                drop.Angle = p.ReadUInt16();
                if (p.ReadBool())
                    drop.OwnerJoinId = p.ReadUInt32();
                drop.Rarity = p.ReadByte();
                if (singleSpawn)
                {
                    drop.DropSourceType = p.ReadByte();
                    drop.DropUniqueId = p.ReadUInt32();
                }
                break;

            case Teleport portal:
                portal.UniqueId = p.ReadUInt32();
                portal.Position = p.ReadPosition();
                portal.Angle = p.ReadUInt16();
                portal.UnknownByte1 = p.ReadByte();
                portal.UnknownByte2 = p.ReadByte();
                portal.UnknownByte3 = p.ReadByte();
                portal.PortalType = (PortalType)p.ReadByte();
                if (portal.PortalType == PortalType.Regular)
                {
                    portal.UnknownUInt1 = p.ReadUInt32();
                    portal.UnknownUInt2 = p.ReadUInt32();
                }
                else if (portal.PortalType == PortalType.Dimensional)
                {
                    portal.OwnerName = p.ReadAscii();
                    portal.OwnerUniqueId = p.ReadUInt32();
                }
                if (portal.UnknownByte2 == 1)
                {
                    portal.UnknownUInt3 = p.ReadUInt32(); // STORE_OnONE_DEFAULT
                    portal.UnknownByte4 = p.ReadByte();
                }
                break;
        }
        return entity;
    }

    private static void ParseModel(SroClient client, Packet p, Model model, bool singleSpawn)
    {
        var data = client.Data;

        if (model is Player player)
        {
            player.Scale = p.ReadByte();
            player.BerserkLevel = p.ReadByte();
            player.PvpCape = (PvpCape)p.ReadByte();
            player.ExpIcon = (ExpIcon)p.ReadByte();
            player.Inventory = ReadEquipment(p, data);
            player.AvatarInventory = ReadEquipment(p, data);

            if (p.ReadBool())
            {
                uint maskId = p.ReadUInt32();
                var maskInfo = data.GetModel(maskId)
                               ?? throw new UnknownGameObjectException($"Mask model {maskId} is not in the game database.");
                var mask = new Mask(maskInfo);
                if (mask.TypeId1 == player.TypeId1 && mask.TypeId2 == player.TypeId2)
                {
                    // Clone of another player
                    mask.Scale = p.ReadByte();
                    mask.Inventory = p.ReadUInt32Array(p.ReadByte());
                }
                player.Mask = mask;
            }
        }
        else if (model is FortressStruct fortress)
        {
            fortress.Hp = p.ReadUInt32();
            fortress.RefEventStructId = p.ReadUInt32();
            fortress.State = p.ReadUInt16();
        }

        p.ReadModelMovementAndState(model, out byte unknown);
        model.UnknownByte1 = unknown;
        model.Buffs = p.ReadBuffs(data, model.UniqueId);

        if (model is Player pl)
        {
            pl.Name = p.ReadAscii();
            pl.JobType = (JobType)p.ReadByte();
            pl.JobLevel = p.ReadByte();
            pl.PvpState = (PvpState)p.ReadByte();
            bool isRiding = p.ReadBool();
            pl.InCombat = p.ReadBool();
            if (isRiding)
                pl.RidingUniqueId = p.ReadUInt32();
            pl.Scrolling = (ScrollingType)p.ReadByte();
            pl.Interaction = (InteractionType)p.ReadByte();
            pl.UnknownByte3 = p.ReadByte();
            pl.GuildName = p.ReadAscii();

            if (pl.HasJobMode())
            {
                pl.Name = "*" + pl.Name;
            }
            else
            {
                pl.GuildInfo = new PlayerGuildInfo
                {
                    GuildId = p.ReadUInt32(),
                    MemberNickname = p.ReadAscii(),
                    GuildLastCrestRev = p.ReadUInt32(),
                    UnionId = p.ReadUInt32(),
                    UnionLastCrestRev = p.ReadUInt32(),
                    IsFriendly = p.ReadBool(),
                    MemberAuthority = p.ReadByte(),
                };
            }

            if (pl.Interaction == InteractionType.OnStall)
                pl.Stall = new Stall { Title = p.ReadAscii(), DecorationId = p.ReadUInt32() };

            pl.EquipmentCooldown = p.ReadByte();
            pl.CaptureTheFlag = (CaptureTheFlag)p.ReadByte();
        }
        else if (model is Npc npc)
        {
            if (p.ReadByte() != 0)
                npc.TalkOptions = p.ReadBytes(p.ReadByte());

            switch (npc)
            {
                case Mob mob:
                    mob.MobType = (MobType)p.ReadByte();
                    if (mob.TypeId4 is 2 or 3)
                        mob.Appearance = p.ReadByte();
                    break;

                case Cos cos when !cos.IsHorse:
                    if (cos.IsAttackPet || cos.IsPickPet)
                        cos.Name = p.ReadAscii();
                    cos.OwnerName = p.ReadAscii();
                    cos.JobType = (JobType)p.ReadByte();
                    if (!cos.IsPickPet)
                        cos.PvpState = (PvpState)p.ReadByte();
                    if (cos.IsGuildGuard)
                        cos.OwnerRefId = p.ReadUInt32();
                    cos.OwnerUniqueId = p.ReadUInt32();
                    break;

                case FortressCos fortressCos:
                    fortressCos.GuildId = p.ReadUInt32();
                    fortressCos.GuildName = p.ReadAscii();
                    break;
            }
        }

        if (singleSpawn)
            model.UnknownByte2 = p.ReadByte();
    }

    /// <summary>Visible equipment of a spawned player: max capacity, count, then (refId [+plus]) entries.</summary>
    private static SlotList<Item> ReadEquipment(Packet p, Data.IGameData data)
    {
        p.ReadByte(); // max capacity
        byte count = p.ReadByte();
        var inventory = new SlotList<Item>(count);
        for (int i = 0; i < count; i++)
        {
            var item = ItemFactory.Create(data, p.ReadUInt32());
            if (item is Equipable equipable)
                equipable.Plus = p.ReadByte();
            inventory[i] = item;
        }
        return inventory;
    }

    /// <summary>0xB045: selection result.</summary>
    public static void Selection(SroClient client, Packet p) =>
        client.World.SelectedEntityId = p.ReadSuccess() ? p.ReadUInt32() : 0;
}
