using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Automation;
using NexBot.Data;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>GM console: only for GM characters, and the spawn packet.</summary>
public class GameMasterTests
{
    [Fact]
    public void Loadmonster_needs_a_gm_and_sends_model_count_and_type()
    {
        var data = WorldTests.Data();
        data.Models[1947] = new ModelInfo(1947, "MOB_CH_TIGER", "Tiger", 2, 1, 1, 509);
        var sent = new ConcurrentQueue<Packet>();
        using var client = new SroClient(data, NullLogger.Instance, p => { sent.Enqueue(p); return true; });
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
        using var gm = new GameMasterConsole(client);
        client.World.Character!.IsGameMaster = false;

        Assert.Contains("not a game master", gm.Execute("/loadmonster 1947 10 1"));
        Assert.Empty(sent);

        client.World.Character!.IsGameMaster = true;
        Assert.Contains("Tiger", gm.Execute("/loadmonster 1947 10 1"));
        var p = Assert.Single(sent);
        Assert.Equal(Opcodes.Client.GameMasterCommand, p.Opcode);
        Assert.Equal("06009B0700000A01", Convert.ToHexString(p.ToArray()));
        Assert.Contains("loadmonster", gm.Execute("help"));
    }
}
