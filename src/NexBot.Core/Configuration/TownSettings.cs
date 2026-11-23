namespace NexBot.Configuration;

/// <summary>One item of the town buy list: keep this many in the bag.</summary>
public sealed class TownBuyItem
{
    /// <summary>The item's server name (ITEM_ETC_HP_POTION_05 ...).</summary>
    public string Item { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>How many to have after buying (what is already in the bag counts).</summary>
    public int Quantity { get; set; }
}

/// <summary>Town choices (phBot's Town tab; saved in settings.json).</summary>
public sealed class TownSettings
{
    public List<TownBuyItem> Buy { get; set; } = [];

    /// <summary>
    /// Walking scripts through the towns (one per town), recorded on the Script page. The town loop walks the one that
    /// starts near the character and visits every shop NPC on the way.
    /// </summary>
    public List<string> Scripts { get; set; } = [];

    /// <summary>
    /// When no town script starts near the character, find the way to each shop NPC by itself from the client's map
    /// data (buildings and walls), and to the training script's start. Turned off for now: the client's map data does
    /// not match the server everywhere and the character got stuck. Not saved and not shown, so it stays off.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AutoRoute { get; set; }

    /// <summary>A script walked after the town script, to the training area (empty: none).</summary>
    public string TrainingScript { get; set; } = "";
    /// <summary>Loop the training script when it ends (walk the training route until stopped).</summary>
    public bool LoopTrainingScript { get; set; }

    /// <summary>Run the town loop by itself after a return scroll or a revive in town.</summary>
    public bool RunAfterReturn { get; set; } = true;
    public bool Repair { get; set; } = true;
    /// <summary>Repair only the worn equipment (one by one) instead of every item.</summary>
    public bool RepairEquippedOnly { get; set; }
    /// <summary>Sell potions, arrows and bolts over the amounts set to buy.</summary>
    public bool SellExcess { get; set; }
    /// <summary>Stop if the bag fills up while buying.</summary>
    public bool StopIfInventoryFull { get; set; } = true;
    /// <summary>Items to sell at any shop: parts of names or server names (until the pick filter exists).</summary>
    public List<string> Sell { get; set; } = [];
    /// <summary>Items to drop on the ground when the town loop starts: parts of names or server names.</summary>
    public List<string> Drop { get; set; } = [];
    /// <summary>Items to put into the storage at the Storage-keeper: parts of names or server names.</summary>
    public List<string> Store { get; set; } = [];

    public TownBuyItem Get(string serverName)
    {
        var item = Buy.FirstOrDefault(b => string.Equals(b.Item, serverName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new TownBuyItem { Item = serverName, Enabled = false };
            Buy.Add(item);
        }
        return item;
    }
}
