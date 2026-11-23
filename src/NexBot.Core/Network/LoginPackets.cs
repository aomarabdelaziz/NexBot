using NexBot.Protocol;

namespace NexBot.Network;

/// <summary>vSRO 1.188 opcodes of the login flow handled by <see cref="BotSession"/>.</summary>
public static class LoginOpcodes
{
    // Both directions
    public const ushort Identity = 0x2001;
    public const ushort KeepAlive = 0x2002;

    // Client -> server
    public const ushort PatchRequest = 0x6100;
    public const ushort ShardListRequest = 0x6101;
    public const ushort LoginRequest = 0x6102;
    public const ushort AuthRequest = 0x6103;
    public const ushort CaptchaAnswer = 0x6323;
    public const ushort CharacterSelectionJoin = 0x7001;
    public const ushort CharacterSelectionAction = 0x7007;
    public const ushort ConfirmSpawn = 0x3012;
    public const ushort TeleportReadyResponse = 0x34B6;

    // Server -> client
    public const ushort PatchResponse = 0xA100;
    public const ushort ShardListResponse = 0xA101;
    public const ushort LoginResponse = 0xA102;
    public const ushort AuthResponse = 0xA103;
    public const ushort CaptchaImage = 0x2322;
    public const ushort CaptchaResponse = 0xA323;
    public const ushort CharacterSelectionJoinResponse = 0xB001;
    public const ushort CharacterSelectionActionResponse = 0xB007;
    public const ushort CelestialPosition = 0x3020;
    public const ushort LoadingStart = 0x34A5;
    public const ushort LoadingEnd = 0x34A6;
    public const ushort TeleportReadyRequest = 0x34B5;
}

/// <summary>Builders for the login packets the bot sends itself.</summary>
public static class LoginPackets
{
    public static Packet PatchRequest(byte locale, uint version) =>
        new Packet(LoginOpcodes.PatchRequest, encrypted: true).WriteByte(locale).WriteAscii("SR_Client").WriteUInt32(version);

    public static Packet ShardListRequest() => new(LoginOpcodes.ShardListRequest);

    public static Packet LoginRequest(byte locale, string username, string password, ushort shardId) =>
        new Packet(LoginOpcodes.LoginRequest, encrypted: true)
            .WriteByte(locale).WriteAscii(username).WriteAscii(password).WriteUInt16(shardId);

    public static Packet CaptchaAnswer(string code) => new Packet(LoginOpcodes.CaptchaAnswer).WriteAscii(code);

    public static Packet AuthRequest(uint token, string username, string password, byte locale) =>
        new Packet(LoginOpcodes.AuthRequest, encrypted: true)
            .WriteUInt32(token).WriteAscii(username).WriteAscii(password).WriteByte(locale)
            .WriteUInt32(0).WriteUInt16(0); // MAC address

    public static Packet CharacterList() => new Packet(LoginOpcodes.CharacterSelectionAction).WriteByte(2);

    public static Packet SelectCharacter(string name) => new Packet(LoginOpcodes.CharacterSelectionJoin).WriteAscii(name);

    public static Packet KeepAlive() => new(LoginOpcodes.KeepAlive);

    public static Packet ConfirmSpawn() => new(LoginOpcodes.ConfirmSpawn);

    public static Packet TeleportReady() => new(LoginOpcodes.TeleportReadyResponse);
}
