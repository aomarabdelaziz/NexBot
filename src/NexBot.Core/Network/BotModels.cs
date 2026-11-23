namespace NexBot.Network;

/// <summary>Where the session is in the login flow.</summary>
public enum BotState
{
    Idle,
    WaitingForClient,
    Gateway,
    Agent,
    CharacterSelection,
    Loading,
    InGame,
    Disconnected,
}

/// <summary>A shard (server) of the 0xA101 list.</summary>
public sealed record ShardInfo(ushort Id, string Name, ushort Online, ushort Capacity, bool IsOpen);

/// <summary>A character of the 0xB007 list.</summary>
public sealed record CharacterInfo(string Name, byte Level, bool IsDeleting);

/// <summary>Server connection settings, resolved from Media.pk2 and optional overrides.</summary>
/// <param name="GatewayHosts">Real gateway hosts; the first one is tried first.</param>
public sealed record ServerProfile(IReadOnlyList<string> GatewayHosts, ushort GatewayPort, uint Version, byte Locale);

/// <summary>Login data and automation switches.</summary>
public sealed record AccountProfile
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";

    /// <summary>Shard (server) name; empty = first open shard.</summary>
    public string ShardName { get; init; } = "";

    /// <summary>Character to join; empty = wait for the user (client mode) / first character (clientless).</summary>
    public string CharacterName { get; init; } = "";

    /// <summary>Answer sent when the gateway asks for a captcha.</summary>
    public string Captcha { get; init; } = "";

    /// <summary>Log in automatically when the shard list arrives.</summary>
    public bool AutoLogin { get; init; } = true;

    /// <summary>Join <see cref="CharacterName"/> automatically when the character list arrives.</summary>
    public bool AutoSelectCharacter { get; init; } = true;

    /// <summary>Keep the character online without a client when the game client is closed in game.</summary>
    public bool ClientlessWhenClientCloses { get; init; } = true;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);
}
