using System.Globalization;
using NexBot.Data;
using NexBot.Game;

namespace NexBot.Map;

/// <summary>
/// The lines the game shows over a piece of equipment: sort of item, mounting part, degree, the white stats (each
/// with how good it rolled, "+42%"), required level, gender, race, max magic options and Advanced elixir. The stats
/// come from the item's ranges in itemdata (the <c>equip</c> column of the bot database) and the item's own white
/// values (5 bits each in the item's variance, 0-31 = 0-100 %).
/// </summary>
public static class EquipmentText
{
    /// <summary>The itemdata columns kept for equipment, in this order, as the <c>equip</c> column (pipe separated).</summary>
    public static readonly int[] Columns = [14, 158, .. Enumerable.Range(58, 60)];

    /// <summary>Header lines (sort, mounting part, degree) of the card; empty when the item has no stats in the database.</summary>
    public static List<string> Header(ItemInfo info)
    {
        var lines = new List<string>();
        if (info.Tid2 != 1)
            return lines;
        lines.Add("Sort of item: " + Sort(info));
        if (Part(info) is { } part)
            lines.Add("Mounting part: " + part);
        var d = Parse(info.Equip);
        if (d is not null && (EquipableType)info.Tid3 is not (EquipableType.Avatar or EquipableType.DevilSpirit or EquipableType.Job))
        {
            int degree = (int)Math.Max(1, (d.Get(61) - 1) / 3 + 1);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Degree: {degree} degrees"));
        }
        return lines;
    }

    /// <summary>The white stats with the item's plus and white values.</summary>
    public static List<string> Stats(ItemInfo info, int plus, ulong variance, uint? durability)
    {
        var lines = new List<string>();
        var d = Parse(info.Equip);
        if (d is null || info.Tid2 != 1)
            return lines;
        var type = (EquipableType)info.Tid3;
        string[] order = type switch
        {
            EquipableType.Weapon => ["DUR", "PR", "MR", "HR", "PA", "MA", "CR"],
            EquipableType.Shield => ["DUR", "PR", "MR", "BR", "PD", "MD"],
            EquipableType.AccessoryChinese or EquipableType.AccessoryEuropean => ["PAB", "MAB"],
            EquipableType.Avatar or EquipableType.DevilSpirit or EquipableType.Job => [],
            _ => ["DUR", "PR", "MR", "PD", "MD", "ER"],
        };
        double White(string stat)
        {
            int index = Array.IndexOf(order, stat);
            return index < 0 ? 0 : ((variance >> (index * 5)) & 0x1F) / 31.0;
        }
        string Pct(string stat) => string.Create(CultureInfo.InvariantCulture, $" (+{Math.Round(White(stat) * 100):0}%)");
        double Roll(int low, string stat, int inc = -1) =>
            d.Get(low) + (d.Get(low + 1) - d.Get(low)) * White(stat) + (inc < 0 ? 0 : d.Get(inc) * plus);
        var inv = CultureInfo.InvariantCulture;
        string F1(double v) => v.ToString("0.0", inv);
        string F0(double v) => Math.Floor(v).ToString("0", inv);

        void Add(string text) => lines.Add(text);
        bool Has(params int[] columns) => columns.Any(c => d.Get(c) != 0);

        if (type == EquipableType.Weapon)
        {
            if (Has(95, 96, 97, 98))
                Add($"Phy. atk. pwr. {F0(Roll(95, "PA") + d.Get(99) * plus)} ~ {F0(Roll(97, "PA") + d.Get(99) * plus)}{Pct("PA")}");
            if (Has(100, 101, 102, 103))
                Add($"Mag. atk. pwr. {F0(Roll(100, "MA") + d.Get(104) * plus)} ~ {F0(Roll(102, "MA") + d.Get(104) * plus)}{Pct("MA")}");
            AddDurability();
            if (Has(113, 114))
                Add($"Attack rate {F0(Roll(113, "HR", 115))}{Pct("HR")}");
            if (Has(116, 117))
                Add($"Critical {F0(Roll(116, "CR"))}{Pct("CR")}");
            if (Has(105, 106, 107, 108))
                Add($"Phy. reinforce {F1(Roll(105, "PR") / 10)} % ~ {F1(Roll(107, "PR") / 10)} %{Pct("PR")}");
            if (Has(109, 110, 111, 112))
                Add($"Mag. reinforce {F1(Roll(109, "MR") / 10)} % ~ {F1(Roll(111, "MR") / 10)} %{Pct("MR")}");
        }
        else if (type is EquipableType.AccessoryChinese or EquipableType.AccessoryEuropean)
        {
            if (Has(71, 72))
                Add($"Phy. absorption {F1(Roll(71, "PAB", 73))}{Pct("PAB")}");
            if (Has(79, 80))
                Add($"Mag. absorption {F1(Roll(79, "MAB", 81))}{Pct("MAB")}");
        }
        else if (order.Length > 0)
        {
            if (Has(65, 66))
                Add($"Phy. def. pwr. {F1(Roll(65, "PD", 67))}{Pct("PD")}");
            if (Has(76, 77))
                Add($"Mag. def. pwr. {F1(Roll(76, "MD", 78))}{Pct("MD")}");
            AddDurability();
            if (type == EquipableType.Shield)
            {
                if (Has(74, 75))
                    Add($"Blocking rate {F0(Roll(74, "BR"))}{Pct("BR")}");
            }
            else if (Has(68, 69))
                Add($"Parry rate {F0(Roll(68, "ER", 70))}{Pct("ER")}");
            if (Has(82, 83))
                Add($"Phy. reinforce {F1(Roll(82, "PR") / 10)} %{Pct("PR")}");
            if (Has(84, 85))
                Add($"Mag. reinforce {F1(Roll(84, "MR") / 10)} %{Pct("MR")}");
        }
        return lines;

        void AddDurability()
        {
            if (!Has(63, 64))
                return;
            string max = F0(Roll(63, "DUR"));
            Add((durability is { } now ? string.Create(inv, $"Durability {now}/{max}") : $"Durability {max}") + Pct("DUR"));
        }
    }

    /// <summary>Required level, gender, race, max magic options and Advanced elixir.</summary>
    public static List<string> Requirements(ItemInfo info)
    {
        var lines = new List<string>();
        if (info.Tid2 != 1)
            return lines;
        if (info.Level > 0)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Required level {info.Level}"));
        var d = Parse(info.Equip);
        if (d is null)
            return lines;
        switch (d.Get(58))
        {
            case 1: lines.Add("Male"); break;
            case 0: lines.Add("Female"); break;
        }
        switch (d.Get(14))
        {
            case 0: lines.Add("Chinese"); break;
            case 1: lines.Add("European"); break;
        }
        int options = (int)d.Get(158);
        if (options > 0)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Max No. of magic options: {options}Unit"));
        if ((EquipableType)info.Tid3 is not (EquipableType.Avatar or EquipableType.DevilSpirit or EquipableType.Job))
            lines.Add("Able to use Advanced elixir.");
        return lines;
    }

    /// <summary>The game's sort of item: the weapon's kind, the armor class, Shield, Earring / Necklace / Ring, Avatar.</summary>
    public static string Sort(ItemInfo info) => (EquipableType)info.Tid3 switch
    {
        EquipableType.Weapon => info.Tid4 switch
        {
            2 => "Sword", 3 => "Blade", 4 => "Spear", 5 => "Glaive", 6 => "Bow", 7 => "One-handed sword",
            8 => "Two-handed sword", 9 => "Axe", 10 => "Warlock rod", 11 => "Staff", 12 => "Crossbow", 13 => "Dagger",
            14 => "Harp", 15 => "Cleric rod", 16 => "Fortress weapon", _ => "Weapon",
        },
        EquipableType.Garment => "Garment",
        EquipableType.Protector => "Protector",
        EquipableType.Armor => "Armor",
        EquipableType.Robe => "Robe",
        EquipableType.LightArmor => "Light armor",
        EquipableType.HeavyArmor => "Heavy armor",
        EquipableType.Shield => "Shield",
        EquipableType.AccessoryChinese or EquipableType.AccessoryEuropean => info.Tid4 switch
        {
            1 => "Earring", 2 => "Necklace", 3 => "Ring", _ => "Accessory",
        },
        EquipableType.Avatar => "Avatar",
        EquipableType.DevilSpirit => "Devil Spirit",
        EquipableType.Job => "Job suit",
        _ => "Equipment",
    };

    /// <summary>Where it is worn (armor and avatars), or null.</summary>
    public static string? Part(ItemInfo info) => (EquipableType)info.Tid3 switch
    {
        EquipableType.Garment or EquipableType.Protector or EquipableType.Armor or EquipableType.Robe
            or EquipableType.LightArmor or EquipableType.HeavyArmor => info.Tid4 switch
        {
            1 => "Head", 2 => "Shoulder", 3 => "Chest", 4 => "Legs", 5 => "Hands", 6 => "Foot", _ => null,
        },
        EquipableType.Avatar => info.Tid4 switch
        {
            1 => "Hat", 2 => "Dress", 3 => "Accessory", 4 => "Flag", _ => null,
        },
        _ => null,
    };

    private static Values? Parse(string equip)
    {
        if (string.IsNullOrEmpty(equip))
            return null;
        var parts = equip.Split('|');
        if (parts.Length != Columns.Length)
            return null;
        var values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);
        return new Values(values);
    }

    /// <summary>The kept columns, read by their itemdata column number.</summary>
    private sealed class Values(double[] values)
    {
        public double Get(int column)
        {
            int index = Array.IndexOf(Columns, column);
            return index < 0 ? 0 : values[index];
        }
    }
}
