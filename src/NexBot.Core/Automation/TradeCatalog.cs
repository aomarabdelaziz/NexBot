using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;

namespace NexBot.Automation;

/// <summary>A transport or job suit the player can pick for trading.</summary>
/// <param name="Owned">How many are in the bag (job suits: worn counts too).</param>
/// <param name="CanUse">The character's level is high enough.</param>
public sealed record TradeOption(ItemInfo Info, int Owned, bool CanUse);

/// <summary>What can be used for trading: transport scrolls (ITEM_COS_T_*) and job suits, and where they are.</summary>
public static class TradeCatalog
{
    /// <summary>A transport scroll that carries trade goods (not riding horses, not attack / pick pets).</summary>
    public static bool IsTradeTransport(ItemInfo info) =>
        info.Tid2 == 3 && info.Tid3 == 3 && info.Tid4 == 2 && info.ServerName.StartsWith("ITEM_COS_T_", StringComparison.Ordinal);

    /// <summary>The job of a job suit (trader flag, thief suit, hunter card ...), or null for other items (PvP capes ...).</summary>
    public static JobType? JobOf(ItemInfo info)
    {
        if (info.Tid2 != 1 || info.Tid3 != 7)
            return null;
        if (info.ServerName.Contains("_TRADE_TRADER", StringComparison.Ordinal))
            return JobType.Trader;
        if (info.ServerName.Contains("_TRADE_THIEF", StringComparison.Ordinal))
            return JobType.Thief;
        if (info.ServerName.Contains("_TRADE_HUNTER", StringComparison.Ordinal))
            return JobType.Hunter;
        return null;
    }

    /// <summary>"ITEM_CH_M_" style prefix of the items this character can wear (from its model: CHAR_CH_MAN_ ...), or null.</summary>
    public static string? WearPrefix(Character? character)
    {
        string model = character?.ServerName ?? "";
        string race = model.StartsWith("CHAR_CH_", StringComparison.Ordinal) ? "CH" : model.StartsWith("CHAR_EU_", StringComparison.Ordinal) ? "EU" : "";
        string sex = model.Contains("_WOMAN", StringComparison.Ordinal) ? "F" : model.Contains("_MAN", StringComparison.Ordinal) ? "M" : "";
        return race.Length > 0 && sex.Length > 0 ? $"ITEM_{race}_{sex}_" : null;
    }

    /// <summary>All trade transports in the game: owned first, then by level.</summary>
    public static IReadOnlyList<TradeOption> Transports(IGameData data, Character? character)
    {
        ArgumentNullException.ThrowIfNull(data);
        return [.. data.GetItemsOfType(3, 3).Where(IsTradeTransport)
            .Select(i => new TradeOption(i, Count(character, i.Id, worn: false), character is null || character.Level >= i.Level))
            .OrderByDescending(o => o.Owned > 0).ThenBy(o => o.Info.Level).ThenBy(o => o.Info.Id)];
    }

    /// <summary>The trader suits (flags, bags) this character can wear: owned first, then by level.</summary>
    public static IReadOnlyList<TradeOption> TraderSuits(IGameData data, Character? character)
    {
        const JobType job = JobType.Trader;
        ArgumentNullException.ThrowIfNull(data);
        string? prefix = WearPrefix(character);
        return [.. data.GetItemsOfType(1, 7)
            .Where(i => JobOf(i) == job && (prefix is null || i.ServerName.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(i => new TradeOption(i, Count(character, i.Id, worn: true), character is null || character.Level >= i.Level))
            .OrderByDescending(o => o.Owned > 0).ThenBy(o => o.Info.Level).ThenBy(o => o.Info.Id)];
    }

    /// <summary>Bag slot of the transport scroll to summon: the preferred one, else any trade transport; -1 if none.</summary>
    public static int FindTransportScroll(Character character, string preferred)
    {
        ArgumentNullException.ThrowIfNull(character);
        int any = -1;
        for (int slot = Character.FirstInventorySlot; slot < character.Inventory.Capacity; slot++)
        {
            if (character.Inventory[slot] is not { } item || !IsTradeTransport(item.Info))
                continue;
            if (preferred.Length == 0 || string.Equals(item.ServerName, preferred, StringComparison.OrdinalIgnoreCase))
                return slot;
            if (any < 0)
                any = slot;
        }
        return any;
    }

    /// <summary>Bag slot of the trader suit to wear: the preferred one, else any trader suit; -1 if none.</summary>
    public static int FindTraderSuit(Character character, string preferred)
    {
        const JobType job = JobType.Trader;
        ArgumentNullException.ThrowIfNull(character);
        int any = -1;
        for (int slot = Character.FirstInventorySlot; slot < character.Inventory.Capacity; slot++)
        {
            if (character.Inventory[slot] is not Equipable { IsJob: true } suit || JobOf(suit.Info) != job || suit.LevelRequired > character.Level)
                continue;
            if (preferred.Length == 0 || string.Equals(suit.ServerName, preferred, StringComparison.OrdinalIgnoreCase))
                return slot;
            if (any < 0)
                any = slot;
        }
        return any;
    }

    private static int Count(Character? character, uint itemId, bool worn)
    {
        if (character is null)
            return 0;
        int first = worn ? 0 : Character.FirstInventorySlot, count = 0;
        for (int slot = first; slot < character.Inventory.Capacity; slot++)
            if (character.Inventory[slot] is { } item && item.Id == itemId)
                count += item.QuantityMax > 1 ? item.Quantity : 1;
        return count;
    }
}
