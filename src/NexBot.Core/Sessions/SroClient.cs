using Microsoft.Extensions.Logging;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Handlers;
using NexBot.Protocol;

namespace NexBot.Sessions;

/// <summary>Handler for a raw packet.</summary>
public delegate void PacketHandler(SroClient client, Packet packet);

/// <summary>
/// The game side of a bot: the world state (character, spawns, party ...) built from the server packets, the typed
/// events and the game actions (move, use item ...).
/// </summary>
/// <remarks>
/// <para>
/// Ported from POC-2's <c>SroClient</c>. There it also owned the connection and the login; in NexBot the
/// <see cref="Network.BotSession"/> does that (proxy or clientless) and feeds every server packet to
/// <see cref="HandlePacket"/>. Packets sent by the actions go to the server through the session.
/// </para>
/// <para>
/// Threading: <see cref="HandlePacket"/> is called on the session's upstream receive loop, so handlers and events run
/// sequentially and only they mutate <see cref="World"/>. Readers on other threads (the window) must use
/// <c>EstimatePosition()</c>.
/// </para>
/// </remarks>
public sealed partial class SroClient : IDisposable
{
    private readonly Func<Packet, bool> _send;
    private readonly Lock _stateLock = new();
    private ClientState _state = ClientState.Idle;
    private bool _everSpawned;

    public SroClient(IGameData data, ILogger logger, Func<Packet, bool> send, string characterName = "")
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        Account = new AccountOptions { CharacterName = characterName };
    }

    public IGameData Data { get; }
    internal ILogger Logger { get; }

    /// <summary>Feature switches read by the handlers (pet recovery ...).</summary>
    public ClientOptions Options { get; } = new();

    public AccountOptions Account { get; }

    /// <summary>Everything known about the world (character, spawns, party ...).</summary>
    public WorldState World { get; } = new();

    public string CharacterName => World.Character?.Name ?? Account.CharacterName;

    public ClientState State
    {
        get { lock (_stateLock) return _state; }
    }

    public bool IsInGame => State == ClientState.InGame;

    #region Events

    public event EventHandler<ClientStateChangedEventArgs>? StateChanged;
    public event EventHandler<CharacterEventArgs>? CharacterLoaded;
    public event EventHandler<SpawnedEventArgs>? Spawned;
    /// <summary>The player used a return scroll or revived in town from the game client (the town loop can start).</summary>
    public event EventHandler<EventArgs>? ReturnedToTownByPlayer;
    public event EventHandler<LevelUpEventArgs>? LevelUp;
    public event EventHandler<EntityEventArgs>? EntitySpawned;
    public event EventHandler<EntityEventArgs>? EntityDespawned;
    public event EventHandler<PlayerEventArgs>? PlayerSpawned;
    public event EventHandler<EntityEventArgs>? MobDied;
    public event EventHandler<AttackFailedEventArgs>? AttackFailed;
    public event EventHandler<DamageEventArgs>? DamageDealt;
    public event EventHandler<CharacterDiedEventArgs>? CharacterDied;
    public event EventHandler<PvpCapeEventArgs>? PvpCapeChanged;
    public event EventHandler<ChatEventArgs>? ChatReceived;
    public event EventHandler<ChatFailedEventArgs>? ChatFailed;
    public event EventHandler<ItemMovedEventArgs>? ItemMoved;
    public event EventHandler<SkillLearnedEventArgs>? SkillLearned;

    /// <summary>
    /// A packet went to the server: sent by the game client through the proxy, or by the bot. Subscribers get their
    /// own copy (read position 0). Used by the script recorder (teleports, return scrolls).
    /// </summary>
    public event EventHandler<PacketSentEventArgs>? PacketSent;
    public event EventHandler<MasteryRaisedEventArgs>? MasteryRaised;
    public event EventHandler<ItemUsedEventArgs>? ItemUsed;
    public event EventHandler<PartyMatchEventArgs>? PartyMatchCreated;
    public event EventHandler<PartyMatchEventArgs>? PartyMatchDeleted;
    public event EventHandler<PartyMatchJoinRequestEventArgs>? PartyMatchJoinRequested;
    public event EventHandler<PartyInvitationEventArgs>? PartyInvitationReceived;
    public event EventHandler<PartyEventArgs>? PartyJoined;
    public event EventHandler<PartyMemberEventArgs>? PartyMemberJoined;
    public event EventHandler<PartyEventArgs>? PartyLeft;
    /// <summary>Any invitation (party, guild, academy ...), after the party-specific event.</summary>
    public event EventHandler<PetitionEventArgs>? PetitionReceived;
    public event EventHandler<PartyMatchListEventArgs>? PartyMatchListReceived;
    public event EventHandler<UniqueNoticeEventArgs>? UniqueNotice;
    public event EventHandler<ItemPickedEventArgs>? ItemPicked;
    public event EventHandler<GoldPickedEventArgs>? GoldPicked;
    /// <summary>One of our pets (transport, horse, attack or pick pet) died.</summary>
    public event EventHandler<EntityEventArgs>? PetDied;

    internal void RaiseCharacterLoaded(Game.Entities.Character c) => Raise(CharacterLoaded, new CharacterEventArgs(c));
    internal void RaiseLevelUp(byte level) => Raise(LevelUp, new LevelUpEventArgs(level));
    internal void RaiseEntitySpawned(Game.Entities.Entity e) => Raise(EntitySpawned, new EntityEventArgs(e));
    internal void RaiseEntityDespawned(Game.Entities.Entity e) => Raise(EntityDespawned, new EntityEventArgs(e));
    internal void RaisePlayerSpawned(Game.Entities.Player p) => Raise(PlayerSpawned, new PlayerEventArgs(p));
    internal void RaiseMobDied(Game.Entities.Entity e) => Raise(MobDied, new EntityEventArgs(e));
    internal void RaiseAttackFailed(ushort errorCode) => Raise(AttackFailed, new AttackFailedEventArgs(errorCode));
    internal void RaiseDamage(DamageEventArgs e) => Raise(DamageDealt, e);
    internal void RaiseCharacterDied(CharacterDiedEventArgs e) => Raise(CharacterDied, e);
    internal void RaisePvpCapeChanged(PvpCape cape, bool success, ushort error) => Raise(PvpCapeChanged, new PvpCapeEventArgs(cape, success, error));
    internal void RaiseChat(ChatType type, string sender, string message) => Raise(ChatReceived, new ChatEventArgs(type, sender, message));
    internal void RaiseChatFailed(ushort errorCode) => Raise(ChatFailed, new ChatFailedEventArgs(errorCode));
    internal void RaiseSkillLearned(bool success, uint skillId, ushort errorCode) =>
        Raise(SkillLearned, new SkillLearnedEventArgs(success, skillId, errorCode));
    internal void RaiseMasteryRaised(bool success, uint masteryId, byte level, ushort errorCode) =>
        Raise(MasteryRaised, new MasteryRaisedEventArgs(success, masteryId, level, errorCode));
    internal void RaiseItemMoved(bool success, ItemMovementType? type, ushort errorCode) =>
        Raise(ItemMoved, new ItemMovedEventArgs(success, type, errorCode));
    internal void RaiseItemUsed(bool success, byte slot, ushort quantity, ushort errorCode) =>
        Raise(ItemUsed, new ItemUsedEventArgs(success, slot, quantity, errorCode));
    internal void RaisePartyMatchCreated(uint n) => Raise(PartyMatchCreated, new PartyMatchEventArgs(n));
    internal void RaisePartyMatchDeleted(uint n) => Raise(PartyMatchDeleted, new PartyMatchEventArgs(n));
    internal void RaisePartyMatchJoinRequested(uint requestId, uint joinId, string name) =>
        Raise(PartyMatchJoinRequested, new PartyMatchJoinRequestEventArgs(requestId, joinId, name));
    internal void RaisePartyInvitation(uint inviter, PartySetup setup, PlayerPetition type) =>
        Raise(PartyInvitationReceived, new PartyInvitationEventArgs(inviter, setup, type));
    internal void RaisePartyJoined(Party party) => Raise(PartyJoined, new PartyEventArgs(party));
    internal void RaisePartyMemberJoined(PartyMember member) => Raise(PartyMemberJoined, new PartyMemberEventArgs(member));
    internal void RaisePartyLeft(Party party) => Raise(PartyLeft, new PartyEventArgs(party));
    internal void RaiseUniqueNotice(bool spawned, uint refId, string name, string killer) =>
        Raise(UniqueNotice, new UniqueNoticeEventArgs(spawned, refId, name, killer));
    internal void RaiseItemPicked(Game.Items.Item item, bool byPet = false, bool intoPet = false) =>
        Raise(ItemPicked, new ItemPickedEventArgs(item, byPet, intoPet));
    internal void RaiseGoldPicked(ulong gold, bool byPet) => Raise(GoldPicked, new GoldPickedEventArgs(gold, byPet));
    internal void RaisePetDied(Game.Entities.Entity pet) => Raise(PetDied, new EntityEventArgs(pet));
    internal void RaisePetition(PlayerPetition type, uint inviter, string name) => Raise(PetitionReceived, new PetitionEventArgs(type, inviter, name));
    internal void RaisePartyMatchList(byte page, byte pages, IReadOnlyList<PartyMatch> matches) =>
        Raise(PartyMatchListReceived, new PartyMatchListEventArgs(page, pages, matches));

    internal void RaiseSpawned(uint uniqueId)
    {
        bool first = !_everSpawned;
        _everSpawned = true;
        Raise(Spawned, new SpawnedEventArgs(uniqueId, first));
    }

    /// <summary>Skill instances seen in 0xB070, used to find the attacker of delayed damage in 0xB071.</summary>
    internal Dictionary<uint, (uint SourceId, uint SkillId)> ActiveSkills { get; } = [];

    /// <summary>Last entity that damaged our character.</summary>
    internal (uint Id, string Name, DateTime At) LastAttacker { get; set; }

    /// <summary>Set once the death of our character has been reported (reset when alive again).</summary>
    internal bool DeathReported { get; set; }

    /// <summary>Cape requested with <see cref="WearPvpCape"/> (for reporting the server's answer).</summary>
    internal PvpCape RequestedCape { get; set; }

    /// <summary>Invokes subscribers one by one, so a throwing subscriber cannot break the session or other subscribers.</summary>
    private void Raise<T>(EventHandler<T>? handler, T args) where T : EventArgs
    {
        if (handler is null)
            return;
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)d)(this, args);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Event handler {Handler} threw", d.Method.Name);
            }
        }
    }

    #endregion

    internal void SetState(ClientState state)
    {
        ClientState previous;
        lock (_stateLock)
        {
            previous = _state;
            if (previous == state)
                return;
            _state = state;
        }
        Raise(StateChanged, new ClientStateChangedEventArgs(previous, state));
    }

    /// <summary>Forgets the world (new gateway login, disconnect).</summary>
    public void Reset()
    {
        World.Reset();
        CharacterDataBuffer = null;
        StorageDataBuffer = null;
        GroupSpawn = default;
        ActiveSkills.Clear();
        SetState(ClientState.Idle);
    }

    /// <summary>Queues a packet to the server. Returns false when not connected.</summary>
    public bool Send(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        bool sent = _send(packet);
        if (sent)
            ObservePacketToServer(packet, fromGameClient: false);
        return sent;
    }

    /// <summary>Reports a packet sent to the server (see <see cref="PacketSent"/>).</summary>
    public void ObservePacketToServer(Packet packet, bool fromGameClient)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (fromGameClient && packet.Opcode is Network.Opcodes.Client.StallCreate or Network.Opcodes.Client.StallUpdate
            or Network.Opcodes.Client.StallDestroy)
            ObserveStallPacket(packet);
        if (fromGameClient && packet.Opcode is Network.Opcodes.Client.AlchemyReinforce or Network.Opcodes.Client.AlchemyStone
            && packet.Payload.Length >= 3 && packet.Payload[0] == 2)
            RaiseAlchemyLearned(packet.Opcode == Network.Opcodes.Client.AlchemyStone, packet.Payload[1], packet.Payload[2],
                Convert.ToHexString(packet.Payload));
        if (fromGameClient && IsReturnToTown(packet))
            Raise(ReturnedToTownByPlayer, EventArgs.Empty);
        if (PacketSent is not { } handlers)
            return;
        var copy = new Packet(packet.Opcode, packet.Payload, packet.Encrypted, packet.Massive);
        Raise(handlers, new PacketSentEventArgs(copy, fromGameClient));
    }

    /// <summary>A return scroll used (0x704C on a return scroll's slot) or a revive in town (0x3053 type 1).</summary>
    private bool IsReturnToTown(Packet packet)
    {
        var p = packet.Payload;
        return packet.Opcode switch
        {
            Network.Opcodes.Client.ItemUse => p.Length >= 1 && World.Character is { } c && p[0] < c.Inventory.Capacity
                && c.Inventory[p[0]] is { } item && Automation.ProtectionCatalog.IsReturnScroll(item),
            Network.Opcodes.Client.AutoResurrection => p.Length >= 1 && p[0] == 1,
            _ => false,
        };
    }

    /// <summary>Updates the world from a server packet. Never throws: a bad packet is logged and skipped.</summary>
    public void HandlePacket(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        try
        {
            packet.Position = 0;
            if (PacketHandlerTable.Handlers.TryGetValue(packet.Opcode, out var handler))
                handler(this, packet);
        }
        catch (Exception ex)
        {
            Logger.LogDebug("World: failed to handle packet 0x{Opcode:X4} ({Length} bytes): {Message}", packet.Opcode, packet.Length, ex.Message);
        }
        finally
        {
            packet.Position = 0;
        }
    }

    // Multi-packet buffers used by the handlers.
    internal Packet? CharacterDataBuffer { get; set; }
    internal Packet? GuildDataBuffer { get; set; }
    internal Packet? StorageDataBuffer { get; set; }
    internal (byte Type, ushort Count, Packet? Buffer) GroupSpawn { get; set; }

    public void Dispose() => _moveGate.Dispose();

    public override string ToString() => $"{CharacterName} ({State})";
}
