using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NexBot.Loader;
using NexBot.Data;
using NexBot.Game;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Network;

/// <summary>
/// One bot: a game-client proxy with automatic login, or a clientless connection.
/// </summary>
/// <remarks>
/// <para>Client mode (<see cref="StartWithClientAsync"/>), the same flow as RSBot:</para>
/// <code>
/// sro_client ──► 127.0.0.1:proxy ──(downstream, server role)── BotSession ──(upstream, client role)──► gateway / agent
/// </code>
/// <list type="bullet">
///   <item>The client's gateway connection is redirected to the proxy by <see cref="ClientLauncher"/>.</item>
///   <item>When the shard list arrives the bot sends the login itself (0x6102), answers the captcha and rewrites the
///   agent address in 0xA102 to the proxy, so the client reconnects to us. Its empty 0x6103 is replaced by one
///   with the real credentials, and the configured character is joined when the list arrives.</item>
///   <item><see cref="GoClientless"/> closes the client and keeps the agent connection alive (0x2002 every 5 s).</item>
/// </list>
/// <para>Clientless mode (<see cref="StartClientlessAsync"/>) runs the same flow without a client.</para>
/// <para>
/// Threading: upstream and downstream packets arrive on two different receive loops. Shared state is guarded by
/// <see cref="_sync"/>; sends are thread-safe. Events are raised on those loops, so UI handlers must marshal.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BotSession : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);

    private readonly ServerProfile _server;
    private readonly AccountProfile _account;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly PacketTrace _trace = new();
    /// <summary>Answers still due to NPC requests the bot sent itself (by answer opcode); see <see cref="FromBot"/>.</summary>
    private readonly Dictionary<ushort, int> _botNpcAnswers = [];

    private TcpListener? _listener;
    private SroConnection? _upstream;
    private SroConnection? _downstream;
    private ServerModule _module;
    private AgentRedirect? _pendingAgent;
    private uint _token;
    private bool _loginSent;
    private bool _loginByBot;
    private bool _characterSelected;
    private bool _clientless;
    private BotState _state = BotState.Idle;
    private Process? _clientProcess;
    private Task _keepAliveTask = Task.CompletedTask;

    private enum ServerModule { None, Gateway, Agent }

    private sealed record AgentRedirect(string Host, ushort Port, uint Token);

    /// <param name="gameData">Game database; when given, the world (character, spawns ...) is tracked in <see cref="Game"/>.</param>
    public BotSession(ServerProfile server, AccountProfile account, ILogger logger, IGameData? gameData = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(account);
        if (server.GatewayHosts.Count == 0)
            throw new ArgumentException("No gateway host configured.", nameof(server));
        _server = server;
        _account = account;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (gameData is not null)
        {
            Game = new SroClient(gameData, logger, packet => { FromBot(packet); return SendUpstream(packet); }, account.CharacterName);
            Trainer = new Map.SkillTrainer(Game, logger);
            _ = Task.Run(() => Trainer.RunAsync(_cts.Token));
        }
    }

    /// <summary>Automatic mastery / skill leveling (null without a game database). Idle until options are set.</summary>
    public Map.SkillTrainer? Trainer { get; }

    public BotState State
    {
        get { lock (_sync) return _state; }
    }

    /// <summary>True once the session runs without a game client.</summary>
    public bool IsClientless
    {
        get { lock (_sync) return _clientless; }
    }

    /// <summary>The game client started by this session (null in clientless mode or after it exited).</summary>
    public Process? ClientProcess
    {
        get { lock (_sync) return _clientProcess; }
    }

    /// <summary>Port of the local proxy (0 until started in client mode).</summary>
    public ushort ProxyPort { get; private set; }

    public event Action<BotState>? StateChanged;
    public event Action<IReadOnlyList<ShardInfo>>? ShardsReceived;
    public event Action<IReadOnlyList<CharacterInfo>>? CharactersReceived;

    /// <summary>Raised when the game client process exits.</summary>
    public event Action? ClientExited;

    /// <summary>The game world and actions (null without a game database).</summary>
    public SroClient? Game { get; }

    #region Start / stop

    /// <summary>Starts the proxy and the game client.</summary>
    /// <param name="clientDirectory">Folder with sro_client.exe.</param>
    /// <param name="proxyPort">Local proxy port, 0 = any free port.</param>
    public Task StartWithClientAsync(string clientDirectory, string executable, byte divisionIndex, byte gatewayIndex, ushort proxyPort = 0)
    {
        StartProxy(proxyPort);

        var process = ClientLauncher.Launch(new LaunchOptions(
            clientDirectory, executable, _server.Locale, divisionIndex, gatewayIndex, _server.GatewayPort, ProxyPort));

        process.EnableRaisingEvents = true;
        process.Exited += OnClientProcessExited;
        lock (_sync) _clientProcess = process;
        _logger.LogInformation("Game client started (PID {Pid}), waiting for it to connect", process.Id);
        SetState(BotState.WaitingForClient);
        return Task.CompletedTask;
    }

    /// <summary>Starts only the local proxy (the game client is started by the caller, or a test connects).</summary>
    internal void StartProxy(ushort proxyPort)
    {
        lock (_sync)
        {
            if (_state != BotState.Idle)
                throw new InvalidOperationException("The session was already started.");
            _clientless = false;
        }
        SetState(BotState.WaitingForClient);

        _listener = new TcpListener(IPAddress.Loopback, proxyPort);
        _listener.Start();
        ProxyPort = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
        _logger.LogInformation("Proxy listening on 127.0.0.1:{Port}", ProxyPort);
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Connects to the gateway without a game client.</summary>
    public async Task StartClientlessAsync()
    {
        lock (_sync)
        {
            if (_state != BotState.Idle)
                throw new InvalidOperationException("The session was already started.");
            _clientless = true;
        }
        if (!_account.HasCredentials)
            throw new InvalidOperationException("Clientless mode needs a username and password.");

        StartKeepAlive();
        await ConnectGatewayAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the game client and keeps the character online without it.</summary>
    public void GoClientless()
    {
        Process? process;
        SroConnection? downstream;
        lock (_sync)
        {
            if (_clientless)
                return;
            if (_module != ServerModule.Agent || _upstream is not { IsConnected: true })
                throw new InvalidOperationException("Log in to the game before switching to clientless.");
            _clientless = true;
            process = _clientProcess;
            downstream = _downstream;
            _downstream = null;
        }

        _logger.LogInformation("Switching to clientless mode");
        StartKeepAlive();
        downstream?.Close();
        KillClient(process);
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();

        SroConnection? upstream, downstream;
        Process? process;
        lock (_sync)
        {
            upstream = _upstream;
            downstream = _downstream;
            process = _clientProcess;
            _upstream = _downstream = null;
        }
        if (downstream is not null) await downstream.DisposeAsync().ConfigureAwait(false);
        if (upstream is not null) await upstream.DisposeAsync().ConfigureAwait(false);
        KillClient(process);
        SetState(BotState.Idle);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private void KillClient(Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug("Could not kill the client: {Message}", ex.Message);
        }
    }

    private void OnClientProcessExited(object? sender, EventArgs e)
    {
        lock (_sync) _clientProcess = null;
        _logger.LogWarning("Game client exited");
        ClientExited?.Invoke();
    }

    private void StartKeepAlive()
    {
        if (!_keepAliveTask.IsCompleted)
            return;
        _keepAliveTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(KeepAliveInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                {
                    SroConnection? upstream;
                    lock (_sync) upstream = _clientless ? _upstream : null;
                    upstream?.Send(LoginPackets.KeepAlive());
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    #endregion

    #region Connections

    private async Task AcceptLoopAsync()
    {
        var listener = _listener!;
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            try
            {
                await OnClientConnectedAsync(socket).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Could not serve the game client: {Message}", ex.Message);
                socket.Dispose();
            }
        }
    }

    private async Task OnClientConnectedAsync(Socket socket)
    {
        SroConnection? oldDownstream;
        AgentRedirect? redirect;
        lock (_sync)
        {
            if (_clientless)
            {
                socket.Dispose();
                return;
            }
            oldDownstream = _downstream;
            _downstream = null;
            redirect = _pendingAgent;
        }
        oldDownstream?.Close();

        // Connect upstream first so nothing the client sends has to be queued.
        if (redirect is null)
        {
            _logger.LogInformation("Game client connected, connecting to the gateway");
            await ConnectGatewayAsync().ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("Game client reconnected, connecting to the agent server {Host}:{Port}", redirect.Host, redirect.Port);
            await ConnectAgentAsync(redirect).ConfigureAwait(false);
        }

        var downstream = SroConnection.Accept(socket, "Client");
        downstream.PacketReceived += OnClientPacketAsync;
        downstream.Disconnected += OnDownstreamDisconnected;
        lock (_sync) _downstream = downstream;
        downstream.Start();
    }

    private async Task ConnectGatewayAsync()
    {
        Exception? last = null;
        foreach (string host in _server.GatewayHosts)
        {
            try
            {
                _logger.LogInformation("Connecting to gateway {Host}:{Port}", host, _server.GatewayPort);
                var connection = await SroConnection.ConnectAsync(host, _server.GatewayPort, "Gateway", ConnectTimeout, _cts.Token).ConfigureAwait(false);
                ReplaceUpstream(connection, ServerModule.Gateway);
                return;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                last = ex;
                _logger.LogWarning("Gateway {Host} failed: {Message}", host, ex.Message);
            }
        }
        SetState(BotState.Disconnected);
        throw new IOException("Could not connect to any gateway server.", last);
    }

    private async Task ConnectAgentAsync(AgentRedirect redirect)
    {
        var connection = await SroConnection.ConnectAsync(redirect.Host, redirect.Port, "Agent", ConnectTimeout, _cts.Token).ConfigureAwait(false);
        ReplaceUpstream(connection, ServerModule.Agent);
    }

    private void ReplaceUpstream(SroConnection connection, ServerModule module)
    {
        SroConnection? old;
        lock (_sync)
        {
            old = _upstream;
            _upstream = connection;
            _module = module;
            if (module == ServerModule.Gateway)
            {
                Game?.Reset();
                _loginSent = false;
                _loginByBot = false;
                _characterSelected = false;
            }
        }
        old?.Close();
        connection.PacketReceived += OnServerPacketAsync;
        connection.Disconnected += OnUpstreamDisconnected;
        connection.Start();
    }

    private void OnUpstreamDisconnected(SroConnection connection, Exception? reason)
    {
        SroConnection? downstream = null;
        bool redirecting;
        lock (_sync)
        {
            if (!ReferenceEquals(_upstream, connection))
                return; // replaced on purpose
            _upstream = null;
            redirecting = _pendingAgent is not null && _module == ServerModule.Gateway;
            if (!redirecting)
            {
                downstream = _downstream;
                _downstream = null;
            }
        }

        if (redirecting)
        {
            _logger.LogDebug("Gateway closed during the agent redirect");
            return;
        }

        _logger.LogWarning("Disconnected from the {Server} server{Reason}", connection.Name,
            reason is null ? "" : $": {reason.Message}");
        downstream?.Close();
        SetState(BotState.Disconnected);
    }

    private void OnDownstreamDisconnected(SroConnection connection, Exception? reason)
    {
        SroConnection? upstream = null;
        bool goClientless = false;
        lock (_sync)
        {
            if (!ReferenceEquals(_downstream, connection))
                return; // replaced, or GoClientless() already detached it
            _downstream = null;

            if (_clientless)
                return;

            bool inWorld = _state is BotState.Loading or BotState.InGame;
            if (_module == ServerModule.Agent && inWorld && _account.ClientlessWhenClientCloses && _upstream is { IsConnected: true })
            {
                _clientless = true;
                goClientless = true;
            }
            else
            {
                // OnUpstreamDisconnected decides whether this was a redirect or a real disconnect.
                upstream = _upstream;
            }
        }

        if (goClientless)
        {
            _logger.LogInformation("Game client disconnected in game, staying online in clientless mode");
            _logger.LogWarning("Last packets before the game client dropped (the one it could not handle is usually near the end):\n{Packets}", _trace.Dump());
            StartKeepAlive();
            return;
        }

        _logger.LogDebug("Game client disconnected from the proxy");
        upstream?.Close();
    }

    #endregion

    #region Server -> client

    private ValueTask OnServerPacketAsync(SroConnection connection, Packet packet)
    {
        Packet? forward = packet;
        SroConnection? downstream;
        lock (_sync) downstream = _downstream;
        bool toBot = false;
        ShopReplySync? shop = null;
        try
        {
            forward = HandleServerPacket(packet);
            // Answers to the bot's own NPC requests are kept from the client (it crashes on them); a shop buy / sell
            // is shown to it in another form after the bot applied it.
            toBot = forward is not null && downstream is not null && IsAnswerToBot(forward);
            if (toBot && Game is { } game && forward!.Opcode == Opcodes.Server.ItemMovementResponse)
                shop = ShopReplySync.Before(game, forward);
            Game?.HandlePacket(packet);
        }
        catch (Exception ex) when (ex is PacketException or ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning("Could not parse server packet {Packet}: {Message}", $"0x{packet.Opcode:X4}", ex.Message);
        }

        if (forward is not null && downstream is not null)
        {
            if (toBot)
            {
                Trace("S>bot", forward);
                if (shop is not null && Game is { } game)
                    foreach (var replacement in shop.After(game))
                    {
                        Trace("bot>C", replacement);
                        downstream.Send(replacement);
                    }
            }
            else
            {
                Trace("S>C", forward);
                downstream.Send(forward);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Reacts to a server packet. Returns the packet to forward to the client (null = drop).</summary>
    private Packet? HandleServerPacket(Packet packet)
    {
        switch (packet.Opcode)
        {
            case LoginOpcodes.Identity:
                OnIdentity(packet.ReadAscii());
                break;

            case LoginOpcodes.PatchResponse:
                OnPatchResponse(packet);
                break;

            case LoginOpcodes.ShardListResponse:
                OnShardList(packet);
                break;

            case LoginOpcodes.CaptchaImage:
                if (IsLoginOwnedByBot())
                {
                    _logger.LogInformation("Gateway asks for a captcha, answering '{Captcha}'", _account.Captcha);
                    SendUpstream(LoginPackets.CaptchaAnswer(_account.Captcha));
                }
                break;

            case LoginOpcodes.CaptchaResponse:
                if (packet.ReadByte() == 1)
                    _logger.LogInformation("Captcha accepted");
                else
                    _logger.LogWarning("Wrong captcha ({Attempts}/{Max}), set the captcha in the settings", packet.ReadUInt32(), packet.ReadUInt32());
                break;

            case LoginOpcodes.LoginResponse:
                return OnLoginResponse(packet);

            case LoginOpcodes.AuthResponse:
                OnAuthResponse(packet);
                break;

            case LoginOpcodes.CharacterSelectionActionResponse:
                OnCharacterSelection(packet);
                break;

            case LoginOpcodes.CharacterSelectionJoinResponse:
                if (packet.ReadByte() == 1)
                {
                    _logger.LogInformation("Character selected, loading the world");
                    SetState(BotState.Loading);
                }
                else
                {
                    _logger.LogWarning("Could not join the character (error 0x{Error:X})", packet.Remaining >= 2 ? packet.ReadUInt16() : 0);
                }
                break;

            case LoginOpcodes.CelestialPosition:
                if (IsClientless)
                    SendUpstream(LoginPackets.ConfirmSpawn());
                if (State != BotState.InGame)
                    _logger.LogInformation("Welcome to the Silkroad, the character is in game");
                SetState(BotState.InGame);
                break;

            case LoginOpcodes.TeleportReadyRequest:
                if (IsClientless)
                    SendUpstream(LoginPackets.TeleportReady());
                break;
        }

        packet.Position = 0;
        return packet;
    }

    private void OnIdentity(string module)
    {
        bool clientless;
        lock (_sync) clientless = _clientless;

        switch (module)
        {
            case "GatewayServer":
                SetState(BotState.Gateway);
                if (clientless)
                    SendUpstream(LoginPackets.PatchRequest(_server.Locale, _server.Version));
                break;

            case "AgentServer":
                SetState(BotState.Agent);
                if (clientless)
                {
                    uint token;
                    lock (_sync) token = _token;
                    SendUpstream(LoginPackets.AuthRequest(token, _account.Username, _account.Password, _server.Locale));
                }
                break;

            default:
                _logger.LogDebug("Unknown server module {Module}", module);
                break;
        }
    }

    private void OnPatchResponse(Packet packet)
    {
        if (packet.ReadByte() == 1)
        {
            if (IsClientless)
                SendUpstream(LoginPackets.ShardListRequest());
            return;
        }
        byte error = packet.Remaining > 0 ? packet.ReadByte() : (byte)0;
        _logger.LogError("The server rejected client version {Version} (error {Error}); update the client or the version override",
            _server.Version, error);
    }

    private void OnShardList(Packet packet)
    {
        while (packet.ReadByte() == 1)
        {
            packet.ReadByte();  // farm id
            packet.ReadAscii(); // farm name
        }

        var shards = new List<ShardInfo>();
        while (packet.ReadByte() == 1)
        {
            ushort id = packet.ReadUInt16();
            string name = packet.ReadAscii();
            ushort online = packet.ReadUInt16();
            ushort capacity = packet.ReadUInt16();
            bool open = packet.ReadByte() == 1;
            packet.ReadByte(); // farm id
            shards.Add(new ShardInfo(id, name, online, capacity, open));
        }

        ShardsReceived?.Invoke(shards);
        TrySendLogin(shards);
    }

    private bool IsLoginOwnedByBot()
    {
        lock (_sync) return _loginByBot;
    }

    private void TrySendLogin(List<ShardInfo> shards)
    {
        lock (_sync)
        {
            if (_loginSent)
                return;
        }

        if (!_account.AutoLogin || !_account.HasCredentials)
        {
            if (!IsClientless)
                _logger.LogInformation("Auto login is off or no account is set, log in from the game client");
            return;
        }

        var shard = string.IsNullOrWhiteSpace(_account.ShardName)
            ? shards.Find(s => s.IsOpen) ?? shards.FirstOrDefault()
            : shards.Find(s => string.Equals(s.Name, _account.ShardName, StringComparison.OrdinalIgnoreCase));

        if (shard is null)
        {
            _logger.LogWarning("Server '{Shard}' is not in the server list ({List})", _account.ShardName,
                string.Join(", ", shards.Select(s => s.Name)));
            return;
        }

        if (!shard.IsOpen)
        {
            _logger.LogWarning("Server {Shard} is under inspection, waiting", shard.Name);
            if (IsClientless) // the game client asks for the list again by itself
                _ = Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ContinueWith(
                    _ => SendUpstream(LoginPackets.ShardListRequest()), TaskContinuationOptions.OnlyOnRanToCompletion);
            return;
        }

        lock (_sync) _loginSent = _loginByBot = true;
        _logger.LogInformation("Logging in as {User} on {Shard} ({Online}/{Capacity})", _account.Username, shard.Name, shard.Online, shard.Capacity);
        SendUpstream(LoginPackets.LoginRequest(_server.Locale, _account.Username, _account.Password, shard.Id));
    }

    private Packet? OnLoginResponse(Packet packet)
    {
        byte result = packet.ReadByte();
        if (result != 1)
        {
            LogLoginFailure(packet);
            packet.Position = 0;
            return packet;
        }

        uint token = packet.ReadUInt32();
        string host = packet.ReadAscii();
        ushort port = packet.ReadUInt16();
        var rest = packet.ReadSpan(packet.Remaining);
        var redirect = new AgentRedirect(host, port, token);

        bool clientless;
        lock (_sync)
        {
            _token = token;
            _pendingAgent = redirect;
            clientless = _clientless;
        }
        _logger.LogInformation("Gateway login accepted, agent server {Host}:{Port}", host, port);

        if (clientless)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ConnectAgentAsync(redirect).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    _logger.LogError("Could not connect to the agent server: {Message}", ex.Message);
                    SetState(BotState.Disconnected);
                }
            });
            return null;
        }

        // Point the client at the proxy; it reconnects and we connect to the real agent server.
        return new Packet(LoginOpcodes.LoginResponse, packet.Encrypted)
            .WriteByte(1)
            .WriteUInt32(token)
            .WriteAscii("127.0.0.1")
            .WriteUInt16(ProxyPort)
            .WriteBytes(rest);
    }

    private void LogLoginFailure(Packet packet)
    {
        byte error = packet.Remaining > 0 ? packet.ReadByte() : (byte)0;
        string message = error switch
        {
            1 => "wrong username or password",
            2 => "the account is blocked or the server is under inspection",
            3 => "the account is already connected",
            5 => "the server is full",
            8 => "the connection limit for this IP was exceeded",
            _ => $"error C{error + 1}",
        };
        lock (_sync) _loginSent = _loginByBot = false;
        _logger.LogError("Gateway login failed: {Reason}", message);
    }

    private void OnAuthResponse(Packet packet)
    {
        lock (_sync) _pendingAgent = null;
        if (packet.ReadByte() != 1)
        {
            byte error = packet.Remaining > 0 ? packet.ReadByte() : (byte)0;
            _logger.LogError("The agent server rejected the login (error {Error})", error);
            return;
        }

        _logger.LogInformation("Agent login accepted");
        SetState(BotState.CharacterSelection);
        if (IsClientless)
            SendUpstream(LoginPackets.CharacterList());
    }

    private void OnCharacterSelection(Packet packet)
    {
        byte action = packet.ReadByte();
        byte result = packet.ReadByte();
        if (action != 2 || result != 1)
            return;

        var characters = ReadCharacterList(packet);
        foreach (var c in characters)
            _logger.LogInformation("Character found: {Name} (lv. {Level}){Deleting}", c.Name, c.Level, c.IsDeleting ? " [being deleted]" : "");
        CharactersReceived?.Invoke(characters);

        lock (_sync)
        {
            if (_characterSelected)
                return;
        }

        string name = _account.CharacterName;
        CharacterInfo? character = null;
        if (_account.AutoSelectCharacter && !string.IsNullOrWhiteSpace(name))
            character = characters.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        else if (IsClientless)
            character = characters.Find(c => !c.IsDeleting);

        if (character is null)
        {
            if (!string.IsNullOrWhiteSpace(name) && _account.AutoSelectCharacter)
                _logger.LogWarning("Character '{Name}' is not on this account", name);
            else if (!IsClientless)
                _logger.LogInformation("Select your character in the game client");
            return;
        }

        lock (_sync) _characterSelected = true;
        _logger.LogInformation("Joining the game with {Name}", character.Name);
        SendUpstream(LoginPackets.SelectCharacter(character.Name));
    }

    /// <summary>vSRO 1.188 layout of the 0xB007 character list.</summary>
    private static List<CharacterInfo> ReadCharacterList(Packet p)
    {
        byte count = p.ReadByte();
        var list = new List<CharacterInfo>(count);
        for (int n = 0; n < count; n++)
        {
            p.ReadUInt32();             // model
            string name = p.ReadAscii();
            p.ReadByte();               // scale
            byte level = p.ReadByte();
            p.ReadUInt64();             // exp
            p.ReadUInt16();             // str
            p.ReadUInt16();             // int
            p.ReadUInt16();             // stat points
            p.ReadUInt32();             // hp
            p.ReadUInt32();             // mp
            bool deleting = p.ReadBool();
            if (deleting)
                p.ReadUInt32();         // minutes until deletion
            p.ReadByte();               // guild member type
            if (p.ReadBool())
                p.ReadAscii();          // guild name
            p.ReadByte();               // academy member type
            byte items = p.ReadByte();
            p.ReadSpan(items * 5);      // item id + plus
            byte avatars = p.ReadByte();
            p.ReadSpan(avatars * 5);
            list.Add(new CharacterInfo(name, level, deleting));
        }
        return list;
    }

    #endregion

    #region Client -> server

    private ValueTask OnClientPacketAsync(SroConnection connection, Packet packet)
    {
        Packet? forward = packet;
        try
        {
            forward = HandleClientPacket(packet);
        }
        catch (Exception ex) when (ex is PacketException or ArgumentException)
        {
            _logger.LogWarning("Could not parse client packet {Packet}: {Message}", $"0x{packet.Opcode:X4}", ex.Message);
        }

        if (forward is not null)
        {
            Trace("C>S", forward);
            SendUpstream(forward);
        }
        return ValueTask.CompletedTask;
    }

    private Packet? HandleClientPacket(Packet packet)
    {
        ServerModule module;
        lock (_sync) module = _module;

        switch (packet.Opcode)
        {
            case LoginOpcodes.Identity:
                return null; // our upstream security sends its own identity

            case LoginOpcodes.PatchRequest when module == ServerModule.Agent:
                return null; // same as RSBot: not meant for the agent server

            case LoginOpcodes.LoginRequest:
                lock (_sync)
                {
                    _loginSent = true; // the user logged in by hand
                    _loginByBot = false;
                }
                break;

            case LoginOpcodes.AuthRequest when IsLoginOwnedByBot():
            {
                // The client never saw the credentials (the bot logged in), so its 0x6103 is empty.
                uint token = packet.ReadUInt32();
                _logger.LogDebug("Replacing the client's agent login");
                return LoginPackets.AuthRequest(token, _account.Username, _account.Password, _server.Locale);
            }

            case LoginOpcodes.CharacterSelectionJoin:
                lock (_sync) _characterSelected = true;
                break;
        }

        packet.Position = 0;
        if (module == ServerModule.Agent)
            Game?.ObservePacketToServer(packet, fromGameClient: true);
        return packet;
    }

    #endregion

    /// <summary>
    /// A packet the bot sends itself. The game client crashes on answers to NPC requests it did not make (0xB046 "shop
    /// open" for a shop it never opened), so the answers to the bot's NPC select / talk / close are kept from it.
    /// </summary>
    private void FromBot(Packet packet)
    {
        Trace("bot>S", packet);
        ushort answer = packet.Opcode switch
        {
            Opcodes.Client.EntitySelection when IsNpc(packet) => Opcodes.Server.EntitySelection,
            Opcodes.Client.EntityTalk => Opcodes.Server.EntityTalkResponse,
            Opcodes.Client.NpcClose => Opcodes.Server.NpcCloseResponse,
            Opcodes.Client.ItemRepair => Opcodes.Server.ItemRepairResponse,
            Opcodes.Client.ItemMovement when IsShopMove(packet) => Opcodes.Server.ItemMovementResponse,
            _ => 0,
        };
        if (answer == 0)
            return;
        lock (_botNpcAnswers)
            _botNpcAnswers[answer] = Math.Min(_botNpcAnswers.GetValueOrDefault(answer) + 1, 4);
    }

    /// <summary>A buy / sell at an NPC shop (into or out of the inventory or a transport), or a deposit into the storage.</summary>
    private static bool IsShopMove(Packet packet)
    {
        var payload = packet.Payload;
        return payload.Length > 0 && (ItemMovementType)payload[0] is ItemMovementType.ShopToInventory or ItemMovementType.InventoryToShop
            or ItemMovementType.ShopToTransport or ItemMovementType.TransportToShop or ItemMovementType.InventoryToStorage;
    }

    /// <summary>An NPC (not a monster) is selected: monsters' selections stay visible in the game.</summary>
    private bool IsNpc(Packet packet)
    {
        var payload = packet.Payload;
        return payload.Length >= 4 && Game is { } game
            && game.World.Npcs.TryGetValue(BitConverter.ToUInt32(payload), out var npc) && !npc.IsMob;
    }

    /// <summary>The answer to an NPC request the bot sent (and counts it off).</summary>
    private bool IsAnswerToBot(Packet packet)
    {
        lock (_botNpcAnswers)
        {
            if (!_botNpcAnswers.TryGetValue(packet.Opcode, out int due) || due == 0)
                return false;
            _botNpcAnswers[packet.Opcode] = due - 1;
            return true;
        }
    }

    /// <summary>Keeps the packet in <see cref="_trace"/> (keep-alives left out).</summary>
    private void Trace(string direction, Packet packet)
    {
        if (packet.Opcode != 0x2002)
            _trace.Add(direction, packet);
        if (packet.Opcode is 0x7116 or 0xB116 or 0x7420 or 0xB420 or 0x30C9)
            _logger.LogInformation("Pet packet {Direction} 0x{Opcode:X4} [{Payload}]", direction, packet.Opcode, Convert.ToHexString(packet.Payload));
        // Picking up: the character's pick-up action (0x7074 [01][02] ...) and the grab pet's (0x70C5 [pet][08] ...), with the answers.
        if ((packet.Opcode == 0x7074 && packet.Payload.Length > 1 && packet.Payload[1] == 2)
            || (packet.Opcode == 0x70C5 && packet.Payload.Length > 4 && packet.Payload[4] == 8)
            || packet.Opcode is 0xB0C5 || (packet.Opcode == 0xB074 && packet.Payload.Length > 0 && packet.Payload[0] == 2)
            || (packet.Opcode == 0xB034 && packet.Payload.Length > 1 && packet.Payload[1] is 6 or 17 or 26 or 27 or 28))
            _logger.LogInformation("Pick packet {Direction} 0x{Opcode:X4} [{Payload}]", direction, packet.Opcode, Convert.ToHexString(packet.Payload));
        if (IsExchangePacket(packet))
            _logger.LogInformation("Exchange packet {Direction} 0x{Opcode:X4} [{Payload}]", direction, packet.Opcode,
                packet.Payload.Length > 120 ? $"{packet.Payload.Length} bytes: {Convert.ToHexString(packet.Payload[..120])}..." : Convert.ToHexString(packet.Payload));
        if (IsStoragePacket(packet))
            _logger.LogInformation("Storage packet {Direction} 0x{Opcode:X4} [{Payload}]", direction, packet.Opcode,
                packet.Opcode == Opcodes.Server.StorageData ? $"{packet.Payload.Length} bytes" : Convert.ToHexString(packet.Payload));
    }

    /// <summary>
    /// Storage requests and answers (and NPC talks), logged while the storage deposit is worked out: what the game
    /// sends when the player uses the storage is what the bot must send.
    /// </summary>
    private static bool IsExchangePacket(Packet packet)
    {
        var p = packet.Payload;
        return packet.Opcode switch
        {
            >= 0x7081 and <= 0x7084 or >= 0xB081 and <= 0xB084 or >= 0x3085 and <= 0x308C => true,
            Opcodes.Server.PlayerPetitionRequest => true, // 0x3080 both ways: invitations and their answers
            Opcodes.Client.ItemMovement => p.Length > 0 && p[0] is 4 or 5 or 13,
            Opcodes.Server.ItemMovementResponse => p.Length > 1 && p[0] == 1 && p[1] is 4 or 5 or 13,
            _ => false,
        };
    }

    private static bool IsStoragePacket(Packet packet)
    {
        var p = packet.Payload;
        return packet.Opcode switch
        {
            Opcodes.Client.StorageDataRequest or 0xB03C or Opcodes.Client.EntityTalk or Opcodes.Server.EntityTalkResponse
                or Opcodes.Server.StorageDataBegin or Opcodes.Server.StorageData or Opcodes.Server.StorageDataEnd => true,
            Opcodes.Client.ItemMovement => p.Length > 0 && p[0] is 1 or 2 or 3 or 11 or 12,
            Opcodes.Server.ItemMovementResponse => p.Length > 1 && (p[0] != 1 || p[1] is 1 or 2 or 3 or 11 or 12),
            _ => false,
        };
    }

    private bool SendUpstream(Packet packet)
    {
        SroConnection? upstream;
        lock (_sync) upstream = _upstream;
        if (upstream is not { IsConnected: true })
            return false;
        upstream.Send(packet);
        return true;
    }

    private void SetState(BotState state)
    {
        lock (_sync)
        {
            if (_state == state)
                return;
            _state = state;
        }
        StateChanged?.Invoke(state);
    }
}
