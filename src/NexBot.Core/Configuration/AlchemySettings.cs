namespace NexBot.Configuration;

/// <summary>Alchemy choices (phBot's Alchemy tab; saved in settings.json).</summary>
public sealed class AlchemySettings
{
    // ---- Plus ----
    public int StopAt { get; set; } = 7;
    public int SuccessDelayMs { get; set; } = 1500;
    public int FailureDelayMs { get; set; } = 2500;
    /// <summary>Use lucky powder from <see cref="LuckyPowderAt"/> on. Null in settings saved before the switch existed:
    /// then a plus above 0 means on.</summary>
    public bool? UseLuckyPowder { get; set; }
    /// <summary>The plus from which lucky powder is used (0 = from the start).</summary>
    public int LuckyPowderAt { get; set; }

    /// <summary>True when lucky powder is switched on.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LuckyPowderOn => UseLuckyPowder ?? LuckyPowderAt > 0;

    /// <summary>Lucky powder degree (0 = Auto: the item's degree). Some servers change powder degrees.</summary>
    public int LuckyPowderDegree { get; set; }

    // Each stone: a switch and the plus it starts at (0 = from the start). A null switch comes from settings saved
    // before the switches existed: then a plus above 0 means on.
    public bool? UseAstral { get; set; }
    public int AstralAt { get; set; }
    public bool? UseSteady { get; set; }
    public int SteadyAt { get; set; }
    public bool? UseImmortal { get; set; }
    public int ImmortalAt { get; set; }
    public bool? UseLuckyStone { get; set; }
    public int LuckyStoneAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool AstralOn => UseAstral ?? AstralAt > 0;
    [System.Text.Json.Serialization.JsonIgnore] public bool SteadyOn => UseSteady ?? SteadyAt > 0;
    [System.Text.Json.Serialization.JsonIgnore] public bool ImmortalOn => UseImmortal ?? ImmortalAt > 0;
    [System.Text.Json.Serialization.JsonIgnore] public bool LuckyStoneOn => UseLuckyStone ?? LuckyStoneAt > 0;

    /// <summary>Stop an item after <see cref="StopAfterAttempts"/> elixirs / stones.</summary>
    public bool? UseStopAfterAttempts { get; set; }
    public int StopAfterAttempts { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool StopAfterOn => (UseStopAfterAttempts ?? StopAfterAttempts > 0) && StopAfterAttempts > 0;
    /// <summary>Go to the next queued item after a failed plus.</summary>
    public bool MoveOnAfterFailure { get; set; }
    /// <summary>Stop the whole queue when an item is destroyed.</summary>
    public bool StopAllIfDestroyed { get; set; } = true;
    /// <summary>Item degree for powders and stones (0 = from the item's name). Some servers change degrees.</summary>
    public int DegreeOverride { get; set; }

    // ---- Attribute / Alchemic ----
    /// <summary>Stop attribute stones when the white value reaches this percent.</summary>
    public int AttributeStopPercent { get; set; } = 80;
    /// <summary>Stop alchemic stones when the blue value reaches this value (not a percent).</summary>
    public int AlchemicStopValue { get; set; } = 5;
    public bool UseCatalyst { get; set; }

    // ---- packet layout (learned from the game client) ----
    /// <summary>Type byte of an elixir fuse (0x7150).</summary>
    public byte ElixirType { get; set; } = 3;
    /// <summary>Type byte of a magic (blue / plus) stone fuse (0x7151).</summary>
    public byte MagicStoneType { get; set; } = 4;
    /// <summary>Type byte of an attribute (white) stone fuse (0x7151).</summary>
    public byte AttributeStoneType { get; set; } = 5;
    /// <summary>Hex of the last alchemy packet the game client sent (shown on the page).</summary>
    public string LearnedFrom { get; set; } = "";
}
