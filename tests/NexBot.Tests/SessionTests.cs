using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Network;
using NexBot.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace NexBot.Tests;

/// <summary>
/// A fake vSRO gateway + agent server, enough to walk through the login flow.
/// Records every packet it receives.
/// </summary>
internal sealed class FakeServer : IAsyncDisposable
{
    public const string User = "tester";
    public const string Pass = "p4ss";
    public const string Character = "Hero";
    public const uint Token = 0xCAFEBABE;

    private readonly TcpListener _gateway = new(IPAddress.Loopback, 0);
    private readonly TcpListener _agent = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<SroConnection> _connections = [];

    public ConcurrentQueue<(string Server, Packet Packet)> Received { get; } = new();

    public ushort GatewayPort => (ushort)((IPEndPoint)_gateway.LocalEndpoint).Port;
    public ushort AgentPort => (ushort)((IPEndPoint)_agent.LocalEndpoint).Port;

    public void Start()
    {
        _gateway.Start();
        _agent.Start();
        _ = AcceptAsync(_gateway, "GatewayServer");
        _ = AcceptAsync(_agent, "AgentServer");
    }

    private async Task AcceptAsync(TcpListener listener, string module)
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener.AcceptSocketAsync(_cts.Token); }
            catch (Exception) { return; }

            var connection = SroConnection.Accept(socket, module);
            lock (_connections) _connections.Add(connection);
            connection.PacketReceived += (c, p) =>
            {
                Received.Enqueue((module, p));
                p.Position = 0;
                Handle(c, module, p);
                return ValueTask.CompletedTask;
            };
            connection.Start();
            connection.Send(new Packet(0x2001).WriteAscii(module).WriteByte(0));
        }
    }

    private void Handle(SroConnection c, string module, Packet p)
    {
        switch (p.Opcode)
        {
            case 0x6100:
                c.Send(new Packet(0xA100).WriteByte(1));
                break;
            case 0x6101:
                c.Send(new Packet(0xA101)
                    .WriteByte(1).WriteByte(1).WriteAscii("SRO_Farm").WriteByte(0)              // farms
                    .WriteByte(1).WriteUInt16(64).WriteAscii("Hades").WriteUInt16(12).WriteUInt16(1000).WriteByte(1).WriteByte(1)
                    .WriteByte(0));
                break;
            case 0x6102:
            {
                p.ReadByte();
                bool ok = p.ReadAscii() == User && p.ReadAscii() == Pass && p.ReadUInt16() == 64;
                c.Send(ok
                    ? new Packet(0xA102).WriteByte(1).WriteUInt32(Token).WriteAscii("127.0.0.1").WriteUInt16(AgentPort)
                    : new Packet(0xA102).WriteByte(2).WriteByte(1).WriteUInt32(1).WriteUInt32(3));
                break;
            }
            case 0x6103:
            {
                bool ok = p.ReadUInt32() == Token && p.ReadAscii() == User && p.ReadAscii() == Pass;
                c.Send(ok ? new Packet(0xA103).WriteByte(1) : new Packet(0xA103).WriteByte(2).WriteByte(1));
                break;
            }
            case 0x7007 when p.ReadByte() == 2:
                c.Send(new Packet(0xB007).WriteByte(2).WriteByte(1).WriteByte(1)
                    .WriteUInt32(1907).WriteAscii(Character).WriteByte(34).WriteByte(80).WriteUInt64(123)
                    .WriteUInt16(100).WriteUInt16(100).WriteUInt16(0).WriteUInt32(5000).WriteUInt32(4000)
                    .WriteByte(0)              // not deleting
                    .WriteByte(0).WriteByte(0) // guild member type, no guild name
                    .WriteByte(0)              // academy
                    .WriteByte(1).WriteUInt32(3632).WriteByte(5)
                    .WriteByte(0));
                break;
            case 0x7001:
                if (p.ReadAscii() == Character)
                {
                    c.Send(new Packet(0xB001).WriteByte(1));
                    c.Send(new Packet(0x34A5));
                    c.Send(new Packet(0x34A6));
                    c.Send(new Packet(0x3020).WriteUInt32(0x1234));
                }
                break;
        }
    }

    public bool Saw(string server, ushort opcode) => Received.Any(r => r.Server == server && r.Packet.Opcode == opcode);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _gateway.Stop();
        _agent.Stop();
        List<SroConnection> list;
        lock (_connections) list = [.. _connections];
        foreach (var c in list)
            await c.DisposeAsync();
    }
}

internal sealed class XunitLogger(ITestOutputHelper output) : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        try { output.WriteLine($"{logLevel,-11} {formatter(state, exception)}"); }
        catch (InvalidOperationException) { /* test finished */ }
    }
}

public class SessionTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static AccountProfile Account(string password = FakeServer.Pass) => new()
    {
        Username = FakeServer.User,
        Password = password,
        ShardName = "Hades",
        CharacterName = FakeServer.Character,
    };

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Clientless_logs_in_and_spawns()
    {
        await using var server = new FakeServer();
        server.Start();
        var profile = new ServerProfile(["127.0.0.1"], server.GatewayPort, 188, 22);
        await using var session = new BotSession(profile, Account(), new XunitLogger(output));

        var shards = new List<ShardInfo>();
        session.ShardsReceived += s => shards.AddRange(s);
        await session.StartClientlessAsync();

        await WaitUntil(() => session.State == BotState.InGame, "in game");
        await WaitUntil(() => server.Saw("AgentServer", 0x3012), "spawn confirmation");
        Assert.True(server.Saw("GatewayServer", 0x6100));
        Assert.Equal("Hades", Assert.Single(shards).Name);
        Assert.True(session.IsClientless);
    }

    [Fact]
    public async Task Client_mode_logs_the_game_client_in_through_the_proxy()
    {
        await using var server = new FakeServer();
        server.Start();
        var profile = new ServerProfile(["127.0.0.1"], server.GatewayPort, 188, 22);
        await using var session = new BotSession(profile, Account(), new XunitLogger(output));
        session.StartProxy(0);

        // A fake sro_client: it only does what the real one does on its own (no credentials typed).
        var clientReceived = new ConcurrentQueue<Packet>();
        (string Host, ushort Port, uint Token)? agent = null;

        async Task<SroConnection> ConnectClient(ushort port)
        {
            var c = await SroConnection.ConnectAsync("127.0.0.1", port, "FakeClient", Timeout);
            c.PacketReceived += (conn, p) =>
            {
                clientReceived.Enqueue(p);
                switch (p.Opcode)
                {
                    case 0x2001 when p.ReadAscii() == "GatewayServer":
                        conn.Send(LoginPackets.PatchRequest(22, 188));
                        break;
                    case 0x2001:
                        conn.Send(LoginPackets.AuthRequest(agent!.Value.Token, "", "", 22)); // client knows no password
                        break;
                    case 0xA100:
                        conn.Send(LoginPackets.ShardListRequest());
                        break;
                    case 0xA102 when p.ReadByte() == 1:
                        agent = (null, 0, p.ReadUInt32());
                        agent = (p.ReadAscii(), p.ReadUInt16(), agent.Value.Token);
                        break;
                    case 0xA103 when p.ReadByte() == 1:
                        conn.Send(LoginPackets.CharacterList());
                        break;
                    case 0x3020:
                        conn.Send(LoginPackets.ConfirmSpawn());
                        break;
                }
                p.Position = 0;
                return ValueTask.CompletedTask;
            };
            c.Start();
            return c;
        }

        await using var gatewayClient = await ConnectClient(session.ProxyPort);
        await WaitUntil(() => agent is not null, "0xA102 with the agent address");

        // The bot logged in for the client and pointed it back at the proxy.
        Assert.Equal("127.0.0.1", agent!.Value.Host);
        Assert.Equal(session.ProxyPort, agent.Value.Port);
        Assert.Equal(FakeServer.Token, agent.Value.Token);

        gatewayClient.Close(); // the real client drops the gateway connection here
        await using var agentClient = await ConnectClient(agent.Value.Port);

        await WaitUntil(() => session.State == BotState.InGame, "in game");
        await WaitUntil(() => clientReceived.Any(p => p.Opcode == 0x3020), "client got 0x3020");
        await WaitUntil(() => server.Saw("AgentServer", 0x3012), "spawn confirmation from the client");

        // The client's empty 0x6103 was replaced with the real credentials (the fake agent checks them).
        Assert.Contains(clientReceived, p => p.Opcode == 0xA103 && p.Payload[0] == 1);
        Assert.Contains(clientReceived, p => p.Opcode == 0xB001);
        Assert.False(session.IsClientless);

        // Going clientless keeps the agent connection alive.
        session.GoClientless();
        Assert.True(session.IsClientless);
        await WaitUntil(() => server.Received.Any(r => r.Server == "AgentServer" && r.Packet.Opcode == 0x2002), "keep-alive");
        Assert.Equal(BotState.InGame, session.State);
    }

    [Fact]
    public async Task Wrong_password_is_reported_and_does_not_reach_the_agent()
    {
        await using var server = new FakeServer();
        server.Start();
        var profile = new ServerProfile(["127.0.0.1"], server.GatewayPort, 188, 22);
        await using var session = new BotSession(profile, Account("wrong"), NullLogger.Instance);
        await session.StartClientlessAsync();

        await WaitUntil(() => server.Saw("GatewayServer", 0x6102), "login request");
        await Task.Delay(300);
        Assert.False(server.Saw("AgentServer", 0x6103));
        Assert.Equal(BotState.Gateway, session.State);
    }
}
