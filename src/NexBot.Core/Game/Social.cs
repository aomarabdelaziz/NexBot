using NexBot.Game.Items;

namespace NexBot.Game;

public sealed class PartyMember
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GuildName { get; set; } = string.Empty;
    public byte Level { get; set; }
    public Position Position { get; set; }
    public uint ModelId { get; set; }
    public byte HpMp { get; set; }
    public byte HpPercent => (byte)((HpMp & 15) * 10);
    public byte MpPercent => (byte)((HpMp >> 4) * 10);
    public uint MasteryPrimaryId { get; set; }
    public uint MasterySecondaryId { get; set; }

    public override string ToString() => $"{Name} (Lv {Level})";
}

public sealed class Party
{
    public PartyPurpose Purpose { get; set; }
    public PartySetup Setup { get; set; }

    /// <summary>Members in join order; the first one is the master.</summary>
    public OrderedDictionary<uint, PartyMember> Members { get; } = [];

    public PartyMember? Master => Members.Count > 0 ? Members.GetAt(0).Value : null;
    public int Capacity => Setup.HasFlag(PartySetup.ExpShared) ? 8 : 4;
    public bool IsFull => Members.Count >= Capacity;

    public override string ToString() =>
        $"Exp {(Setup.HasFlag(PartySetup.ExpShared) ? "Shared" : "Free-For-All")} • " +
        $"Item {(Setup.HasFlag(PartySetup.ItemShared) ? "Shared" : "Free-For-All")} • " +
        $"{(Setup.HasFlag(PartySetup.AnyoneCanInvite) ? "Anyone" : "Only Master")} Can Invite";
}

public sealed class PartyMatch
{
    public uint Number { get; set; }
    public uint MasterJoinId { get; set; }
    public string MasterName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public byte LevelMin { get; set; }
    public byte LevelMax { get; set; }
    public PartySetup Setup { get; set; }
    public PartyPurpose Purpose { get; set; }
    public byte MemberCount { get; set; }
    public byte MemberMax => (byte)(Setup.HasFlag(PartySetup.ExpShared) ? 8 : 4);
    public bool IsJobMode => Purpose is PartyPurpose.Thief or PartyPurpose.Trader;
}

public sealed class GuildMember
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte Level { get; set; }
    public uint GuildPoints { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public uint ModelId { get; set; }
    public GuildPermissions Permissions { get; set; }
    public bool IsOffline { get; set; }

    /// <summary>The member's rank in the guild data: 0 = the guild master.</summary>
    public byte Rank { get; set; }

    public bool IsMaster => Rank == 0;
}

public sealed class Guild
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte Level { get; set; }
    /// <summary>Gathered guild points (SP given to the guild).</summary>
    public uint GuildPoints { get; set; }

    /// <summary>The notice's title.</summary>
    public string Notice { get; set; } = string.Empty;

    /// <summary>The notice's text.</summary>
    public string Message { get; set; } = string.Empty;
    public Dictionary<uint, GuildMember> Members { get; } = [];
    public GuildMember? Master { get; set; }
    public ulong StorageGold { get; set; }
    public SlotList<Item> Storage { get; set; } = new();
}
