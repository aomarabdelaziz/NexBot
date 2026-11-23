using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Map;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>Map training area, session statistics and sound alerts.</summary>
public class MapSoundTests
{
    private const uint TigerRef = 1954;

    private static SroClient Client()
    {
        var data = WorldTests.Data();
        data.Models[TigerRef] = new ModelInfo(TigerRef, "MOB_CH_TIGERWOMAN", "Tiger Girl", 1, 2, 1, 0);
        var client = new SroClient(data, NullLogger.Instance, _ => true);
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
        return client;
    }

    private static (SoundAlerts Alerts, List<SoundAlertEventArgs> Rung) Alerts(SroClient client, SoundSettings settings)
    {
        var alerts = new SoundAlerts(client, settings);
        var rung = new List<SoundAlertEventArgs>();
        alerts.Alert += (_, e) => rung.Add(e);
        return (alerts, rung);
    }

    [Fact]
    public void Training_circle_contains_points_within_the_radius()
    {
        var area = new TrainingAreaSettings { X = 100, Y = 200, Radius = 50 };
        Assert.True(area.Contains(130, 230));
        Assert.False(area.Contains(140, 240));
        Assert.False(new TrainingAreaSettings().Contains(0, 0)); // not set
    }

    [Fact]
    public void Training_polygon_is_used_when_switched_on()
    {
        // An L shape drawn clockwise; (15, 15) is in the cut-out corner.
        var area = new TrainingAreaSettings
        {
            X = 5, Y = 5, Radius = 100,
            Polygon = [new(0, 0), new(0, 20), new(10, 20), new(10, 10), new(20, 10), new(20, 0)],
        };
        Assert.True(area.Contains(15, 15)); // the circle
        area.UsePolygon = true;
        Assert.True(area.Contains(5, 15));
        Assert.True(area.Contains(15, 5));
        Assert.False(area.Contains(15, 15));
        Assert.False(area.Contains(-1, 5));

        area.Polygon.Reverse(); // counter-clockwise works the same
        Assert.True(area.Contains(5, 15));
        Assert.False(area.Contains(15, 15));
    }

    [Fact]
    public void Unique_notice_is_read_and_rings_for_listed_uniques()
    {
        using var client = Client();
        var settings = new SoundSettings();
        settings.Get(SoundEvent.UniqueSpawned).Enabled = true;
        settings.Get(SoundEvent.SpecificUnique).Enabled = true;
        settings.Get(SoundEvent.SpecificUnique).File = @"C:\sounds\tiger.wav";
        settings.Uniques.Add("tiger");
        var (alerts, rung) = Alerts(client, settings);
        using var _ = alerts;
        UniqueNoticeEventArgs? notice = null;
        client.UniqueNotice += (_, e) => notice = e;

        client.HandlePacket(new Packet(Opcodes.Server.UniqueAction).WriteByte(5).WriteByte(0x0C).WriteUInt32(TigerRef));

        Assert.NotNull(notice);
        Assert.True(notice!.Spawned);
        Assert.Equal("Tiger Girl", notice.Name);
        Assert.Equal([SoundEvent.UniqueSpawned, SoundEvent.SpecificUnique], rung.Select(r => r.Event));
        Assert.Equal(@"C:\sounds\tiger.wav", rung[1].File);

        client.HandlePacket(new Packet(Opcodes.Server.UniqueAction).WriteByte(6).WriteByte(0x0C).WriteUInt32(TigerRef).WriteAscii("Hero"));
        Assert.False(notice.Spawned);
        Assert.Equal("Hero", notice.KilledBy);
        Assert.Equal(2, rung.Count); // a kill does not ring
    }

    [Fact]
    public void Sounds_ring_only_when_on_not_muted_and_not_repeated()
    {
        using var client = Client();
        var settings = new SoundSettings();
        var (alerts, rung) = Alerts(client, settings);
        using var _ = alerts;

        client.RaiseChat(ChatType.Private, "Friend", "hi");
        Assert.Empty(rung); // off

        settings.Get(SoundEvent.PrivateMessage).Enabled = true;
        client.RaiseChat(ChatType.Private, "Friend", "hi");
        client.RaiseChat(ChatType.Private, "Friend", "hi"); // the same message right after: once
        client.RaiseChat(ChatType.All, "Friend", "hello");  // not a whisper
        Assert.Single(rung);
        Assert.Equal(SoundEvent.PrivateMessage, rung[0].Event);

        settings.Muted = true;
        client.RaiseChat(ChatType.Private, "Other", "yo");
        Assert.Single(rung);
    }

    [Fact]
    public void Gm_titan_and_attacks_ring()
    {
        using var client = Client();
        var settings = new SoundSettings();
        foreach (var e in Enum.GetValues<SoundEvent>())
            settings.Get(e).Enabled = true;
        var (alerts, rung) = Alerts(client, settings);
        using var _ = alerts;
        var here = client.World.Character!.Position;
        var model = new ModelInfo(1907, "CHAR_CH_MAN_ADVENTURER", "Man", 1, 1, 0, 0);

        var gm = new Player(model) { UniqueId = 900, Name = "[GM]Helper", Position = here };
        client.World.Entities[900] = gm;
        client.RaiseEntitySpawned(gm);
        var titan = new Mob(new ModelInfo(1960, "MOB_TITAN", "Titan", 1, 2, 1, 0)) { UniqueId = 901, MobType = MobType.Titan, Position = here };
        client.RaiseEntitySpawned(titan);
        var rogue = new Player(model) { UniqueId = 902, Name = "Rogue", Position = here };
        client.World.Entities[902] = rogue;
        uint me = client.World.Character.UniqueId;
        client.RaiseDamage(new DamageEventArgs(902, "Rogue", me, "Test", 1, 10, default, default));
        client.RaiseDamage(new DamageEventArgs(me, "Test", 902, "Rogue", 1, 10, default, default));
        client.RaiseCharacterDied(new CharacterDiedEventArgs(902, "Rogue", true));

        Assert.Equal(
            [SoundEvent.GmSpawned, SoundEvent.TitanSpawned, SoundEvent.Attacked, SoundEvent.AttackingPlayer, SoundEvent.PlayerDied],
            rung.Select(r => r.Event));
    }

    [Fact]
    public void Session_stats_count_our_kills_and_gold()
    {
        using var client = Client();
        using var stats = new SessionStats(client);
        var c = client.World.Character!;
        stats.Sample(); // base
        c.Gold += 1500;

        var ours = new Mob(new ModelInfo(1955, "MOB_A", "Mob", 1, 2, 1, 0)) { UniqueId = 801 };
        var theirs = new Mob(new ModelInfo(1955, "MOB_A", "Mob", 1, 2, 1, 0)) { UniqueId = 802 };
        client.RaiseDamage(new DamageEventArgs(c.UniqueId, c.Name, 801, "Mob", 1, 50, default, default));
        client.RaiseMobDied(ours);
        client.RaiseMobDied(theirs); // someone else's kill

        var s = stats.Sample();
        Assert.Equal(1, s.Kills);
        Assert.Equal(1500, s.GoldChange);
        Assert.Equal(0, s.Deaths);
    }
}
