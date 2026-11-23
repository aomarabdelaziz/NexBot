using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Automation;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>Walk scripts: the text format, recording (points, teleports) and walking with a teleport.</summary>
public class ScriptTests
{
    private const uint GateId = 500, Destination = 2;

    private static void EnterGame(SroClient client)
    {
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
    }

    /// <summary>The server's side of a teleport: loading screen, then the character again.</summary>
    private static void Teleported(SroClient client)
    {
        client.HandlePacket(new Packet(Opcodes.Server.TeleportReadyRequest));
        EnterGame(client);
    }

    private static void AddGate(SroClient client, Position at)
    {
        var gate = new Teleport(new TeleportInfo(9, "STORE_CH_GATE", "Gate", 0, 0, 0),
            [new TeleportLinkInfo(9, "Jangan", Destination, "Donwhang", "GATE_DW")])
        { UniqueId = GateId, Position = at };
        client.World.Teleports[GateId] = gate;
    }

    private const uint SoldierId = 600, SoldierRefId = 2031, OtherSide = 44;

    /// <summary>A teleport NPC in the middle of town (spawned as an NPC, not a gate).</summary>
    private static InMemoryGameData DataWithSoldier()
    {
        var data = WorldTests.Data();
        data.TeleportLinks[SoldierRefId] =
        [
            new TeleportLinkInfo(SoldierRefId, "Soldier Choiyoung [Teleport]", 43, "Solder Sangnam [Teleport]", "GATE_NPC_CH_SOLDIER_EM1"),
            new TeleportLinkInfo(SoldierRefId, "Soldier Choiyoung [Teleport]", OtherSide, "Soldier Jingyo [Teleport]", "GATE_NPC_CH_SOLDIER_EM1"),
        ];
        return data;
    }

    private static void AddSoldier(SroClient client, Position at)
    {
        var soldier = new Guide(new ModelInfo(SoldierRefId, "NPC_CH_SOLDIER_EM1", "Soldier Choiyoung [Teleport]", 2, 2, 0, 0))
        { UniqueId = SoldierId, Position = at };
        client.World.Entities[SoldierId] = soldier;
        client.World.Npcs[SoldierId] = soldier;
    }

    private static async Task Until(Func<bool> condition, int seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(50);
    }

    [Fact]
    public void A_town_route_ending_where_it_starts_begins_at_its_first_line()
    {
        var script = WalkScript.Parse(["walk,3550,2071,0", "walk,3572,2045,0", "DoBlacksmith", "walk,3595,2089,0", "walk,3549,2066,0", "DoScript"]);
        var spawn = new Position(3549, 2068);

        Assert.Equal(4, script.FindNearestMove(spawn, 300));   // the last point is a step nearer
        Assert.Equal(0, script.FindStartMove(spawn, 300, 30)); // but the walk starts at the top
        Assert.Equal(3, script.FindStartMove(new Position(3594, 2088), 300, 30)); // mid route: from there
    }

    [Fact]
    public void A_phbot_town_script_is_read()
    {
        var script = WalkScript.Parse("""
            walk,6432,1096,-32
            walk,6433,1064,-32
            DoStorage
            walk,6373,1098,0
            DoBlacksmith
            wait,2000
            DoScript
            """);

        Assert.Empty(script.Warnings);
        Assert.Equal([WalkCommandType.Move, WalkCommandType.Move, WalkCommandType.Visit, WalkCommandType.Move, WalkCommandType.Visit, WalkCommandType.Wait],
            script.Commands.Select(c => c.Type));
        Assert.Equal(6432, script.Commands[0].Position.PosX, 0);
        Assert.Equal(1096, script.Commands[0].Position.PosY, 0);
        Assert.Equal("STORAGE", script.Commands[2].Target);
        Assert.Equal("VISIT,BLACKSMITH", script.Commands[4].ToLine());
        Assert.Equal(WalkCommandType.Visit, WalkScript.Parse("VISIT,BLACKSMITH").Commands[0].Type);
    }

    [Fact]
    public void Script_text_round_trips_with_teleports()
    {
        string[] lines =
        [
            "// my route",
            "MOVE,6435,933",
            "MOVE,32769,120,-45,30",
            "WAIT,1500",
            "TELEPORT,STORE_CH_GATE,2,Jangan → Donwhang",
            "RETURN",
            "REPEAT",
            "jump",
        ];
        var script = WalkScript.Parse(lines);
        Assert.Equal(
            [WalkCommandType.Move, WalkCommandType.Move, WalkCommandType.Wait, WalkCommandType.Teleport, WalkCommandType.Return, WalkCommandType.Repeat],
            script.Commands.Select(c => c.Type));
        Assert.Single(script.Warnings); // "jump"
        var teleport = script.Commands[3];
        Assert.Equal(("STORE_CH_GATE", Destination, "Jangan → Donwhang"), (teleport.Teleporter, teleport.DestinationId, teleport.Label));
        Assert.True(script.Commands[1].Position.InDungeon);

        // Written back, every line parses to the same command.
        var again = WalkScript.Parse(WalkScript.Format(script.Commands));
        Assert.Equal(script.Commands.Select(c => c.ToLine()), again.Commands.Select(c => c.ToLine()));
        Assert.Equal("MOVE,32769,120,-45,30", again.Commands[1].ToLine());
    }

    [Fact]
    public async Task Recorder_writes_points_and_the_teleport()
    {
        using var client = new SroClient(WorldTests.Data(), NullLogger.Instance, _ => true);
        EnterGame(client);
        var start = client.World.Character!.Position;
        using var recorder = new ScriptRecorder(client) { MinDistance = 25 };
        Assert.Null(recorder.Start());
        Assert.Single(recorder.Commands); // the starting point

        // Walk 30 units east: a point.
        var east = new Position(start.PosX + 30, start.PosY);
        client.World.Character.Position = east;
        await Until(() => recorder.Commands.Count >= 2);
        Assert.Equal(2, recorder.Commands.Count);

        // The player uses the gate in the game client.
        AddGate(client, east);
        client.ObservePacketToServer(ClientPackets.UseTeleport(GateId, Destination), fromGameClient: true);
        var teleport = recorder.Commands[^1];
        Assert.Equal(WalkCommandType.Teleport, teleport.Type);
        Assert.Equal("TELEPORT,STORE_CH_GATE,2,Jangan → Donwhang", teleport.ToLine());

        // Loading screen, then the arrival point.
        Teleported(client);
        await Until(() => recorder.Commands.Count >= 4);
        recorder.Stop();
        Assert.Equal([WalkCommandType.Move, WalkCommandType.Move, WalkCommandType.Teleport, WalkCommandType.Move],
            recorder.Commands.Select(c => c.Type));
    }

    [Fact]
    public async Task Recorder_merges_points_on_a_straight_line()
    {
        using var client = new SroClient(WorldTests.Data(), NullLogger.Instance, _ => true);
        EnterGame(client);
        var c = client.World.Character!;
        var start = c.Position;
        using var recorder = new ScriptRecorder(client) { MinDistance = 25 };
        recorder.Start();

        foreach (int east in new[] { 30, 60, 90 })
        {
            c.Position = new Position(start.PosX + east, start.PosY);
            await Until(() => recorder.Commands.Count >= 2 && recorder.Commands[^1].Position.PosX >= start.PosX + east - 1);
        }
        // One straight road: the start and the far end only.
        Assert.Equal(2, recorder.Commands.Count);
        Assert.Equal(start.PosX + 90, recorder.Commands[1].Position.PosX, 0);

        // A turn north starts a new line.
        c.Position = new Position(start.PosX + 90, start.PosY + 30);
        await Until(() => recorder.Commands.Count >= 3);
        recorder.Stop();
        Assert.Equal(3, recorder.Commands.Count);
    }

    [Fact]
    public void Speed_updates_and_the_mount_drive_the_position_estimate()
    {
        using var client = new SroClient(WorldTests.Data(), NullLogger.Instance, _ => true);
        EnterGame(client);
        var c = client.World.Character!;
        client.HandlePacket(new Packet(Opcodes.Server.EntitySpeedUpdate).WriteUInt32(c.UniqueId).WriteSingle(20f).WriteSingle(75f));
        Assert.Equal((20f, 75f), (c.SpeedWalking, c.SpeedRunning));

        Assert.Same(c, client.World.Mover);
        var horse = new Cos(new ModelInfo(2192, "COS_C_HORSE2", "Shadow Horse", 2, 3, 1, 2400)) { UniqueId = 7001, Position = c.Position };
        client.World.MyPets[7001] = horse;
        c.RidingUniqueId = 7001;
        Assert.Same(horse, client.World.Mover); // riding: the horse moves (at its own speed)
    }

    [Fact]
    public async Task Walker_uses_the_teleporter_and_goes_on_after_loading()
    {
        var sent = new List<Packet>();
        SroClient live = null;
        using var client = new SroClient(WorldTests.Data(), NullLogger.Instance, p =>
        {
            lock (sent)
                sent.Add(p);
            if (p.Opcode == Opcodes.Client.TeleportUse)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200);
                    Teleported(live);
                });
            return true;
        });
        live = client;
        EnterGame(client);
        var here = client.World.Character!.Position;
        AddGate(client, here);

        // Stand at the gate, teleport, then a point at the arrival (the captured position again).
        var script = WalkScript.Parse(
        [
            $"MOVE,{here.PosX:0},{here.PosY:0}",
            "TELEPORT,STORE_CH_GATE,2,Jangan → Donwhang",
            $"MOVE,{here.PosX:0},{here.PosY:0}",
        ]);
        using var walker = new AutoWalker(client, script, new AutoWalkOptions { Loop = false, PollInterval = TimeSpan.FromMilliseconds(50) });
        walker.Start();
        await Until(() => walker.IsRunning, 2);
        await Until(() => !walker.IsRunning, 15);

        Assert.Equal("Finished.", walker.Status);
        Packet use;
        lock (sent)
            use = Assert.Single(sent, p => p.Opcode == Opcodes.Client.TeleportUse);
        Assert.Equal(GateId, use.ReadUInt32());
        Assert.Equal(2, use.ReadByte());
        Assert.Equal(Destination, use.ReadUInt32());
    }

    [Fact]
    public void Recorder_writes_teleport_npcs_inside_a_town()
    {
        using var client = new SroClient(DataWithSoldier(), NullLogger.Instance, _ => true);
        EnterGame(client);
        AddSoldier(client, client.World.Character!.Position);
        using var recorder = new ScriptRecorder(client);
        recorder.Start();

        client.ObservePacketToServer(ClientPackets.UseTeleport(SoldierId, OtherSide), fromGameClient: true);
        recorder.Stop();
        Assert.Equal("TELEPORT,NPC_CH_SOLDIER_EM1,44,Soldier Choiyoung → Soldier Jingyo", recorder.Commands[^1].ToLine());
    }

    [Fact]
    public async Task Walker_selects_the_teleport_npc_and_teleports()
    {
        var sent = new List<Packet>();
        SroClient live = null;
        using var client = new SroClient(DataWithSoldier(), NullLogger.Instance, p =>
        {
            lock (sent)
                sent.Add(p);
            if (p.Opcode == Opcodes.Client.TeleportUse)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200);
                    Teleported(live);
                });
            return true;
        });
        live = client;
        EnterGame(client);
        var here = client.World.Character!.Position;
        AddSoldier(client, here);

        var script = WalkScript.Parse([$"MOVE,{here.PosX:0},{here.PosY:0}", "TELEPORT,NPC_CH_SOLDIER_EM1,44", $"MOVE,{here.PosX:0},{here.PosY:0}"]);
        using var walker = new AutoWalker(client, script, new AutoWalkOptions { Loop = false, PollInterval = TimeSpan.FromMilliseconds(50) });
        walker.Start();
        await Until(() => walker.IsRunning, 2);
        await Until(() => !walker.IsRunning, 15);

        Assert.Equal("Finished.", walker.Status);
        List<ushort> opcodes;
        lock (sent)
            opcodes = sent.Select(p => p.Opcode).ToList();
        int select = opcodes.IndexOf(Opcodes.Client.EntitySelection), use = opcodes.IndexOf(Opcodes.Client.TeleportUse);
        Assert.True(select >= 0 && select < use, "selects the NPC before teleporting");
        Packet teleport;
        lock (sent)
            teleport = sent[use];
        Assert.Equal(SoldierId, teleport.ReadUInt32());
        Assert.Equal(2, teleport.ReadByte());
        Assert.Equal(OtherSide, teleport.ReadUInt32());
    }

    [Fact]
    public async Task Walker_stops_when_the_teleporter_is_not_there()
    {
        using var client = new SroClient(WorldTests.Data(), NullLogger.Instance, _ => true);
        EnterGame(client);
        var here = client.World.Character!.Position;
        var script = WalkScript.Parse([$"MOVE,{here.PosX:0},{here.PosY:0}", "TELEPORT,STORE_CH_GATE,2"]);
        using var walker = new AutoWalker(client, script, new AutoWalkOptions { Loop = false, PollInterval = TimeSpan.FromMilliseconds(50) });
        walker.Start();
        await Until(() => walker.IsRunning, 2);
        await Until(() => !walker.IsRunning, 15);
        Assert.Contains("teleport did not work", walker.Status);
    }
}
