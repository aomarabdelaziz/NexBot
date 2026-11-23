using System.Text;
using NexBot.Data;

namespace NexBot.Game.Items;

public enum RentableType : uint
{
    None = 0,
    LimitedTime = 1,
    LimitedDistance = 2,
    Package = 3,
}

/// <summary>Rental information attached to an item.</summary>
public sealed class Rentable(uint type = 0)
{
    public static Rentable None => new();

    public RentableType Type { get; } = (RentableType)type;
    public ushort CanDelete { get; set; }
    public uint PeriodBeginTime { get; set; }
    public uint PeriodEndTime { get; set; }
    public ushort CanRecharge { get; set; }
    public uint MeterRateTime { get; set; }
    public uint PackingTime { get; set; }
}

/// <summary>Base item (inventory, storage, shop ...). Use <see cref="ItemFactory"/> to create one.</summary>
public class Item
{
    protected internal Item(ItemInfo info, Rentable? rentable)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Rentable = rentable ?? Rentable.None;
        Quantity = 1;
    }

    protected Item(Item other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Info = other.Info;
        Rentable = other.Rentable;
        Quantity = other.Quantity;
    }

    public ItemInfo Info { get; }
    public Rentable Rentable { get; set; }
    public ushort Quantity { get; set; }

    public uint Id => Info.Id;
    public string ServerName => Info.ServerName;
    public string Name => Info.Name;
    /// <summary>Always 3 (item) for items.</summary>
    public const byte TypeId1 = 3;
    public byte TypeId2 => Info.Tid2;
    public byte TypeId3 => Info.Tid3;
    public byte TypeId4 => Info.Tid4;
    public ushort QuantityMax => Info.Stack;
    public byte LevelRequired => Info.Level;

    public bool IsEquipable => TypeId2 == 1;
    public bool IsCos => TypeId2 == 2;
    public bool IsEtc => TypeId2 == 3;

    public bool IsType(byte typeId2, byte typeId3, byte typeId4) =>
        TypeId2 == typeId2 && TypeId3 == typeId3 && TypeId4 == typeId4;

    /// <summary>Item "usage type" sent with 0x704C (cash/bionic flags + type IDs).</summary>
    public ushort UsageType =>
        (ushort)(Info.CashItem | Info.Bionic | TypeId1 << 2 | TypeId2 << 5 | TypeId3 << 7 | TypeId4 << 11);

    /// <summary>The usage type with the rental type in the low bits instead of the cash flag (as xBot sends it).</summary>
    public ushort UsageTypeFromRentable => (ushort)((UsageType & ~0x3) | ((int)Rentable.Type & 0x3));

    public string QuantityText => QuantityMax > 1 ? $" ({Quantity}/{QuantityMax})" : string.Empty;

    public virtual string FullName => Name;

    public virtual Item Clone() => new(this);

    public override string ToString() => FullName + QuantityText;
}

public enum PetItemState : byte
{
    NeverSummoned = 1,
    Summoned = 2,
    Unsummoned = 3,
    Dead = 4,
}

/// <summary>Pet summon scrolls, transform scrolls, cubes.</summary>
public sealed class CosItem : Item
{
    internal CosItem(ItemInfo info, Rentable? rentable) : base(info, rentable) { }
    private CosItem(CosItem other) : base(other)
    {
        State = other.State;
        ModelId = other.ModelId;
        ModelName = other.ModelName;
        UnknownByte = other.UnknownByte;
    }

    public PetItemState State { get; set; }
    public uint ModelId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public byte UnknownByte { get; set; }

    public bool IsPet => TypeId3 == 1;
    public bool IsTransform => TypeId3 == 2;
    public bool IsCube => TypeId3 == 3;

    public override Item Clone() => new CosItem(this);
}

/// <summary>Consumables, alchemy materials, quest items ...</summary>
public sealed class EtcItem : Item
{
    internal EtcItem(ItemInfo info, Rentable? rentable) : base(info, rentable) { }
    private EtcItem(EtcItem other) : base(other) => AssimilationProbability = other.AssimilationProbability;

    public byte AssimilationProbability { get; set; }
    public bool IsAlchemy => TypeId3 == 11;

    public override Item Clone() => new EtcItem(this);
}

[Flags]
public enum ItemRarity : byte
{
    None = 0,
    SealOfStar = 1,
    SealOfMoon = 2,
    SealOfSun = 4,
    Honor = 8,
    Nova = 16,
}

public enum ItemGender : byte
{
    None = 0,
    Male = 1,
    Female = 2,
}

public sealed class MagicOption(uint id, MagicOptionInfo? info)
{
    public uint Id { get; } = id;
    public string ServerName { get; } = info?.ServerName ?? string.Empty;
    public string Name { get; } = info?.Name ?? string.Empty;
    public uint MaxValue { get; } = info?.MaxValue ?? 0;
    public uint Value { get; set; }

    public override string ToString() => Name.Length == 0 ? $"MagicOption {Id} = {Value}" : $"{Name} {Value}";
}

public sealed class ItemSocket(byte slot, uint id)
{
    public byte Slot { get; } = slot;
    public uint Id { get; } = id;
    public uint Value { get; set; }
}

public sealed class AdvancedElixir(byte slot, uint id)
{
    public byte Slot { get; } = slot;
    public uint Id { get; } = id;
    public uint Value { get; set; }
}

/// <summary>Weapons, armor, accessories, avatars, job suits.</summary>
public sealed class Equipable : Item
{
    internal Equipable(ItemInfo info, Rentable? rentable) : base(info, rentable) => Quantity = 1;

    private Equipable(Equipable other) : base(other)
    {
        Plus = other.Plus;
        Variance = other.Variance;
        Durability = other.Durability;
        MagicOptions = other.MagicOptions;
        Sockets = other.Sockets;
        AdvancedElixirs = other.AdvancedElixirs;
    }

    public byte Plus { get; set; }
    public ulong Variance { get; set; }
    public uint Durability { get; set; }
    public IReadOnlyList<MagicOption> MagicOptions { get; set; } = [];
    public IReadOnlyList<ItemSocket> Sockets { get; set; } = [];
    public IReadOnlyList<AdvancedElixir> AdvancedElixirs { get; set; } = [];

    public bool IsJob => TypeId3 == 7;
    public bool IsAvatar => TypeId3 == 13;
    public EquipableType EquipableType => (EquipableType)TypeId3;

    public ItemRarity Rarity
    {
        get
        {
            var rarity = ItemRarity.None;
            if (ServerName.Contains("_RARE", StringComparison.Ordinal))
            {
                if (ServerName.Contains("_HONOR", StringComparison.Ordinal)) rarity |= ItemRarity.Honor;
                if (ServerName.Contains("_A_", StringComparison.Ordinal)) rarity |= ItemRarity.SealOfStar;
                if (ServerName.Contains("_B_", StringComparison.Ordinal)) rarity |= ItemRarity.SealOfMoon;
                if (ServerName.Contains("_C_", StringComparison.Ordinal)) rarity |= ItemRarity.SealOfSun;
            }
            return rarity;
        }
    }

    public ItemGender Gender =>
        ServerName.Contains("_M_", StringComparison.Ordinal) ? ItemGender.Male
        : ServerName.Contains("_W_", StringComparison.Ordinal) ? ItemGender.Female
        : ItemGender.None;

    public override string FullName
    {
        get
        {
            var sb = new StringBuilder(Name);
            if (Plus > 0) sb.Append(" (+").Append(Plus).Append(')');
            if (AdvancedElixirs.Count > 0) sb.Append("[Adv. +").Append(AdvancedElixirs[0].Value).Append(']');
            var rarity = Rarity;
            if (rarity != ItemRarity.None) sb.Append(" [").Append(rarity).Append(']');
            return sb.ToString();
        }
    }

    public override Item Clone() => new Equipable(this);
}
