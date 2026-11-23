namespace NexBot.Configuration;

/// <summary>A price for one kind of item (phBot's Inventory and Filter tabs share it).</summary>
public sealed class StallPrice
{
    /// <summary>The item's server name (ITEM_ETC_...).</summary>
    public string Item { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Price for one stall slot.</summary>
    public ulong Price { get; set; }
    /// <summary>
    /// How many go in one stall slot: 0 puts the whole stack; more splits stacks into slots of this size (phBot's
    /// Filter "Quantity": elixirs, global chats ... are sold a few at a time).
    /// </summary>
    public int Quantity { get; set; }
    /// <summary>Added from the Filter tab (any game item) rather than from the inventory.</summary>
    public bool FromFilter { get; set; }
}

/// <summary>Stall choices (phBot's Stall tab; saved in settings.json).</summary>
public sealed class StallSettings
{
    public string Title { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>Open the stall by itself whenever enough items can be stalled.</summary>
    public bool AutoStall { get; set; }

    /// <summary>
    /// Open the stall only when at least this many slots can be filled (phBot's Options value; 0 = whenever any item
    /// can be stalled).
    /// </summary>
    public int MinItems { get; set; }

    /// <summary>When everything sold, close and open again with what is left in the inventory.</summary>
    public bool RefillWhenSoldOut { get; set; } = true;

    public List<StallPrice> Prices { get; set; } = [];

    /// <summary>Add-item bytes after the price learned from the game client (hex; empty = the default).</summary>
    public string AddTrailerHex { get; set; } = "";
    /// <summary>True once <see cref="AddTrailerHex"/> was learned (it may be empty on some servers).</summary>
    public bool AddTrailerLearned { get; set; }

    /// <summary>The enabled price for this item, or null.</summary>
    public StallPrice? PriceOf(string serverName) =>
        Prices.FirstOrDefault(p => p.Enabled && p.Price > 0 && string.Equals(p.Item, serverName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The price entry for this item, added (no price) when missing.</summary>
    public StallPrice Get(string serverName)
    {
        var price = Prices.FirstOrDefault(p => string.Equals(p.Item, serverName, StringComparison.OrdinalIgnoreCase));
        if (price is null)
        {
            price = new StallPrice { Item = serverName };
            Prices.Add(price);
        }
        return price;
    }
}
