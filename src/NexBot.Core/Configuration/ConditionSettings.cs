namespace NexBot.Configuration;

/// <summary>What a rule checks (all of a rule's checks must hold).</summary>
public enum IfType
{
    HpBelow,
    MpBelow,
    FreeSlotsBelow,
    ItemCountBelow,
    GoldAbove,
    GoldBelow,
    LevelAtLeast,
    Dead,
    PlayerNear,
    GmNear,
    UniqueNear,
    WhisperReceived,
    StallSoldOut,
    NotInParty,
}

/// <summary>What a rule does, in order.</summary>
public enum ThenType
{
    Log,
    PlaySound,
    UseItem,
    ReturnToTown,
    Whisper,
    Wait,
    StopBot,
    CloseStall,
    LeaveParty,
    Disconnect,
}

public sealed class ConditionIf
{
    public IfType Type { get; set; }
    public int Number { get; set; }
    public string Text { get; set; } = "";
}

public sealed class ConditionThen
{
    public ThenType Type { get; set; }
    public int Number { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>"If … then …" (phBot's Conditions): runs whether the bot is started, stopped, tracing or stalling.</summary>
public sealed class ConditionRule
{
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;
    public List<ConditionIf> Ifs { get; set; } = [];
    public List<ConditionThen> Thens { get; set; } = [];
    /// <summary>While the checks keep holding, run again after this many seconds (0 = only when they become true).</summary>
    public int RepeatSeconds { get; set; }
}

public sealed class ConditionSettings
{
    public List<ConditionRule> Rules { get; set; } = [];

    /// <summary>Readable text for the If / Then lists.</summary>
    public static string Describe(ConditionIf c) => c.Type switch
    {
        IfType.HpBelow => $"HP below {c.Number} %",
        IfType.MpBelow => $"MP below {c.Number} %",
        IfType.FreeSlotsBelow => $"Free inventory slots below {c.Number}",
        IfType.ItemCountBelow => $"Fewer than {c.Number} of \"{c.Text}\"",
        IfType.GoldAbove => $"Gold above {c.Number:N0}",
        IfType.GoldBelow => $"Gold below {c.Number:N0}",
        IfType.LevelAtLeast => $"Level {c.Number} or more",
        IfType.Dead => "The character is dead",
        IfType.PlayerNear => c.Text.Length > 0 ? $"Player \"{c.Text}\" near" : "Any player near" + (c.Number > 0 ? $" (within {c.Number} m)" : ""),
        IfType.GmNear => "A GM is near",
        IfType.UniqueNear => c.Text.Length > 0 ? $"Unique \"{c.Text}\" near" : "Any unique near",
        IfType.WhisperReceived => c.Text.Length > 0 ? $"Whisper containing \"{c.Text}\"" : "Any whisper",
        IfType.StallSoldOut => "The stall sold out",
        IfType.NotInParty => "Not in a party",
        _ => c.Type.ToString(),
    };

    public static string Describe(ConditionThen t) => t.Type switch
    {
        ThenType.Log => $"Log \"{t.Text}\"",
        ThenType.PlaySound => t.Text.Length > 0 ? $"Play {Path.GetFileName(t.Text)}" : "Play the alert sound",
        ThenType.UseItem => $"Use \"{t.Text}\"",
        ThenType.ReturnToTown => "Use a return scroll",
        ThenType.Whisper => $"Whisper {t.Text}",
        ThenType.Wait => $"Wait {t.Number} s",
        ThenType.StopBot => "Stop scripts, trade and alchemy",
        ThenType.CloseStall => "Close the stall",
        ThenType.LeaveParty => "Leave the party",
        ThenType.Disconnect => "Disconnect (stop the bot)",
        _ => t.Type.ToString(),
    };

    /// <summary>What the Number / Text fields mean for each type (empty: not used).</summary>
    public static (string Number, string Text) Fields(IfType t) => t switch
    {
        IfType.HpBelow or IfType.MpBelow => ("Percent", ""),
        IfType.FreeSlotsBelow => ("Slots", ""),
        IfType.ItemCountBelow => ("Count", "Item name (part)"),
        IfType.GoldAbove or IfType.GoldBelow => ("Gold", ""),
        IfType.LevelAtLeast => ("Level", ""),
        IfType.PlayerNear => ("Within m (0 any)", "Name (empty: anyone)"),
        IfType.UniqueNear => ("", "Name (empty: any)"),
        IfType.WhisperReceived => ("", "Contains (empty: any)"),
        _ => ("", ""),
    };

    public static (string Number, string Text) Fields(ThenType t) => t switch
    {
        ThenType.Log => ("", "Message"),
        ThenType.PlaySound => ("", ".wav file (empty: Windows sound)"),
        ThenType.UseItem => ("", "Item name (part)"),
        ThenType.Whisper => ("", "name: message"),
        ThenType.Wait => ("Seconds", ""),
        _ => ("", ""),
    };
}
