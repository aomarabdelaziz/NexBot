using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using NexBot.Data;
using NexBot.Map;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// The card the game shows over an inventory item: name (rare colour for Seal items), sort of item and level, the
/// description with its "How To Use" and "Restriction" parts, the item's stats (HP recovery[570] ...), magic
/// options, durability, quantity and where it is.
/// </summary>
internal static partial class ItemCard
{
    /// <param name="info">The database row: a finer sort than "Item", the potion values, equipment stats.</param>
    /// <param name="wornBy">
    /// Another player's gear: the server only tells the item and its plus, so the rolled stats, blues and durability
    /// are left out and the card says whose it is.
    /// </param>
    /// <param name="place">Where the item is, for the card's last line, when it is not in our bag or gear.</param>
    public static ItemPreviewInfo For(ItemView item, bool avatar, ItemInfo? info = null, string? wornBy = null, string? place = null)
    {
        if (info is { Tid2: 1 } && EquipmentText.Header(info) is { Count: > 0 } header)
            return Equipment(item, avatar, info, header, wornBy, place);
        var inv = CultureInfo.CurrentCulture;
        string kind = item.Kind == "Item" && info is not null ? Capital(ItemVisuals.Kind(info)) : item.Kind;
        var lines = new List<string>
        {
            "Sort of item: " + kind + (item.Level > 0 ? string.Create(inv, $"  ·  Lv. {item.Level}") : ""),
        };
        if (item.Rarity is { } rarity)
            lines.Add(ItemDetails.Words(rarity));
        var description = Description(item.Description);
        if (description.Count > 0)
        {
            lines.Add("");
            lines.AddRange(description);
        }
        var stats = info is null ? [] : Stats(info);
        if (stats.Count > 0 || item.MagicOptions.Count > 0)
        {
            lines.Add("");
            lines.AddRange(stats);
            lines.AddRange(item.MagicOptions);
        }
        lines.Add("");
        if (item.Durability is { } durability)
            lines.Add(string.Create(inv, $"Durability {durability:N0}"));
        if (item.QuantityMax > 1)
            lines.Add(string.Create(inv, $"Quantity {item.Quantity:N0}"));
        lines.Add(place ?? (wornBy is null ? Where(item, avatar) : WornBy(wornBy, avatar)));
        return new ItemPreviewInfo(item.Icon, item.FullName, string.Join("\n", lines), item.Rarity is null ? null : Sox);
    }

    private static string WornBy(string player, bool avatar) => (avatar ? "Avatar worn by " : "Worn by ") + player;

    /// <summary>
    /// Worn, avatar and bag equipment like the game's card: sort, mounting part, degree; white stats with their
    /// rolls; magic options (blue); required level, gender, race, max magic options, Advanced elixir; description.
    /// </summary>
    private static ItemPreviewInfo Equipment(ItemView item, bool avatar, ItemInfo info, List<string> header, string? wornBy, string? place)
    {
        var lines = header.Select(h => ItemPreview.OrangeMark + h).ToList();
        if (item.Rarity is { } rarity)
            lines.Add(ItemDetails.Words(rarity));
        var stats = wornBy is not null ? ["The server does not send another player's stat rolls, blues or durability."]
            : EquipmentText.Stats(info, item.Plus, item.Variance, item.Durability);
        if (stats.Count == 0 && item.Durability is { } durability)
            stats.Add(string.Create(CultureInfo.CurrentCulture, $"Durability {durability:N0}"));
        if (stats.Count > 0)
        {
            lines.Add("");
            lines.AddRange(stats);
        }
        if (item.MagicOptions.Count > 0)
        {
            lines.Add("");
            lines.AddRange(item.MagicOptions.Select(m => ItemPreview.BlueMark + m));
        }
        var requirements = EquipmentText.Requirements(info);
        if (requirements.Count > 0)
        {
            lines.Add("");
            lines.AddRange(requirements.Select(r => r.StartsWith("Max No.", StringComparison.Ordinal) ? ItemPreview.OrangeMark + r
                : r.StartsWith("Able to", StringComparison.Ordinal) ? ItemPreview.HeadingMark + r : r));
        }
        var description = Description(item.Description);
        if (description.Count > 0)
        {
            lines.Add("");
            lines.AddRange(description);
        }
        lines.Add("");
        lines.Add(place ?? (wornBy is null ? Where(item, avatar) : WornBy(wornBy, avatar)));
        return new ItemPreviewInfo(item.Icon, item.FullName, string.Join("\n", lines), item.Rarity is null ? null : Sox);
    }

    private static string Where(ItemView item, bool avatar) =>
        item.SlotName is not null ? (avatar ? "Avatar · " : "Worn · ") + item.SlotName
            : string.Create(CultureInfo.InvariantCulture, $"Bag slot {item.Slot - CharacterSheet.FirstBagSlot + 1}");

    /// <summary>
    /// The game's description markup as card lines: &lt;br&gt; breaks the line, a &lt;strong&gt; part is a heading,
    /// other tags (&lt;sml2&gt;, &lt;font&gt;) are dropped.
    /// </summary>
    internal static List<string> Description(string markup)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(markup))
            return lines;
        foreach (string part in BreakTag().Split(markup))
        {
            bool heading = part.Contains("<strong>", StringComparison.OrdinalIgnoreCase);
            string text = WebUtility.HtmlDecode(AnyTag().Replace(part, "")).Trim();
            if (text.Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0)
                    lines.Add("");
                continue;
            }
            lines.Add(heading ? ItemPreview.HeadingMark + text : text);
        }
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>The item's own values, as the game lists them under the description (potions: HP / MP recovery).</summary>
    internal static List<string> Stats(ItemInfo info)
    {
        var lines = new List<string>();
        if (info.Tid2 != 3 || info.Tid3 != 1 || info.Params.Length == 0)
            return lines;
        var values = info.Params.Split('|').Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0).ToArray();
        string[] names = ["HP recovery[{0}]", "HP recovery[{0}%]", "MP recovery[{0}]", "MP recovery[{0}%]"];
        for (int i = 0; i < Math.Min(values.Length, names.Length); i++)
            if (values[i] > 0)
                lines.Add(string.Format(CultureInfo.InvariantCulture, names[i], values[i]));
        return lines;
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex AnyTag();
}
