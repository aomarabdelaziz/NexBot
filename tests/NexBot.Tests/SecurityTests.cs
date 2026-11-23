using System.Buffers;
using NexBot.Protocol;
using Xunit;
using Legacy = SRClientless.SilkroadSecurityApi;

namespace NexBot.Tests;

/// <summary>The server role of <see cref="SroSecurity"/> (used by the proxy towards sro_client).</summary>
public class SecurityTests
{
    private static List<Packet> Pump(SroSecurity from, SroSecurity to)
    {
        var writer = new ArrayBufferWriter<byte>();
        from.WriteOutgoing(writer);
        var packets = new List<Packet>();
        if (writer.WrittenCount > 0)
            to.Receive(writer.WrittenSpan, packets);
        return packets;
    }

    private static List<Packet> Pump(SroSecurity from, Legacy.Security to)
    {
        var writer = new ArrayBufferWriter<byte>();
        from.WriteOutgoing(writer);
        if (writer.WrittenCount > 0)
        {
            byte[] data = writer.WrittenSpan.ToArray();
            to.Recv(data, 0, data.Length);
        }
        return [];
    }

    private static List<Packet> Pump(Legacy.Security from, SroSecurity to)
    {
        var packets = new List<Packet>();
        foreach (var kv in from.TransferOutgoing() ?? [])
            to.Receive(kv.Key.Buffer.AsSpan(kv.Key.Offset, kv.Key.Size), packets);
        return packets;
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Server_role_talks_to_the_original_client_implementation(bool blowfish, bool securityBytes, bool handshake)
    {
        var server = new SroSecurity();
        server.GenerateServerHandshake(blowfish, securityBytes, handshake);
        var client = new Legacy.Security();

        var received = new List<Packet>();
        for (int i = 0; i < 4; i++)
        {
            Pump(server, client);
            received.AddRange(Pump(client, server));
        }
        Assert.True(server.HandshakeAccepted);

        // The legacy client sends its 0x2001 identity right after accepting.
        Assert.Contains(received, p => p.Opcode == 0x2001);

        // client -> server, encrypted and plain
        var login = new Legacy.Packet(0x6102, true);
        login.WriteByte((byte)22);
        login.WriteAscii("user");
        login.WriteAscii("secret");
        login.WriteUShort((ushort)64);
        client.Send(login);
        var ping = new Legacy.Packet(0x2002);
        client.Send(ping);
        received = Pump(client, server);
        Assert.Equal([0x6102, 0x2002], received.Select(p => p.Opcode));
        var p6102 = received[0];
        Assert.Equal(22, p6102.ReadByte());
        Assert.Equal("user", p6102.ReadAscii());
        Assert.Equal("secret", p6102.ReadAscii());
        Assert.Equal(64, p6102.ReadUInt16());

        // server -> client, plain, encrypted and massive
        server.Send(new Packet(0x2001).WriteAscii("GatewayServer").WriteByte(0));
        server.Send(new Packet(0xA102, encrypted: true).WriteByte(1).WriteUInt32(1234).WriteAscii("127.0.0.1").WriteUInt16(15884));
        var big = new Packet(0x3013, massive: true);
        for (int i = 0; i < 10000; i++)
            big.WriteByte((byte)i);
        server.Send(big);
        Pump(server, client);
        // The legacy API also hands its 0x5000/0x9000 handshake packets to the caller.
        var incoming = (client.TransferIncoming() ?? []).Where(p => p.Opcode is not (0x5000 or 0x9000)).ToList();
        Assert.Equal([0x2001, 0xA102, 0x3013], incoming.Select(p => (int)p.Opcode));
        Assert.Equal("GatewayServer", incoming[0].ReadAscii());
        Assert.Equal(1, incoming[1].ReadByte());
        Assert.Equal(1234u, incoming[1].ReadUInt());
        Assert.Equal(10000, incoming[2].GetBytes().Length);
        Assert.Equal(big.Payload.ToArray(), incoming[2].GetBytes());
    }

    [Fact]
    public void Server_role_talks_to_the_new_client_role()
    {
        var server = new SroSecurity();
        server.GenerateServerHandshake();
        var client = new SroSecurity();

        var toServer = new List<Packet>();
        for (int i = 0; i < 4; i++)
        {
            Pump(server, client);
            toServer.AddRange(Pump(client, server));
        }
        Assert.True(client.HandshakeAccepted);
        Assert.True(server.HandshakeAccepted);
        Assert.Contains(toServer, p => p.Opcode == 0x2001);

        client.Send(new Packet(0x6103, encrypted: true).WriteUInt32(99).WriteAscii("a").WriteAscii("b"));
        var got = Pump(client, server);
        var p = Assert.Single(got);
        Assert.True(p.Encrypted);
        Assert.Equal(99u, p.ReadUInt32());

        server.Send(new Packet(0xB007).WriteByte(2).WriteByte(1));
        var back = Assert.Single(Pump(server, client));
        Assert.Equal(0xB007, back.Opcode);
    }

    [Fact]
    public void Redirect_stub_has_the_expected_machine_code()
    {
        const int stub = 0x10000000, connect = 0x77117730;
        byte[] code = Loader.ConnectRedirectStub.Build(stub, connect, gatewayPort: 15779, proxyPort: 40000);
        Assert.Equal(
            "8B442408" + "85C0" + "741B" + "66833802" + "7515" + "668178023DA3" + "750D" + "66C740029C40" + "C740047F000001" +
            "8BFF558BEC" + "E9" + Convert.ToHexString(BitConverter.GetBytes(connect + 5 - (stub + 45))),
            Convert.ToHexString(code));
        Assert.Equal(45, code.Length);

        byte[] jump = Loader.ConnectRedirectStub.Jump(connect, stub);
        Assert.Equal(0xE9, jump[0]);
        Assert.Equal(stub - (connect + 5), BitConverter.ToInt32(jump, 1));
    }
}
