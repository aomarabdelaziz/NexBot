using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>Our own guild from the guild data packets (0x34B3, 0x3101 ..., 0x34B4).</summary>
public class GuildTests
{
    private static SroClient Client()
    {
        var client = new SroClient(WorldTests.Data(), NullLogger.Instance, _ => true);
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        return client;
    }

    [Fact]
    public void Guild_name_and_level_are_read_from_the_guild_data_even_when_split_in_chunks()
    {
        using var client = Client();
        var data = new Packet(Opcodes.Server.GuildData).WriteUInt32(4321).WriteAscii("Phoenix").WriteByte(3).WriteUInt32(150000).Payload.ToArray();
        client.HandlePacket(new Packet(Opcodes.Server.GuildDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.GuildData, data.AsSpan(0, 6)));
        client.HandlePacket(new Packet(Opcodes.Server.GuildData, data.AsSpan(6)));
        client.HandlePacket(new Packet(Opcodes.Server.GuildDataEnd));

        Assert.Equal(4321u, client.World.Guild!.Id);
        Assert.Equal("Phoenix", client.World.Guild.Name);
        Assert.Equal(3, client.World.Guild.Level);
        Assert.Equal("Phoenix", client.World.Character!.GuildName);
    }

    /// <summary>Captured from a vSRO 1.188 server: guild "Test" Lv 1, master NexBot_CH (online), member [GM] Admin.</summary>
    private const string Captured = "67030000040054657374010000000000000000000000000002010000000A005B474D5D2041646D696E0A6D000000000000000000000000000000000000000000008407000000000400000009004E6578426F745F4348006E00000000FFFFFFFF00000000000000000000000000007C070000010000";

    [Fact]
    public void Members_master_and_online_state_are_read_from_a_captured_guild()
    {
        using var client = Client();
        client.HandlePacket(new Packet(Opcodes.Server.GuildDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.GuildData, Convert.FromHexString(Captured)));
        client.HandlePacket(new Packet(Opcodes.Server.GuildDataEnd));

        var guild = client.World.Guild!;
        Assert.Equal(("Test", (byte)1), (guild.Name, guild.Level));
        Assert.Equal(2, guild.Members.Count);
        var master = guild.Master!;
        Assert.Equal(("NexBot_CH", (byte)110, 1916u, false), (master.Name, master.Level, master.ModelId, master.IsOffline));
        var admin = guild.Members[1];
        Assert.Equal(("[GM] Admin", (byte)109, 1924u, true, false), (admin.Name, admin.Level, admin.ModelId, admin.IsOffline, admin.IsMaster));
    }

    [Fact]
    public void A_notice_is_read_in_either_order_of_the_notice_part()
    {
        // The capture has an empty notice: write one in each order and check the members still read.
        foreach (bool numberFirst in new[] { true, false })
        {
            var p = new Packet(Opcodes.Server.GuildData).WriteUInt32(7).WriteAscii("Phoenix").WriteByte(2).WriteUInt32(500);
            if (numberFirst)
                p.WriteUInt32(0).WriteAscii("Welcome").WriteAscii("Hunt at 8\nBe nice");
            else
                p.WriteAscii("Welcome").WriteAscii("Hunt at 8\nBe nice").WriteUInt32(0);
            p.WriteByte(0).WriteByte(1);
            p.WriteUInt32(4).WriteAscii("NexBot_CH").WriteByte(0).WriteByte(110).WriteUInt32(0).WriteBytes(new byte[16])
                .WriteAscii("").WriteUInt32(1916).WriteByte(1).WriteByte(0);
            p.WriteByte(0);

            var guild = NexBot.Handlers.GuildParser.Parse(p.Payload, out bool membersRead)!;

            Assert.True(membersRead);
            Assert.Equal(("Welcome", "Hunt at 8\nBe nice"), (guild.Notice, guild.Message));
            Assert.Equal("NexBot_CH", guild.Master!.Name);
        }
    }

    [Fact]
    public void Guild_data_that_does_not_look_like_a_guild_is_ignored()
    {
        using var client = Client();
        client.HandlePacket(new Packet(Opcodes.Server.GuildDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.GuildData, [0, 0, 0, 0, 2, 0, 1, 2, 9]));
        client.HandlePacket(new Packet(Opcodes.Server.GuildDataEnd));

        Assert.Null(client.World.Guild);
    }
}
