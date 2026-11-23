using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Protocol;

namespace NexBot.Sessions;

/// <summary>Lifecycle of a <see cref="SroClient"/>.</summary>
public enum ClientState
{
    Idle,
    ConnectingGateway,
    Gateway,
    ConnectingAgent,
    CharacterSelection,
    Loading,
    InGame,
    Reconnecting,
    Stopped,
}

/// <summary>Why the login failed. See <see cref="LoginFailure.IsFatal"/>.</summary>
public enum LoginFailureReason
{
    Unknown,
    WrongVersion,
    WrongPassword,
    Banned,
    ServerInspection,
    AlreadyConnected,
    ServerFull,
    IpLimitExceeded,
    ConnectionRefused,
    CaptchaFailed,
    AuthenticationFailed,
    CharacterNotFound,
    CharacterSlotsFull,
    CharacterNameTaken,
    CharacterCreationFailed,
    CharacterJoinFailed,
}

/// <summary>A login failure. Fatal failures stop the reconnect loop (retrying would not help).</summary>
public sealed record LoginFailure(LoginFailureReason Reason, string Message)
{
    public bool IsFatal => Reason is LoginFailureReason.WrongVersion
        or LoginFailureReason.WrongPassword
        or LoginFailureReason.Banned
        or LoginFailureReason.CharacterNotFound
        or LoginFailureReason.CharacterSlotsFull
        or LoginFailureReason.CharacterNameTaken
        or LoginFailureReason.CharacterCreationFailed;

    public override string ToString() => $"{Reason}: {Message}";
}

public sealed class ClientStateChangedEventArgs(ClientState previous, ClientState current) : EventArgs
{
    public ClientState Previous { get; } = previous;
    public ClientState Current { get; } = current;
}

public sealed class LoginFailedEventArgs(LoginFailure failure) : EventArgs
{
    public LoginFailure Failure { get; } = failure;
}

public sealed class PacketEventArgs(Packet packet) : EventArgs
{
    public Packet Packet { get; } = packet;
}

public sealed class CharacterListEventArgs(IReadOnlyList<CharacterSlot> characters) : EventArgs
{
    public IReadOnlyList<CharacterSlot> Characters { get; } = characters;
}

public sealed class CharacterEventArgs(Character character) : EventArgs
{
    public Character Character { get; } = character;
}

public sealed class SpawnedEventArgs(uint uniqueId, bool isFirstSpawn) : EventArgs
{
    public uint UniqueId { get; } = uniqueId;
    /// <summary>False when re-spawning after a teleport.</summary>
    public bool IsFirstSpawn { get; } = isFirstSpawn;
}

public sealed class LevelUpEventArgs(byte level) : EventArgs
{
    public byte Level { get; } = level;
}

public sealed class EntityEventArgs(Entity entity) : EventArgs
{
    public Entity Entity { get; } = entity;
}

public sealed class PlayerEventArgs(Player player) : EventArgs
{
    public Player Player { get; } = player;
}

public sealed class ChatEventArgs(ChatType type, string sender, string message) : EventArgs
{
    public ChatType Type { get; } = type;
    public string Sender { get; } = sender;
    public string Message { get; } = message;
}

public sealed class ChatFailedEventArgs(ushort errorCode) : EventArgs
{
    public ushort ErrorCode { get; } = errorCode;
}

/// <summary>The server answered an item movement (0xB034). <see cref="Type"/> is null when it failed.</summary>
public sealed class ItemMovedEventArgs(bool success, ItemMovementType? type, ushort errorCode) : EventArgs
{
    public bool Success { get; } = success;
    public ItemMovementType? Type { get; } = type;
    public ushort ErrorCode { get; } = errorCode;
}

/// <summary>The server answered an item use (0xB04C). <see cref="Slot"/> / <see cref="Quantity"/> are set on success.</summary>
public sealed class ItemUsedEventArgs(bool success, byte slot, ushort quantity, ushort errorCode) : EventArgs
{
    public bool Success { get; } = success;
    public byte Slot { get; } = slot;
    /// <summary>Quantity left in the slot (0 = used up).</summary>
    public ushort Quantity { get; } = quantity;
    public ushort ErrorCode { get; } = errorCode;
}

/// <summary>A packet that went to the server (see <see cref="SroClient.PacketSent"/>).</summary>
public sealed class PacketSentEventArgs(NexBot.Protocol.Packet packet, bool fromGameClient) : EventArgs
{
    public NexBot.Protocol.Packet Packet { get; } = packet;
    /// <summary>True when the game client sent it (the player did it), false when the bot did.</summary>
    public bool FromGameClient { get; } = fromGameClient;
}

/// <summary>Answer to a skill learn / raise (0xB0A1).</summary>
public sealed class SkillLearnedEventArgs(bool success, uint skillId, ushort errorCode) : EventArgs
{
    public bool Success { get; } = success;
    /// <summary>The learned skill level's ID (0 on failure).</summary>
    public uint SkillId { get; } = skillId;
    public ushort ErrorCode { get; } = errorCode;
}

/// <summary>Answer to a mastery raise (0xB0A2).</summary>
public sealed class MasteryRaisedEventArgs(bool success, uint masteryId, byte level, ushort errorCode) : EventArgs
{
    public bool Success { get; } = success;
    public uint MasteryId { get; } = masteryId;
    public byte Level { get; } = level;
    public ushort ErrorCode { get; } = errorCode;
}

/// <summary>Result of <see cref="SroClient.LearnSkillAsync"/> and <see cref="SroClient.RaiseMasteryAsync"/>.</summary>
public sealed record SkillResult(bool Ok, string Message, ushort ErrorCode = 0);

/// <summary>Result of <see cref="SroClient.MoveInventoryItemAsync"/>.</summary>
public sealed record ItemMoveResult(bool Ok, string Message, ushort ErrorCode = 0);

/// <summary>One hit of an attack or skill (anyone hitting anyone in view).</summary>
public sealed class DamageEventArgs(
    uint sourceId, string sourceName, uint targetId, string targetName, uint skillId,
    uint damage, DamageType type, DamageEffect effect) : EventArgs
{
    public uint SourceId { get; } = sourceId;
    public string SourceName { get; } = sourceName;
    public uint TargetId { get; } = targetId;
    public string TargetName { get; } = targetName;

    /// <summary>Skill used (1 or the weapon's base skill for common attacks; 0 if unknown).</summary>
    public uint SkillId { get; } = skillId;

    public uint Damage { get; } = damage;
    public DamageType Type { get; } = type;
    public DamageEffect Effect { get; } = effect;

    public bool IsCritical => Type.HasFlag(DamageType.Critical);
    public bool IsBlocked => Effect.HasFlag(DamageEffect.Block);
    /// <summary>The hit was cancelled (immune / missed): no damage.</summary>
    public bool IsCancelled => Effect.HasFlag(DamageEffect.Cancel);
    public bool IsKnockBack => Effect.HasFlag(DamageEffect.KnockBack);
    /// <summary>This hit killed the target.</summary>
    public bool IsKill => Effect.HasFlag(DamageEffect.Dead);
}

/// <summary>Result of a PvP cape request.</summary>
public sealed class PvpCapeEventArgs(PvpCape cape, bool success, ushort errorCode) : EventArgs
{
    public PvpCape Cape { get; } = cape;
    public bool Success { get; } = success;
    public ushort ErrorCode { get; } = errorCode;
}

/// <summary>Our character died.</summary>
public sealed class CharacterDiedEventArgs(uint killerId, string? killerName, bool killingBlow) : EventArgs
{
    /// <summary>Unique ID of the killer (0 if unknown).</summary>
    public uint KillerId { get; } = killerId;
    public string? KillerName { get; } = killerName;

    /// <summary>True when the killer dealt the killing hit; false when it was the last one to hit us before dying.</summary>
    public bool KillingBlow { get; } = killingBlow;
}

/// <summary>The server refused our attack or skill (0xB070 failure).</summary>
public sealed class AttackFailedEventArgs(ushort errorCode) : EventArgs
{
    public ushort ErrorCode { get; } = errorCode;
}

public sealed class PartyMatchEventArgs(uint number) : EventArgs
{
    public uint Number { get; } = number;
}

public sealed class PartyMatchJoinRequestEventArgs(uint requestId, uint joinId, string playerName) : EventArgs
{
    public uint RequestId { get; } = requestId;
    public uint JoinId { get; } = joinId;
    public string PlayerName { get; } = playerName;
}

public sealed class PartyInvitationEventArgs(uint inviterUniqueId, PartySetup setup, PlayerPetition type) : EventArgs
{
    public uint InviterUniqueId { get; } = inviterUniqueId;
    public PartySetup Setup { get; } = setup;
    public PlayerPetition Type { get; } = type;
}

/// <summary>An invitation that needs an answer (party, guild, academy, union ...).</summary>
/// <param name="InviterName">The inviter's name when the player is near, else empty.</param>
public sealed class PetitionEventArgs(PlayerPetition type, uint inviterUniqueId, string inviterName) : EventArgs
{
    public PlayerPetition Type { get; } = type;
    public uint InviterUniqueId { get; } = inviterUniqueId;
    public string InviterName { get; } = inviterName;
}

/// <summary>A page of the party matching list.</summary>
public sealed class PartyMatchListEventArgs(byte page, byte pageCount, IReadOnlyList<PartyMatch> matches) : EventArgs
{
    public byte Page { get; } = page;
    public byte PageCount { get; } = pageCount;
    public IReadOnlyList<PartyMatch> Matches { get; } = matches;
}

/// <summary>The in-game unique notice (0x300C): a unique appeared somewhere, or was killed.</summary>
/// <param name="Name">The unique's readable name (from the game data), or "#refId".</param>
/// <param name="KilledBy">Who killed it (empty when it appeared).</param>
public sealed class UniqueNoticeEventArgs(bool spawned, uint refId, string name, string killedBy) : EventArgs
{
    public bool Spawned { get; } = spawned;
    public uint RefId { get; } = refId;
    public string Name { get; } = name;
    public string KilledBy { get; } = killedBy;
}

/// <summary>An item picked up from the ground by the character or its grab pet (into the bag, or into the pet's bag).</summary>
public sealed class ItemPickedEventArgs(NexBot.Game.Items.Item item, bool byPet = false, bool intoPet = false) : EventArgs
{
    public NexBot.Game.Items.Item Item { get; } = item;
    /// <summary>The grab pet picked it up.</summary>
    public bool ByPet { get; } = byPet;
    /// <summary>It went into the grab pet's own bag (not the character's).</summary>
    public bool IntoPet { get; } = intoPet;
}

/// <summary>Gold picked up from the ground by the character or its grab pet.</summary>
public sealed class GoldPickedEventArgs(ulong gold, bool byPet) : EventArgs
{
    public ulong Gold { get; } = gold;
    public bool ByPet { get; } = byPet;
}

public sealed class PartyEventArgs(Party party) : EventArgs
{
    public Party Party { get; } = party;
}

public sealed class PartyMemberEventArgs(PartyMember member) : EventArgs
{
    public PartyMember Member { get; } = member;
}
