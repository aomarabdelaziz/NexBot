namespace NexBot.Configuration;

/// <summary>How a BUY into the transport decides what and how much to buy.</summary>
public enum TradeBuyMode
{
    /// <summary>Exactly what the script recorded.</summary>
    AsRecorded,
    /// <summary>The recorded goods, until the server refuses (the transport is full).</summary>
    FillTransport,
    /// <summary>The enabled goods of the goods list that this trader sells, with their quantities.</summary>
    GoodsList,
}

/// <summary>What the character does with the transport on the road.</summary>
public enum TradeMountMode
{
    /// <summary>Always ride; never fight back (bring enough recovery kits for the transport).</summary>
    StayOn,
    /// <summary>Always walk next to the transport (needed to fight thieves).</summary>
    StayOff,
    /// <summary>Get off to fight, and back on when nothing attacks.</summary>
    Remount,
}

/// <summary>One trade good of the goods list.</summary>
public sealed class TradeGood
{
    /// <summary>Server name (ITEM_ETC_TRADE_CH_01 ...).</summary>
    public string Item { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>How many to buy at each visit.</summary>
    public int Quantity { get; set; }
}

/// <summary>Auto trade choices (saved in settings.json), modelled on phBot's Trade tab.</summary>
public sealed class TradeSettings
{
    /// <summary>Server name of the preferred trade transport scroll (ITEM_COS_T_...); empty: any trade transport.</summary>
    public string Transport { get; set; } = "";

    /// <summary>Server name of the preferred trader suit (flag / bag); empty: any trader suit. Auto trade is for traders only.</summary>
    public string JobSuit { get; set; } = "";

    /// <summary>Put the trader suit on before a script summons the trade transport (RIDE,TRANSPORT).</summary>
    public bool WearJobSuit { get; set; } = true;

    // ---- loop -----------------------------------------------------------------------------------

    /// <summary>The trade loops, in order: script files recorded on the Script page (e.g. Jangan → Donwhang).</summary>
    public List<string> Loops { get; set; } = [];

    /// <summary>Run the loops again when the last one is done.</summary>
    public bool RepeatLoops { get; set; }

    /// <summary>How many times to run the loops when repeating (0 = until stopped).</summary>
    public int LoopCount { get; set; }

    /// <summary>Use a return scroll after the last loop (with one loop: buy, sell, return, buy again ...).</summary>
    public bool ReturnAfterLoops { get; set; }

    // ---- goods ----------------------------------------------------------------------------------

    public TradeBuyMode BuyMode { get; set; } = TradeBuyMode.AsRecorded;

    public List<TradeGood> Goods { get; set; } = [];

    // ---- road -----------------------------------------------------------------------------------

    public TradeMountMode MountMode { get; set; } = TradeMountMode.StayOn;

    /// <summary>Attack thieves that spawn around the transport (only when not staying on the transport).</summary>
    public bool AttackThieves { get; set; }

    /// <summary>Thieves this close to the transport are attacked (metres).</summary>
    public int AttackRadius { get; set; } = 30;

    /// <summary>The goods list entry for an item, created when missing.</summary>
    public TradeGood Good(string item)
    {
        var good = Goods.FirstOrDefault(g => string.Equals(g.Item, item, StringComparison.OrdinalIgnoreCase));
        if (good is null)
        {
            good = new TradeGood { Item = item };
            Goods.Add(good);
        }
        return good;
    }
}
