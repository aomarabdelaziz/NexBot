using NexBot.Game;

namespace NexBot.Configuration;

/// <summary>Where and how to connect.</summary>
public sealed class ServerOptions
{
    /// <summary>Gateway server host name or IP.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Gateway server port.</summary>
    public int Port { get; set; } = 15779;

    /// <summary>Client version sent in the patch request (e.g. 190 for many vSRO 1.188 servers).</summary>
    public uint Version { get; set; } = 190;

    /// <summary>Client locale (22 = vSRO).</summary>
    public byte Locale { get; set; } = 22;

    /// <summary>Static captcha answer, when the gateway asks for one.</summary>
    public string Captcha { get; set; } = string.Empty;

    /// <summary>ANSI code page for in-game strings (1252 Western, 1256 Arabic, 949 Korean ...).</summary>
    public int CodePage { get; set; } = 1252;

    /// <summary>Shard to log into by ID. When null, <see cref="ShardName"/> or the last listed shard is used.</summary>
    public ushort? ShardId { get; set; }

    /// <summary>Shard to log into by name (case-insensitive). Ignored when <see cref="ShardId"/> is set.</summary>
    public string? ShardName { get; set; }
}

/// <summary>One game account driven by one <see cref="Sessions.SroClient"/>.</summary>
public sealed class AccountOptions
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Character to select. Created automatically if missing and <see cref="CreateCharacterIfMissing"/>.</summary>
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>Second password / MaxiGuard character PIN, if the server uses one.</summary>
    public string? PinCode { get; set; }

    public bool CreateCharacterIfMissing { get; set; } = true;
    public CharacterRace CreateRace { get; set; } = CharacterRace.Chinese;
    public bool CreateMale { get; set; } = true;

    /// <summary>Set to false to keep an account in the file without starting it.</summary>
    public bool Enabled { get; set; } = true;

    public override string ToString() => $"{Username}/{CharacterName}";
}

/// <summary>Automatic reconnection with exponential back-off.</summary>
public sealed class ReconnectOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Random extra delay (0..Jitter) so many bots don't reconnect at the same instant. Zero disables it.</summary>
    public TimeSpan Jitter { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum attempts in a row without reaching the game world. 0 = unlimited.</summary>
    public int MaxAttempts { get; set; }
}

/// <summary>Opt-in automation that the legacy client had hard-wired or disabled.</summary>
public sealed class FeatureOptions
{
    /// <summary>Use recovery kits / abnormal pills on the ride or transport pet automatically.</summary>
    public bool AutoPetRecovery { get; set; }

    /// <summary>HP percent at or below which a recovery kit is used.</summary>
    public int PetRecoveryHpPercent { get; set; } = 80;
}

/// <summary>Per-client runtime behaviour.</summary>
public sealed class ClientOptions
{
    public ServerOptions Server { get; set; } = new();
    public ReconnectOptions Reconnect { get; set; } = new();
    public FeatureOptions Features { get; set; } = new();

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>A ping (0x2002) is sent when nothing has been sent for this long.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Disconnect when nothing has been received for this long (dead connection detection).</summary>
    public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
