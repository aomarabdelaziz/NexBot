using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;

namespace NexBot.Automation;

/// <summary>A group of items on the Pick Filter page (the first combo box): its name and its item types (type IDs 2 and 3).</summary>
public sealed record PickCategory(string Name, params (byte Tid2, byte Tid3)[] Types);

/// <summary>The pick filter's decisions (pure: tested): what to pick up, store, sell and drop.</summary>
public static class PickFilter
{
    /// <summary>The item groups, in the order of the combo box.</summary>
    public static IReadOnlyList<PickCategory> Categories { get; } =
    [
        new("Weapons", (1, 6)),
        new("Shields", (1, 4)),
        new("Chinese armor", (1, 1), (1, 2), (1, 3)),
        new("European armor", (1, 9), (1, 10), (1, 11)),
        new("Accessories", (1, 5), (1, 12)),
        new("Job suits", (1, 7)),
        new("Avatars", (1, 13), (1, 14)),
        new("Potions and pills", (3, 1), (3, 2)),
        new("Scrolls", (3, 3)),
        new("Arrows and bolts", (3, 4)),
        new("Gold and coins", (3, 5)),
        new("Elixirs and powders", (3, 10)),
        new("Alchemy stones and tablets", (3, 11)),
        new("Trade goods and event items", (3, 8), (3, 9)),
        new("Pets and summons", (2, 1), (2, 2), (2, 3), (3, 13)),
        new("Other items", (3, 6), (3, 7), (3, 12), (3, 14), (3, 15), (3, 16)),
    ];

    /// <summary>The items of a group, by name; <paramref name="degree"/> 0 = any; <paramref name="rare"/> null = any.</summary>
    public static List<ItemInfo> ItemsOf(IGameData data, PickCategory category, int degree, bool? rare) =>
        [.. category.Types.SelectMany(t => data.GetItemsOfType(t.Tid2, t.Tid3))
            .Where(i => degree == 0 || AlchemyCatalog.DegreeOf(i.ServerName) == degree)
            .Where(i => rare is null || IsRare(i.ServerName) == rare)
            .OrderBy(i => i.Tid2 == 1 ? AlchemyCatalog.DegreeOf(i.ServerName) : 0)
            .ThenBy(i => i.Level)
            .ThenBy(i => i.Name.Length > 0 ? i.Name : i.ServerName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>A rare (Seal of Star / Moon / Sun) item: …_A_RARE, …_B_RARE, …_C_RARE.</summary>
    public static bool IsRare(string serverName) => serverName.Contains("_RARE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rare, or equipment with blue (magic) options.</summary>
    public static bool IsRareOrBlue(Item item) => IsRare(item.ServerName) || item is Equipable { MagicOptions.Count: > 0 };

    /// <summary>Arrows for Chinese characters, bolts for European ones.</summary>
    public static bool IsOwnAmmo(string serverName, bool european) =>
        serverName.StartsWith(european ? "ITEM_ETC_AMMO_BOLT" : "ITEM_ETC_AMMO_ARROW", StringComparison.OrdinalIgnoreCase);

    public static bool IsEuropean(Character c) => c.ServerName.Contains("_EU_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whose drop it is, for picking: the character's own, a party member's, nobody's (free for everyone) or another
    /// player's (the server refuses it until its time runs out).
    /// </summary>
    public static bool MayTake(PickFilterSettings s, Drop drop, uint ownJoinId, IEnumerable<uint> partyJoinIds)
    {
        if (!drop.HasOwner)
            return s.PickOtherPlayersItems;
        if (drop.OwnerJoinId == ownJoinId)
            return true;
        return s.PickPartyItems && partyJoinIds.Contains(drop.OwnerJoinId);
    }

    /// <summary>
    /// The item's name with what tells it apart from the items of the same name (pure: tested): race, seal and event,
    /// e.g. "Gold Ring (European, Seal of Sun)" for ITEM_EU_RING_03_C_RARE.
    /// </summary>
    public static string Label(string name, string serverName)
    {
        var tags = new List<string>();
        if (serverName.StartsWith("ITEM_EVENT_", StringComparison.OrdinalIgnoreCase))
            tags.Add("event");
        if (serverName.Contains("_CH_", StringComparison.OrdinalIgnoreCase))
            tags.Add("Chinese");
        else if (serverName.Contains("_EU_", StringComparison.OrdinalIgnoreCase))
            tags.Add("European");
        if (serverName.EndsWith("_A_RARE", StringComparison.OrdinalIgnoreCase))
            tags.Add("Seal of Star");
        else if (serverName.EndsWith("_B_RARE", StringComparison.OrdinalIgnoreCase))
            tags.Add("Seal of Moon");
        else if (serverName.EndsWith("_C_RARE", StringComparison.OrdinalIgnoreCase))
            tags.Add("Seal of Sun");
        string text = name.Length > 0 ? name : serverName;
        return tags.Count == 0 ? text : $"{text} ({string.Join(", ", tags)})";
    }

    public static string Label(ItemInfo info) => Label(info.Name, info.ServerName);

    /// <summary>
    /// Why this drop is not picked up, or null when it is (pure: tested). Room in the bag is checked apart.
    /// </summary>
    public static string? WhyNot(PickFilterSettings s, Drop drop, bool european, int ammoInBag, uint ownJoinId, IEnumerable<uint> partyJoinIds)
    {
        if (s.DontPickItems)
            return "\"Don't pick items\" is on";
        bool ammo = s.PickArrows && IsOwnAmmo(drop.ServerName, european) && ammoInBag < s.ArrowAmount;
        if (!ammo)
        {
            if (!s.Get(drop.ServerName).HasFlag(PickAction.Pick))
                return $"Pick is not ticked for {drop.ServerName}";
            if (s.OnlyPickRareOrBlue && drop.IsEquipable && drop.Rarity == 0 && !IsRare(drop.ServerName))
                return "it is not rare or blue (\"Only pick rare or blue items\" is on)";
        }
        if (!drop.HasOwner)
            return s.PickOtherPlayersItems ? null : "nobody owns it (dropped by a player): turn on \"Pick other player's items\"";
        if (drop.OwnerJoinId == ownJoinId)
            return null;
        if (partyJoinIds.Contains(drop.OwnerJoinId))
            return s.PickPartyItems ? null : "a party member's drop: turn on \"Pick party items\"";
        return "it belongs to another player (the server lets you take it when its owner time runs out)";
    }

    /// <summary>Pick up this drop? (Ownership and room in the bag are checked apart.) <paramref name="ammoInBag"/>: arrows or bolts the character has.</summary>
    public static bool ShouldPick(PickFilterSettings s, Drop drop, bool european, int ammoInBag)
    {
        if (s.DontPickItems)
            return false;
        if (s.PickArrows && IsOwnAmmo(drop.ServerName, european) && ammoInBag < s.ArrowAmount)
            return true;
        if (!s.Get(drop.ServerName).HasFlag(PickAction.Pick))
            return false;
        if (s.OnlyPickRareOrBlue && drop.IsEquipable)
            return drop.Rarity > 0 || IsRare(drop.ServerName);
        return true;
    }

    /// <summary>Put this bag item into the storage?</summary>
    public static bool ShouldStore(PickFilterSettings s, Item item)
    {
        if (!s.Get(item.ServerName).HasFlag(PickAction.Store))
            return false;
        if (item is not Equipable e)
            return true;
        if (s.OnlyStoreRareOrBlue && !IsRareOrBlue(item))
            return false;
        return !s.OnlyStoreWithBlues || HasBlue(e, s.BlueAttributes);
    }

    /// <summary>Sell this bag item? Items stored are not sold; rare items only when allowed; not with a high plus.</summary>
    public static bool ShouldSell(PickFilterSettings s, Item item)
    {
        if (!s.Get(item.ServerName).HasFlag(PickAction.Sell) || ShouldStore(s, item))
            return false;
        if (!s.AllowSellingAllTypes && IsRare(item.ServerName))
            return false;
        return s.DontSellPlusAtLeast <= 0 || item is not Equipable e || e.Plus < s.DontSellPlusAtLeast;
    }

    /// <summary>Drop this bag item? Items stored or sold are not dropped.</summary>
    public static bool ShouldDrop(PickFilterSettings s, Item item) =>
        s.Get(item.ServerName).HasFlag(PickAction.Drop) && !ShouldStore(s, item) && !ShouldSell(s, item);

    /// <summary>The item has a blue attribute whose name or server name (MATTR_STR ...) starts with one of <paramref name="names"/>.</summary>
    public static bool HasBlue(Equipable item, IReadOnlyList<string> names) =>
        item.MagicOptions.Any(m => names.Any(n => n.Trim().Length > 0
            && (m.Name.StartsWith(n.Trim(), StringComparison.OrdinalIgnoreCase)
                || m.ServerName.StartsWith(n.Trim(), StringComparison.OrdinalIgnoreCase)
                || m.ServerName.StartsWith("MATTR_" + n.Trim(), StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// Gold to move at the Storage-keeper (pure: tested): positive = put into the storage, negative = take out of it,
    /// 0 = nothing. Keeps <see cref="PickFilterSettings.KeepGold"/> in the bag.
    /// </summary>
    public static long GoldToStore(PickFilterSettings s, ulong bagGold, ulong storageGold)
    {
        ulong keep = s.KeepGold;
        if (s.StoreGold && bagGold > keep)
        {
            ulong amount = bagGold - keep;
            if (s.StoreGoldMax > 0)
                amount = storageGold >= s.StoreGoldMax ? 0 : Math.Min(amount, s.StoreGoldMax - storageGold);
            return (long)Math.Min(amount, long.MaxValue);
        }
        if (s.TakeGoldFromStorage && keep > 0 && bagGold < keep && storageGold > 0)
            return -(long)Math.Min(keep - bagGold, storageGold);
        return 0;
    }
}
