namespace NexBot.Configuration;

/// <summary>A switch with a number (phBot's "[x] HP potions &lt;= 50" rows).</summary>
public sealed class ProtectionOption
{
    public ProtectionOption() { }
    public ProtectionOption(bool on, int value)
    {
        On = on;
        Value = value;
    }

    public bool On { get; set; }
    public int Value { get; set; }
}

/// <summary>Protection choices (phBot's Protection tab; saved in settings.json).</summary>
public sealed class ProtectionSettings
{
    // ---- Potions: use when the value drops below the percent ----
    public ProtectionOption HpPotion { get; set; } = new(true, 60);
    public ProtectionOption MpPotion { get; set; } = new(true, 40);
    public ProtectionOption VigorHp { get; set; } = new(false, 30);
    public ProtectionOption VigorMp { get; set; } = new(false, 20);
    /// <summary>Universal pill on any bad status (freeze, burn, poison ...).</summary>
    public bool UniversalPill { get; set; } = true;
    /// <summary>Abnormal state (purification) pill on any bad status, when no universal pill helps.</summary>
    public bool PurificationPill { get; set; } = true;
    /// <summary>Wait at least this long between two potions of the same kind.</summary>
    public int PotionDelayMs { get; set; } = 1000;

    // ---- Return to town (while the bot is walking a script, trading or hunting) ----
    public ProtectionOption HpPotionsLeft { get; set; } = new(true, 10);
    public ProtectionOption MpPotionsLeft { get; set; } = new(true, 5);
    public bool OutOfUniversalPills { get; set; }
    public bool OutOfVigors { get; set; }
    public bool OutOfArrows { get; set; } = true;
    public bool OutOfSpeedScrolls { get; set; }
    /// <summary>Return when this many free inventory slots or fewer are left (phBot: 2).</summary>
    public ProtectionOption InventoryFull { get; set; } = new(true, 2);
    public ProtectionOption WeaponDurability { get; set; } = new(true, 4);
    public ProtectionOption ShieldDurability { get; set; } = new(false, 4);
    public ProtectionOption ArmorDurability { get; set; } = new(false, 4);
    /// <summary>Minutes before the next full hour (events).</summary>
    public ProtectionOption BeforeNextHour { get; set; } = new(false, 5);
    public ProtectionOption EveryMinutes { get; set; } = new(false, 60);
    public bool DailyAt { get; set; }
    /// <summary>Daily return time, "HH:mm".</summary>
    public string DailyTime { get; set; } = "00:00";
    public ProtectionOption PartyBelow { get; set; } = new(false, 2);
    public ProtectionOption NotAttackedMinutes { get; set; } = new(false, 5);
    public bool UniqueNear { get; set; }
    public bool TransportDied { get; set; }
    public bool PinkStatus { get; set; }
    /// <summary>Return when out of Energy of Life (the second-degree Berserk / Hwan items).</summary>
    public bool OutOfEnergyOfLife { get; set; }
    /// <summary>Return to town, then disconnect, every so many minutes.</summary>
    public ProtectionOption ReturnDisconnectMinutes { get; set; } = new(false, 120);
    /// <summary>Wear another weapon of the same kind from the bag when the worn one breaks (instead of returning).</summary>
    public bool ChangeWeaponIfBroken { get; set; }
    /// <summary>Also check the return reasons when no script or trade runs, while the character is outside a town.</summary>
    public bool ReturnOutsideTown { get; set; } = true;

    // ---- Dead ----
    /// <summary>Revive in town when dead and nobody resurrected us within the minutes.</summary>
    public ProtectionOption DeadReturn { get; set; } = new(true, 1);
    public bool DeadOutsideAreaNow { get; set; }
    /// <summary>Use a resurrection scroll (revive where you died) instead of going to town, when one is in the bag.</summary>
    public bool UseResurrectionScroll { get; set; }

    // ---- Scrolls ----
    /// <summary>Parts of item names (speed, damage increase ...): used again whenever their buff is gone.</summary>
    public List<string> Scrolls { get; set; } = [];
    public bool ScrollsWhileBotting { get; set; } = true;
    public bool ScrollsWhenNotBotting { get; set; } = true;

    // ---- Pet return ----
    public bool PetRecoveryKits { get; set; }
    public bool PetRevive { get; set; }
    public bool PetFeed { get; set; }
    public bool PetAbnormal { get; set; }

    // ---- Stats ----
    public bool AutoStats { get; set; }
    /// <summary>Of each level's 3 stat points: this many go to STR, the rest to INT.</summary>
    public int StrPerLevel { get; set; } = 3;

    // ---- Misc ----
    public ProtectionOption StopAfterDeaths { get; set; } = new(false, 3);
    public bool DisconnectOnGm { get; set; }
    public bool DisconnectWhenAttackedByPlayer { get; set; }
    public bool DisconnectOnMurderStatus { get; set; }
    public ProtectionOption DisconnectEveryMinutes { get; set; } = new(false, 240);
    /// <summary>Summon a new job transport from the bag when it dies.</summary>
    public bool RespawnTransport { get; set; }
    /// <summary>Stop the bot when a player in thief job mode is seen in town (auto trade).</summary>
    public bool StopIfThiefInTown { get; set; }

    // ---- Berserk and monster preferences (used by the hunting bot) ----
    public bool BerserkOnMonsterType { get; set; }
    /// <summary>The monster types for berserk, comma-separated: General, Champion, Giant, Titan, Strong, Elite, Unique, Party.</summary>
    public string BerserkMonsterType { get; set; } = "Giant";
    public ProtectionOption BerserkWhenMonstersAttack { get; set; } = new(false, 5);
    public bool BerserkWhileScript { get; set; }
    /// <summary>Monster types (Giant ...) and exact monsters (server names MOB_...), most wanted first.</summary>
    public List<string> MonsterPreferences { get; set; } = [];
    public bool SwitchToPreferred { get; set; }
}
