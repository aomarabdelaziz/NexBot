namespace NexBot.Configuration;

/// <summary>Things that can play a sound (phBot's Sound tab, in its order).</summary>
public enum SoundEvent
{
    GmSpawned,
    UniqueSpawned,
    TitanSpawned,
    UniqueInRange,
    PlayerDied,
    AttackingPlayer,
    ReturningToTown,
    PrivateMessage,
    Attacked,
    ThiefNearby,
    HunterNearby,
    RareItemDrop,
    TransportDied,
    /// <summary>A unique from <see cref="SoundSettings.Uniques"/> appeared (by the in-game notice).</summary>
    SpecificUnique,
}

/// <summary>One sound: on / off and the file to play (empty: the Windows alert sound).</summary>
public sealed class SoundAlert
{
    public SoundEvent Event { get; set; }
    public bool Enabled { get; set; }
    /// <summary>A .wav file; empty plays the Windows exclamation sound.</summary>
    public string File { get; set; } = "";
}

/// <summary>Sounds for game events (saved in settings.json).</summary>
public sealed class SoundSettings
{
    /// <summary>Mute every sound without losing the choices.</summary>
    public bool Muted { get; set; }

    public List<SoundAlert> Alerts { get; set; } = [];

    /// <summary>Unique names (or parts of names) for <see cref="SoundEvent.SpecificUnique"/>.</summary>
    public List<string> Uniques { get; set; } = [];

    /// <summary>The alert for <paramref name="e"/>, added (off) when the settings file had none.</summary>
    public SoundAlert Get(SoundEvent e)
    {
        var alert = Alerts.FirstOrDefault(a => a.Event == e);
        if (alert is null)
        {
            alert = new SoundAlert { Event = e };
            Alerts.Add(alert);
        }
        return alert;
    }

    /// <summary>Readable name and description for the page.</summary>
    public static (string Name, string Description) Describe(SoundEvent e) => e switch
    {
        SoundEvent.GmSpawned => ("GM spawned", "A game master appears near you"),
        SoundEvent.UniqueSpawned => ("Unique spawned", "Any unique notice, or a unique appears near you"),
        SoundEvent.TitanSpawned => ("Titan spawned", "A titan monster appears near you"),
        SoundEvent.UniqueInRange => ("Unique in range", "A unique appears near you"),
        SoundEvent.PlayerDied => ("Player died", "Your character dies"),
        SoundEvent.AttackingPlayer => ("Attacking another player", "Your character hits another player"),
        SoundEvent.ReturningToTown => ("Returning to town", "A return scroll is used"),
        SoundEvent.PrivateMessage => ("Private message", "Someone whispers you"),
        SoundEvent.Attacked => ("Attacked", "Another player hits your character"),
        SoundEvent.ThiefNearby => ("Thief nearby", "A thief (in job mode) appears near you"),
        SoundEvent.HunterNearby => ("Hunter nearby", "A hunter (in job mode) appears near you"),
        SoundEvent.RareItemDrop => ("Rare item drop", "You pick up a rare (Seal) item"),
        SoundEvent.TransportDied => ("Transport died", "Your job transport dies"),
        SoundEvent.SpecificUnique => ("Specific unique", "A unique from the list below appears (in-game notice)"),
        _ => (e.ToString(), ""),
    };
}
