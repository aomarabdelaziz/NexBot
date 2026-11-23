using System.Diagnostics;
using NexBot.Data;
using NexBot.Game.Items;

namespace NexBot.Game.Entities;

/// <summary>Anything that can be spawned in the world: models, drops, teleports, skill zones.</summary>
public abstract class Entity
{
    protected Entity(uint refId, string serverName, string name, byte typeId1, byte typeId2, byte typeId3, byte typeId4)
    {
        RefId = refId;
        ServerName = serverName;
        Name = name;
        TypeId1 = typeId1;
        TypeId2 = typeId2;
        TypeId3 = typeId3;
        TypeId4 = typeId4;
    }

    /// <summary>Reference object ID (row in models/items/teleports).</summary>
    public uint RefId { get; protected set; }
    public string ServerName { get; protected set; }
    public string Name { get; set; }
    public byte TypeId1 { get; }
    public byte TypeId2 { get; }
    public byte TypeId3 { get; }
    public byte TypeId4 { get; }

    /// <summary>Runtime unique ID assigned by the server.</summary>
    public uint UniqueId { get; set; }
    public Position Position { get; set; }
    public ushort Angle { get; set; }

    public bool IsModel => TypeId1 == 1;
    public bool IsDrop => TypeId1 == 3;
    public bool IsTeleport => TypeId1 == 4;
    public virtual bool IsSkillZone => false;

    public double RadianAngle => Angle * Math.PI * 2 / ushort.MaxValue;
    public double DegreeAngle => Angle * 360.0 / ushort.MaxValue;

    public virtual Position GetRealtimePosition() => Position;

    public override string ToString() => $"{Name} [{UniqueId}]";
}

/// <summary>A living thing with HP and movement: players, monsters, NPCs, pets.</summary>
public class Model : Entity
{
    private Position? _movementPosition;
    private long _positionTimestamp;

    public Model(ModelInfo info)
        : base(info.Id, info.ServerName, info.Name, 1, info.Tid2, info.Tid3, info.Tid4)
    {
        Info = info;
        HpMax = info.Hp;
        Hp = info.Hp;
    }

    public ModelInfo Info { get; }

    public uint Hp { get; set; }
    public uint Mp { get; set; }
    public uint HpMax { get; set; }
    public MovementSpeed MovementSpeedType { get; set; }
    public MovementAction MovementActionType { get; set; }
    public LifeState LifeState { get; set; }
    public MotionState MotionState { get; set; }
    public GameState GameState { get; set; }
    public float SpeedWalking { get; set; }
    public float SpeedRunning { get; set; }
    public float SpeedBerserk { get; set; }
    public BadStatus BadStatus { get; set; }
    public Dictionary<uint, Buff> Buffs { get; set; } = [];
    public byte UnknownByte1 { get; set; }
    public byte UnknownByte2 { get; set; }

    public bool IsPlayer => TypeId2 == 1;
    public bool IsNpc => TypeId2 == 2;

    /// <summary>Destination of the current movement, if any.</summary>
    public Position? MovementPosition
    {
        get => _movementPosition;
        set
        {
            _movementPosition = value;
            _positionTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Restarts movement interpolation from now.</summary>
    public void RestartPositionTimer() => _positionTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Movement speed in world units per millisecond.</summary>
    public double UnitsPerMillisecond => CurrentSpeed * 0.1 / 1000;

    public float CurrentSpeed => MovementSpeedType == MovementSpeed.Running ? SpeedRunning : SpeedWalking;

    /// <summary>Interpolates the current position from the last known position and movement target.</summary>
    public override Position GetRealtimePosition()
    {
        if (_movementPosition is not { } target || Position.IsNear(target))
            return Position;
        Position = EstimatePosition();
        RestartPositionTimer();
        return Position;
    }

    /// <summary>
    /// Same estimate as <see cref="GetRealtimePosition"/> but without changing any state, so it is safe to call
    /// from another thread (e.g. a walking script) while packets update the entity.
    /// </summary>
    public Position EstimatePosition()
    {
        var start = Position;
        if (_movementPosition is not { } target || start.IsNear(target))
            return start;

        double unitsPerMs = UnitsPerMillisecond;
        double elapsed = Stopwatch.GetElapsedTime(_positionTimestamp).TotalMilliseconds;
        if (elapsed >= start.TimeTo(target, unitsPerMs))
            return target;

        double length = start.DistanceTo(target);
        double travelled = elapsed * unitsPerMs;
        double x = (target.PosX - start.PosX) / length * travelled + start.PosX;
        double y = (target.PosY - start.PosY) / length * travelled + start.PosY;
        return start.InDungeon ? Position.FromWorld(x, y, start.Region, start.Z) : new Position(x, y);
    }

    public int HpPercent => HpMax == 0 ? 0 : (int)Math.Round(Hp * 100.0 / HpMax);
}

/// <summary>Guild information shown on spawned players.</summary>
public sealed class PlayerGuildInfo
{
    public uint GuildId { get; set; }
    public string MemberNickname { get; set; } = string.Empty;
    public uint GuildLastCrestRev { get; set; }
    public uint UnionId { get; set; }
    public uint UnionLastCrestRev { get; set; }
    public bool IsFriendly { get; set; }
    public byte MemberAuthority { get; set; }
}

public class Player : Model
{
    private bool _fullyLoaded;

    public Player(ModelInfo info) : base(info) { }

    public byte Scale { get; set; }
    public byte BerserkLevel { get; set; }
    public PvpCape PvpCape { get; set; }
    public ExpIcon ExpIcon { get; set; }
    public SlotList<Item> Inventory { get; set; } = new();
    public SlotList<Item> AvatarInventory { get; set; } = new();
    public Mask? Mask { get; set; }
    public JobType JobType { get; set; }
    public byte JobLevel { get; set; }
    public PvpState PvpState { get; set; }
    public uint RidingUniqueId { get; set; }
    public bool IsRiding => RidingUniqueId != 0;
    public bool InCombat { get; set; }
    public ScrollingType Scrolling { get; set; }
    public InteractionType Interaction { get; set; }
    public string GuildName { get; set; } = string.Empty;
    public PlayerGuildInfo? GuildInfo { get; set; }
    public Stall? Stall { get; set; }
    public byte EquipmentCooldown { get; set; }
    public byte UnknownByte3 { get; set; }

    public CaptureTheFlag CaptureTheFlag
    {
        get;
        set
        {
            field = value;
            _fullyLoaded = true;
        }
    }

    /// <summary>True when wearing a job suit (trader / thief / hunter).</summary>
    public virtual bool HasJobMode()
    {
        if (_fullyLoaded)
            return GuildInfo is null;
        return Inventory.Exists(static i => i is Equipable { IsJob: true });
    }

    public string FullName
    {
        get
        {
            if (GuildInfo is null)
                return $"{Name} ({JobType})";
            if (GuildInfo.GuildId != 0)
                return $"{Name} [{GuildName}]";
            return Name;
        }
    }
}

/// <summary>The logged-in character.</summary>
public sealed class Character : Player
{
    public Character(ModelInfo info) : base(info) { }

    public byte Level { get; set; }
    public byte LevelMax { get; set; }
    public ulong Exp { get; set; }
    public ulong ExpMax { get; set; }
    public uint SpExp { get; set; }
    public ulong Gold { get; set; }
    public uint Sp { get; set; }
    public ushort StatPoints { get; set; }

    /// <summary>Level-ups already counted from EXP whose 0x3054 level-up message has not come yet.</summary>
    internal int LevelUpsCounted { get; set; }
    public byte BerserkPoints { get; set; }
    public uint GatheredExpPoint { get; set; }
    public uint MpMax { get; set; }

    // From 0x303D (character stats)
    public uint PhysicalAttackMin { get; set; }
    public uint PhysicalAttackMax { get; set; }
    public uint MagicalAttackMin { get; set; }
    public uint MagicalAttackMax { get; set; }
    public ushort PhysicalDefense { get; set; }
    public ushort MagicalDefense { get; set; }
    public ushort HitRate { get; set; }
    public ushort ParryRate { get; set; }
    public ushort Strength { get; set; }
    public ushort Intellect { get; set; }

    public byte PkDaily { get; set; }
    public ushort PkTotal { get; set; }
    public uint PkPenalty { get; set; }
    public Dictionary<uint, Mastery> Masteries { get; set; } = [];
    public Dictionary<uint, Skill> Skills { get; set; } = [];
    public Dictionary<uint, Quest> QuestsCompleted { get; set; } = [];
    public Dictionary<uint, Quest> Quests { get; set; } = [];
    public Dictionary<uint, CollectionBook> CollectionBooks { get; set; } = [];
    public byte UnknownByte4 { get; set; }
    public string JobName { get; set; } = string.Empty;
    public uint JobExp { get; set; }
    public uint JobExpMax { get; set; }
    public uint JobContribution { get; set; }
    public uint JobReward { get; set; }
    public uint JoinId { get; set; }
    public ulong GuideFlag { get; set; }
    public bool IsGameMaster { get; set; }

    public int MpPercent => MpMax == 0 ? 0 : (int)Math.Round(Mp * 100.0 / MpMax);

    public double ExpPercent => ExpMax == 0 ? 0 : Exp * 100.0 / ExpMax;

    /// <summary>Equipment slots are 0-12; regular inventory starts at slot 13.</summary>
    public const int FirstInventorySlot = 13;

    public override bool HasJobMode() =>
        Inventory.FindIndex(static i => i is Equipable { IsJob: true }, 0, FirstInventorySlot - 1) != -1;
}

public class Npc : Model
{
    public Npc(ModelInfo info) : base(info) { }

    public byte[]? TalkOptions { get; set; }
    public bool HasTalk => TalkOptions is not null;

    public bool IsMob => TypeId3 == 1;
    public bool IsGuide => TypeId3 == 2;
    public bool IsCos => TypeId3 == 3;
    public bool IsFortressCos => TypeId3 == 4;
    public bool IsFortressStruct => TypeId3 == 5;
}

public sealed class Mob(ModelInfo info) : Npc(info)
{
    public MobType MobType { get; set; }
    public byte Appearance { get; set; }
}

public sealed class Guide(ModelInfo info) : Npc(info);

/// <summary>Character-owned services: horses, transports, attack and pick pets, guild guards.</summary>
public class Cos : Npc
{
    public Cos(ModelInfo info) : base(info) { }

    public JobType JobType { get; set; }
    public PvpState PvpState { get; set; }
    public uint OwnerUniqueId { get; set; }
    public uint OwnerRefId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public SlotList<Item> Inventory { get; set; } = new();

    public bool IsHorse => TypeId4 == 1;
    public bool IsTransport => TypeId4 == 2;
    public bool IsAttackPet => TypeId4 == 3;
    public bool IsPickPet => TypeId4 == 4;
    public bool IsGuildGuard => TypeId4 == 5;
}

public sealed class AttackPet(ModelInfo info) : Cos(info)
{
    public uint UnknownUInt1 { get; set; }
    public byte Level { get; set; }
    public ulong Exp { get; set; }
    public ulong ExpMax { get; set; }
    public ushort Hgp { get; set; }
    public AttackPetSettings Settings { get; set; }
    public byte UnknownByte3 { get; set; }
    public byte UnknownByte4 { get; set; }

    /// <summary>Changes the model after the pet evolves on level up.</summary>
    public void Evolve(ModelInfo newModel)
    {
        RefId = newModel.Id;
        ServerName = newModel.ServerName;
        HpMax = newModel.Hp;
        Hp = newModel.Hp;
    }
}

public sealed class PickPet(ModelInfo info) : Cos(info)
{
    public uint UnknownUInt1 { get; set; }
    public uint UnknownUInt2 { get; set; }
    public PickPetSettings Settings { get; set; }
}

public sealed class FortressCos(ModelInfo info) : Npc(info)
{
    public uint GuildId { get; set; }
    public string GuildName { get; set; } = string.Empty;
}

public sealed class FortressStruct(ModelInfo info) : Npc(info)
{
    public uint RefEventStructId { get; set; }
    public ushort State { get; set; }
}

/// <summary>Disguise worn by a player (thief/hunter mask or clone).</summary>
public sealed class Mask(ModelInfo info) : Entity(info.Id, info.ServerName, info.Name, 1, info.Tid2, info.Tid3, info.Tid4)
{
    public byte Scale { get; set; }
    public uint[] Inventory { get; set; } = [];
}

/// <summary>Area-of-effect skill placed on the ground.</summary>
public sealed class SkillZone() : Entity(uint.MaxValue, string.Empty, "Skill zone", 0, 0, 0, 0)
{
    public override bool IsSkillZone => true;
    public ushort UnknownUShort1 { get; set; }
    public SkillInfo? Skill { get; private set; }

    public uint SkillId { get; private set; }

    public void SetSkill(uint skillId, SkillInfo? info)
    {
        SkillId = skillId;
        Skill = info;
        if (info is not null)
        {
            Name = info.Name + " (Skill)";
            ServerName = info.ServerName;
        }
    }
}

/// <summary>An item or gold lying on the ground.</summary>
public sealed class Drop(ItemInfo info) : Entity(info.Id, info.ServerName, info.Name, 3, info.Tid2, info.Tid3, info.Tid4)
{
    public ItemInfo Info { get; } = info;
    public uint OwnerJoinId { get; set; }
    public bool HasOwner => OwnerJoinId != 0;
    public string OwnerName { get; set; } = string.Empty;
    public byte Rarity { get; set; }
    public byte DropSourceType { get; set; }
    public uint DropUniqueId { get; set; }
    public byte Plus { get; set; }
    public uint Gold { get; set; }

    public bool IsEquipable => TypeId2 == 1;
    public bool IsEtc => TypeId2 == 3;
    public bool IsGold => TypeId3 == 5 && TypeId4 == 0;
    public bool IsTradeGoods => TypeId3 == 8;
    public bool IsQuest => TypeId3 == 9;
}

/// <summary>Teleport gate or building with its destinations.</summary>
public sealed class Teleport : Entity
{
    public Teleport(TeleportInfo info, IReadOnlyList<TeleportLinkInfo> links)
        : base(info.Id, info.ServerName, info.Name, 4, info.Tid2, info.Tid3, info.Tid4)
    {
        Links = links;
        TeleportName = links.Count > 0 ? links[0].SourceName : info.Name;
    }

    public string TeleportName { get; }
    public IReadOnlyList<TeleportLinkInfo> Links { get; }
    public byte UnknownByte1 { get; set; }
    public byte UnknownByte2 { get; set; }
    public byte UnknownByte3 { get; set; }
    public PortalType PortalType { get; set; }
    public uint UnknownUInt1 { get; set; }
    public uint UnknownUInt2 { get; set; }
    public uint OwnerUniqueId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public uint UnknownUInt3 { get; set; }
    public byte UnknownByte4 { get; set; }
}
