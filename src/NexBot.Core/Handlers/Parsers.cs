using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Protocol;

namespace NexBot.Handlers;

/// <summary>Reusable structure parsers shared by several packets.</summary>
internal static class Parsers
{
    /// <summary>
    /// Result byte of a server response: 1 = success, 2 = failure (followed by an error code).
    /// Must not be read with ReadBool, which would treat the failure value 2 as success.
    /// </summary>
    public static bool ReadSuccess(this Packet p) => p.ReadByte() == 1;

    /// <summary>Error code after a failed result byte (0 if the packet has none).</summary>
    public static ushort ReadErrorCode(this Packet p) => p.Remaining >= 2 ? p.ReadUInt16() : (ushort)0;

    /// <summary>Region + float X/Z/Y, as used by spawn and character data.</summary>
    public static Position ReadPosition(this Packet p)
    {
        ushort region = p.ReadUInt16();
        int x = (int)p.ReadSingle();
        int z = (int)p.ReadSingle();
        int y = (int)p.ReadSingle();
        return new Position(region, x, z, y);
    }

    /// <summary>Movement destination: region + int (dungeon) or ushort X/Z/Y.</summary>
    public static Position ReadMovementTarget(this Packet p, bool dungeon)
    {
        ushort region = p.ReadUInt16();
        return dungeon
            ? new Position(region, p.ReadInt32(), p.ReadInt32(), p.ReadInt32())
            : new Position(region, p.ReadUInt16(), p.ReadUInt16(), p.ReadUInt16());
    }

    /// <summary>Region-first variant where the region decides the coordinate width (party members).</summary>
    public static Position ReadRegionPosition(this Packet p)
    {
        ushort region = p.ReadUInt16();
        return Position.IsDungeonRegion(region)
            ? new Position(region, p.ReadInt32(), p.ReadInt32(), p.ReadInt32())
            : new Position(region, p.ReadUInt16(), p.ReadUInt16(), p.ReadUInt16());
    }

    /// <summary>Position, angle, movement and state block shared by the character and spawned models.</summary>
    public static void ReadModelMovementAndState(this Packet p, Model model, out byte unknownStateByte)
    {
        model.UniqueId = p.ReadUInt32();
        model.Position = p.ReadPosition();
        model.Angle = p.ReadUInt16();

        bool hasMovement = p.ReadBool();
        model.MovementSpeedType = (MovementSpeed)p.ReadByte();
        if (hasMovement)
        {
            model.MovementPosition = p.ReadMovementTarget(model.Position.InDungeon);
        }
        else
        {
            model.MovementActionType = (MovementAction)p.ReadByte();
            model.Angle = p.ReadUInt16();
        }

        model.LifeState = (LifeState)p.ReadByte();
        unknownStateByte = p.ReadByte();
        model.MotionState = (MotionState)p.ReadByte();
        model.GameState = (GameState)p.ReadByte();
        model.SpeedWalking = p.ReadSingle();
        model.SpeedRunning = p.ReadSingle();
        model.SpeedBerserk = p.ReadSingle();
    }

    /// <summary>Buff list. Buffs are keyed by skill group ID so overlapping levels replace each other.</summary>
    public static Dictionary<uint, Buff> ReadBuffs(this Packet p, IGameData data, uint ownerUniqueId)
    {
        byte count = p.ReadByte();
        var buffs = new Dictionary<uint, Buff>(count);
        for (int i = 0; i < count; i++)
        {
            uint skillId = p.ReadUInt32();
            var info = data.GetSkill(skillId)
                       ?? throw new UnknownGameObjectException($"Buff skill {skillId} is not in the game database.");
            var buff = new Buff(info) { UniqueId = p.ReadUInt32(), TargetUniqueId = ownerUniqueId };
            if (buff.HasAutoTransferEffect && p.ReadBool())
                buff.CasterUniqueId = ownerUniqueId;
            buffs[buff.Key] = buff;
        }
        return buffs;
    }

    /// <summary>Full item structure (inventory, storage, pick pet).</summary>
    public static Item ReadItem(this Packet p, IGameData data)
    {
        var rentable = new Rentable(p.ReadUInt32());
        switch (rentable.Type)
        {
            case RentableType.LimitedTime:
                rentable.CanDelete = p.ReadUInt16();
                rentable.PeriodBeginTime = p.ReadUInt32();
                rentable.PeriodEndTime = p.ReadUInt32();
                break;
            case RentableType.LimitedDistance:
                rentable.CanDelete = p.ReadUInt16();
                rentable.CanRecharge = p.ReadUInt16();
                rentable.MeterRateTime = p.ReadUInt32();
                break;
            case RentableType.Package:
                rentable.CanDelete = p.ReadUInt16();
                rentable.CanRecharge = p.ReadUInt16();
                rentable.PeriodBeginTime = p.ReadUInt32();
                rentable.PeriodEndTime = p.ReadUInt32();
                rentable.PackingTime = p.ReadUInt32();
                break;
        }

        var item = ItemFactory.Create(data, p.ReadUInt32(), rentable);
        switch (item)
        {
            case Equipable equipable:
            {
                equipable.Plus = p.ReadByte();
                equipable.Variance = p.ReadUInt64();
                equipable.Durability = p.ReadUInt32();

                byte optionCount = p.ReadByte();
                var options = new MagicOption[optionCount];
                for (int i = 0; i < optionCount; i++)
                {
                    uint id = p.ReadUInt32();
                    options[i] = new MagicOption(id, data.GetMagicOption(id)) { Value = p.ReadUInt32() };
                }
                equipable.MagicOptions = options;

                p.ReadByte(); // 1 = sockets
                byte socketCount = p.ReadByte();
                var sockets = new ItemSocket[socketCount];
                for (int i = 0; i < socketCount; i++)
                    sockets[i] = new ItemSocket(p.ReadByte(), p.ReadUInt32()) { Value = p.ReadUInt32() };
                equipable.Sockets = sockets;

                p.ReadByte(); // 2 = advanced elixirs
                byte elixirCount = p.ReadByte();
                var elixirs = new AdvancedElixir[elixirCount];
                for (int i = 0; i < elixirCount; i++)
                    elixirs[i] = new AdvancedElixir(p.ReadByte(), p.ReadUInt32()) { Value = p.ReadUInt32() };
                equipable.AdvancedElixirs = elixirs;
                break;
            }

            case CosItem cos when cos.IsPet:
                cos.State = (PetItemState)p.ReadByte();
                if (cos.State != PetItemState.NeverSummoned)
                {
                    cos.ModelId = p.ReadUInt32();
                    cos.ModelName = p.ReadAscii();
                    if (cos.TypeId4 == 2)
                        cos.Rentable.PeriodEndTime = p.ReadUInt32();
                    cos.UnknownByte = p.ReadByte();
                }
                break;

            case CosItem cos when cos.IsTransform:
                cos.ModelId = p.ReadUInt32();
                break;

            case CosItem cos when cos.IsCube:
                cos.Quantity = (ushort)p.ReadUInt32();
                break;

            case EtcItem etc:
                etc.Quantity = p.ReadUInt16();
                if (etc.IsAlchemy)
                {
                    if (etc.TypeId4 is 1 or 2) // magic / attribute stones
                        etc.AssimilationProbability = p.ReadByte();
                }
                else if (etc.TypeId3 == 14 && etc.TypeId4 == 2) // ITEM_MALL_GACHA_CARD_WIN / LOSE
                {
                    byte paramCount = p.ReadByte();
                    for (int i = 0; i < paramCount; i++)
                    {
                        p.ReadUInt32(); // magic param id
                        p.ReadUInt32(); // value
                    }
                }
                break;
        }
        return item;
    }

    /// <summary>Slot-indexed item list: capacity, count, then (slot, item) pairs.</summary>
    public static SlotList<Item> ReadInventory(this Packet p, IGameData data)
    {
        var inventory = new SlotList<Item>(p.ReadByte());
        byte count = p.ReadByte();
        for (int i = 0; i < count; i++)
        {
            byte slot = p.ReadByte();
            inventory[slot] = p.ReadItem(data);
        }
        return inventory;
    }
}
