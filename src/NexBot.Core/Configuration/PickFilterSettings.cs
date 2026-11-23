namespace NexBot.Configuration;

/// <summary>What to do with one item (phBot's pick filter): pick it up, put it in the storage, sell it, drop it.</summary>
[Flags]
public enum PickAction
{
    None = 0,
    Pick = 1,
    Store = 2,
    Sell = 4,
    Drop = 8,
}

/// <summary>The pick filter (phBot's Pick Filter tab): per item what to do with it, the pick options and the gold.</summary>
public sealed class PickFilterSettings
{
    /// <summary>What to do with each item, by server name (upper case). Items not listed are not picked, stored, sold or dropped.</summary>
    public Dictionary<string, PickAction> Items { get; set; } = [];

    /// <summary>Pick up items: the grab pet (when it is out) and, when nothing else is going on, the character.</summary>
    public bool Enabled { get; set; }

    /// <summary>When several monsters attack, pick up the drop before killing the others (the hunting bot).</summary>
    public bool PickItemsFirst { get; set; } = true;
    /// <summary>Use the grab pet to pick up items instead of the character.</summary>
    public bool UsePickPet { get; set; } = true;
    /// <summary>With no grab pet out (or its bag full), the character picks up.</summary>
    public bool PickWithCharacterIfNoPet { get; set; } = true;
    /// <summary>Pick up items that belong to nobody (other players' drops whose time ran out, items players dropped).</summary>
    public bool PickOtherPlayersItems { get; set; }
    /// <summary>Pick up items that dropped for a party member.</summary>
    public bool PickPartyItems { get; set; } = true;
    /// <summary>Pick up nothing, whatever the filter says.</summary>
    public bool DontPickItems { get; set; }
    /// <summary>Only pick up rare (SoX) or blue equipment.</summary>
    public bool OnlyPickRareOrBlue { get; set; }
    /// <summary>Pick up arrows (Chinese) or bolts (European) until the bag has this many.</summary>
    public bool PickArrows { get; set; }
    public int ArrowAmount { get; set; } = 1000;

    /// <summary>Sell rare (SoX) items too; off: rare items are never sold.</summary>
    public bool AllowSellingAllTypes { get; set; }
    /// <summary>Do not sell equipment with this plus or more (0: no limit).</summary>
    public int DontSellPlusAtLeast { get; set; }
    /// <summary>Only store rare (SoX) or blue equipment; other equipment marked to store is sold or kept.</summary>
    public bool OnlyStoreRareOrBlue { get; set; }
    /// <summary>Only store equipment with one of the blue attributes below.</summary>
    public bool OnlyStoreWithBlues { get; set; }
    /// <summary>Blue attributes (parts of names: "Str", "Int", "Durability", "MATTR_LUCK" ...).</summary>
    public List<string> BlueAttributes { get; set; } = [];

    /// <summary>Gold to keep in the bag (0: off). Below it, gold is taken from the storage when asked to.</summary>
    public ulong KeepGold { get; set; }
    /// <summary>Take gold from the storage when the bag has less than <see cref="KeepGold"/>.</summary>
    public bool TakeGoldFromStorage { get; set; }
    /// <summary>Put the gold over <see cref="KeepGold"/> into the storage.</summary>
    public bool StoreGold { get; set; }
    /// <summary>The most gold to have in the storage (0: no limit).</summary>
    public ulong StoreGoldMax { get; set; }

    public PickAction Get(string serverName) =>
        Items.TryGetValue(serverName.ToUpperInvariant(), out var action) ? action : PickAction.None;

    public void Set(string serverName, PickAction action)
    {
        string key = serverName.ToUpperInvariant();
        if (action == PickAction.None)
            Items.Remove(key);
        else
            Items[key] = action;
    }
}
