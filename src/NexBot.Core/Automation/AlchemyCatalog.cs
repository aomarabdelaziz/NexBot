using System.Text.RegularExpressions;
using NexBot.Game;
using NexBot.Game.Items;

namespace NexBot.Automation;

/// <summary>The magic stones phBot can use while plussing (each adds an option to the item once).</summary>
public enum PlusStone { Lucky, Steady, Astral, Immortal }

/// <summary>Which stones and elixirs fit which items, item degrees, white stats and blue values (vSRO server names).</summary>
public static partial class AlchemyCatalog
{
    [GeneratedRegex(@"_(\d{2})(?:_|$)")]
    private static partial Regex DegreeToken();

    /// <summary>Equipment degree from the server name (ITEM_CH_SWORD_08_A_RARE → 8), or 0.</summary>
    public static int DegreeOf(string serverName)
    {
        var match = DegreeToken().Match(serverName);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>A stone's or powder's degree: the last two digits (…_PROB_UP_A_08 → 8), or 0.</summary>
    public static int MaterialDegree(string serverName)
    {
        int end = serverName.Length;
        return end >= 2 && char.IsDigit(serverName[end - 1]) && char.IsDigit(serverName[end - 2])
            ? (serverName[end - 2] - '0') * 10 + serverName[end - 1] - '0'
            : 0;
    }

    /// <summary>Equipment that alchemy works on (not job suits, avatars or devil spirits).</summary>
    public static bool CanAlchemy(Item item) =>
        item is Equipable { IsJob: false, IsAvatar: false } e && e.EquipableType != EquipableType.DevilSpirit;

    private static string ElixirKind(Equipable e) => e.EquipableType switch
    {
        EquipableType.Weapon => "WEAPON",
        EquipableType.Shield => "SHIELD",
        EquipableType.AccessoryChinese or EquipableType.AccessoryEuropean => "ACCESSARY",
        _ => "ARMOR",
    };

    /// <summary>Intensifying elixirs (…_REINFORCE_RECIPE_WEAPON_B ...) that fit this item.</summary>
    public static bool IsElixirFor(Item elixir, Equipable item) =>
        elixir.ServerName.Contains("_REINFORCE_RECIPE_", StringComparison.Ordinal)
        && elixir.ServerName.Contains("_" + ElixirKind(item), StringComparison.Ordinal);

    /// <summary>Lucky powder (…_REINFORCE_PROB_UP_…) of the item's degree.</summary>
    public static bool IsPowderFor(Item powder, int degree) =>
        powder.ServerName.Contains("_REINFORCE_PROB_UP", StringComparison.Ordinal) && MaterialDegree(powder.ServerName) == degree;

    private static string StoneToken(PlusStone stone) => stone switch
    {
        PlusStone.Lucky => "_LUCK_",
        PlusStone.Steady => "_SOLID_",
        PlusStone.Astral => "_ASTRAL_",
        _ => "_ATHANASIA_",
    };

    private static string OptionToken(PlusStone stone) => stone switch
    {
        PlusStone.Lucky => "LUCK",
        PlusStone.Steady => "SOLID",
        PlusStone.Astral => "ASTRAL",
        _ => "ATHANASIA",
    };

    /// <summary>The magic stone (…_MAGICSTONE_ATHANASIA_08 ...) of this kind and degree.</summary>
    public static bool IsPlusStone(Item stone, PlusStone kind, int degree) =>
        stone.ServerName.Contains("_MAGICSTONE", StringComparison.Ordinal)
        && stone.ServerName.Contains(StoneToken(kind), StringComparison.Ordinal)
        && MaterialDegree(stone.ServerName) == degree;

    /// <summary>The item already has the option this stone gives.</summary>
    public static bool HasStoneOption(Equipable item, PlusStone kind) =>
        item.MagicOptions.Any(o => o.ServerName.Contains(OptionToken(kind), StringComparison.Ordinal));

    // ---- blue (alchemic / magic stones) ----

    /// <summary>Magic stones that add blue stats (not the four plus stones), e.g. …_MAGICSTONE_STR_08.</summary>
    public static bool IsBlueStone(Item stone) =>
        stone.ServerName.Contains("_MAGICSTONE_", StringComparison.Ordinal)
        && !Enum.GetValues<PlusStone>().Any(k => stone.ServerName.Contains(StoneToken(k), StringComparison.Ordinal));

    /// <summary>The stat a blue stone gives (…_MAGICSTONE_STR_08 → STR).</summary>
    public static string BlueToken(string stoneServerName)
    {
        int at = stoneServerName.IndexOf("_MAGICSTONE_", StringComparison.Ordinal);
        if (at < 0)
            return "";
        string rest = stoneServerName[(at + "_MAGICSTONE_".Length)..];
        int end = rest.LastIndexOf('_');
        return end > 0 ? rest[..end] : rest;
    }

    /// <summary>The item's value of the blue stat this stone gives (0 when it has none yet).</summary>
    public static uint BlueValue(Equipable item, string stoneServerName)
    {
        string token = BlueToken(stoneServerName);
        if (token.Length == 0)
            return 0;
        return item.MagicOptions.FirstOrDefault(o => o.ServerName.EndsWith("_" + token, StringComparison.Ordinal)
                                                      || o.ServerName.Contains("_" + token + "_", StringComparison.Ordinal))?.Value ?? 0;
    }

    // ---- white (attribute stones) ----

    /// <summary>Attribute stones, e.g. …_ATTRSTONE_PA_08.</summary>
    public static bool IsAttributeStone(Item stone) => stone.ServerName.Contains("_ATTRSTONE_", StringComparison.Ordinal);

    /// <summary>The stat an attribute stone raises (…_ATTRSTONE_PA_08 → PA).</summary>
    public static string WhiteToken(string stoneServerName)
    {
        int at = stoneServerName.IndexOf("_ATTRSTONE_", StringComparison.Ordinal);
        if (at < 0)
            return "";
        string rest = stoneServerName[(at + "_ATTRSTONE_".Length)..];
        int end = rest.LastIndexOf('_');
        return end > 0 ? rest[..end] : rest;
    }

    /// <summary>White stats in the order they are packed in the item's variance (5 bits each), per item class.</summary>
    private static string[] WhiteOrder(Equipable item) => item.EquipableType switch
    {
        EquipableType.Weapon => ["DUR", "PAR", "MAR", "HR", "PA", "MA", "CR"],
        EquipableType.Shield => ["DUR", "PAR", "MAR", "BR", "PD", "MD"],
        EquipableType.AccessoryChinese or EquipableType.AccessoryEuropean => ["PAS", "MAS"],
        _ => ["DUR", "PAR", "MAR", "PD", "MD", "ER"],
    };

    /// <summary>The white value (0-100 %) the stone works on, or null when the stone's stat is not known.</summary>
    public static int? WhitePercent(Equipable item, string stoneServerName)
    {
        string token = WhiteToken(stoneServerName);
        int index = Array.IndexOf(WhiteOrder(item), token);
        if (index < 0)
            return null;
        int value = (int)((item.Variance >> (index * 5)) & 0x1F);
        return (int)Math.Round(value * 100.0 / 31);
    }

    /// <summary>Alchemy catalysts (stop a stone from failing).</summary>
    public static bool IsCatalyst(Item item) =>
        item.ServerName.Contains("_ARCHEMY_CATALYST", StringComparison.Ordinal) || item.ServerName.Contains("_ALCHEMY_CATALYST", StringComparison.Ordinal);
}
