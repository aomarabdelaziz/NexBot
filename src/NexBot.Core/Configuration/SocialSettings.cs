using NexBot.Game;

namespace NexBot.Configuration;

/// <summary>Party choices (phBot's Party tab).</summary>
public sealed class PartySettings
{
    /// <summary>Answer party invitations at all (off: never accept).</summary>
    public bool AcceptInvites { get; set; }
    /// <summary>Accept every invitation, ignoring the list.</summary>
    public bool AcceptAll { get; set; }
    /// <summary>Accept invitations from players in <see cref="AcceptInviteList"/>.</summary>
    public bool AcceptFromList { get; set; } = true;
    /// <summary>Refuse invitations from players not in the list (otherwise they just expire).</summary>
    public bool RefuseNotInList { get; set; }
    /// <summary>Seconds to wait before accepting.</summary>
    public int AcceptDelay { get; set; } = 1;

    /// <summary>Send invitations at all.</summary>
    public bool InvitePlayers { get; set; }
    /// <summary>Invite every nearby player, ignoring the list.</summary>
    public bool InviteAll { get; set; }
    /// <summary>Invite nearby players in <see cref="AcceptInviteList"/>.</summary>
    public bool InviteFromList { get; set; } = true;

    /// <summary>Leave when the party master is not in <see cref="LeaderList"/> (empty list: never leave).</summary>
    public bool LeaveIfLeaderNotInList { get; set; }

    /// <summary>Exp / item sharing and who can invite, for parties we create.</summary>
    public PartySetup Setup { get; set; } = PartySetup.All;

    public List<string> LeaderList { get; set; } = [];
    public List<string> AcceptInviteList { get; set; } = [];

    // ---- matching ----
    public string MatchTitle { get; set; } = "NexBot party";
    public byte MatchLevelMin { get; set; } = 1;
    public byte MatchLevelMax { get; set; } = 140;
    /// <summary>Keep our party in the party matching list (formed again every 10 minutes).</summary>
    public bool AutoFormMatch { get; set; }
    /// <summary>Accept players who ask to join our match (by the accept rules above).</summary>
    public bool AcceptMatchJoins { get; set; } = true;
    /// <summary>When not in a party, join the match of this master.</summary>
    public string JoinByName { get; set; } = "";
    /// <summary>When not in a party, join a match whose title contains this.</summary>
    public string JoinByTitle { get; set; } = "";
}

/// <summary>Guild choices (phBot's Guild tab).</summary>
public sealed class GuildSettings
{
    public bool AcceptAllInvites { get; set; }
    /// <summary>Invite nearby players without a guild whose level (estimated from their gear) is above <see cref="InviteAboveLevel"/>.</summary>
    public bool AutoInvite { get; set; }
    public int InviteAboveLevel { get; set; } = 30;
}

/// <summary>Academy choices (phBot's Academy tab).</summary>
public sealed class AcademySettings
{
    public bool AcceptInvites { get; set; }
    public bool AcceptAll { get; set; }
    public bool AcceptFromList { get; set; } = true;
    public bool RefuseNotInList { get; set; }

    public bool InvitePlayers { get; set; }
    public bool InviteAll { get; set; }
    public bool InviteFromList { get; set; } = true;

    /// <summary>A player who whispers this is invited (empty: off).</summary>
    public string Password { get; set; } = "";

    public List<string> AcceptInviteList { get; set; } = [];
}

/// <summary>Following a player (phBot's Players tab).</summary>
public sealed class TraceSettings
{
    public string Player { get; set; } = "";
    /// <summary>Use the game's own trace instead of walking to the player.</summary>
    public bool GameTrace { get; set; }
    /// <summary>Walk again when further than this (metres).</summary>
    public int Distance { get; set; } = 5;
}

/// <summary>All social automation (saved in settings.json).</summary>
public sealed class SocialSettings
{
    public PartySettings Party { get; set; } = new();
    public GuildSettings Guild { get; set; } = new();
    public AcademySettings Academy { get; set; } = new();
    public TraceSettings Trace { get; set; } = new();
    public ExchangeSettings Exchange { get; set; } = new();
}

/// <summary>Answering exchange requests by itself (Inventory page, Exchange).</summary>
public sealed class ExchangeSettings
{
    /// <summary>Accept exchange requests from other players by itself.</summary>
    public bool AutoAccept { get; set; }
    /// <summary>Also accept while the bot is busy (walking a script, the trade route or the town loop).</summary>
    public bool AcceptWhileBotting { get; set; }
    /// <summary>Accept only when the character is in a town (a shop NPC near).</summary>
    public bool TownOnly { get; set; } = true;
}
