using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>Party / guild / academy automation: answering invitations, inviting, leaving, party matching.</summary>
public class SocialTests
{
    private static (SroClient Client, ConcurrentQueue<Packet> Sent) Client()
    {
        var sent = new ConcurrentQueue<Packet>();
        var client = new SroClient(WorldTests.Data(), NullLogger.Instance, p =>
        {
            sent.Enqueue(p);
            return true;
        });
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
        return (client, sent);
    }

    private static Player AddPlayer(SroClient client, uint uid, string name)
    {
        var player = new Player(new ModelInfo(1907, "CHAR_CH_MAN_ADVENTURER", "Man", 1, 1, 0, 0))
        { UniqueId = uid, Name = name, Position = client.World.Character!.Position };
        client.World.Entities[uid] = player;
        client.World.Players[name] = player;
        return player;
    }

    private static Packet Petition(PlayerPetition type, uint inviter)
    {
        var p = new Packet(Opcodes.Server.PlayerPetitionRequest).WriteByte((byte)type).WriteUInt32(inviter);
        if (type is PlayerPetition.PartyCreation or PlayerPetition.PartyInvitation)
            p.WriteByte((byte)PartySetup.All);
        return p;
    }

    private static async Task<bool> Until(Func<bool> condition, int seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(50);
        return condition();
    }

    private static bool Has(ConcurrentQueue<Packet> sent, ushort opcode) => sent.Any(p => p.Opcode == opcode);

    [Fact]
    public async Task Party_invite_from_the_list_is_accepted()
    {
        var (client, sent) = Client();
        using var _ = client;
        AddPlayer(client, 501, "Friend");
        var settings = new SocialSettings();
        settings.Party.AcceptInvites = true;
        settings.Party.AcceptDelay = 0;
        settings.Party.AcceptInviteList.Add("friend");
        using var social = new SocialManager(client, settings);

        client.HandlePacket(Petition(PlayerPetition.PartyInvitation, 501));

        Assert.True(await Until(() => Has(sent, Opcodes.Client.PlayerPetitionResponse)));
        var answer = sent.First(p => p.Opcode == Opcodes.Client.PlayerPetitionResponse);
        Assert.Equal(1, answer.ReadByte());
        Assert.Equal(1, answer.ReadByte());
    }

    [Fact]
    public async Task Party_invite_not_in_the_list_is_refused_or_ignored()
    {
        var (client, sent) = Client();
        using var _ = client;
        AddPlayer(client, 502, "Stranger");
        var settings = new SocialSettings();
        settings.Party.AcceptInvites = true;
        settings.Party.AcceptDelay = 0;
        settings.Party.AcceptInviteList.Add("Friend");
        using var social = new SocialManager(client, settings);

        client.HandlePacket(Petition(PlayerPetition.PartyInvitation, 502));
        await Task.Delay(400);
        Assert.False(Has(sent, Opcodes.Client.PlayerPetitionResponse)); // left to expire

        settings.Party.RefuseNotInList = true;
        client.HandlePacket(Petition(PlayerPetition.PartyInvitation, 502));
        Assert.True(await Until(() => Has(sent, Opcodes.Client.PlayerPetitionResponse)));
        Assert.Equal(2, sent.First(p => p.Opcode == Opcodes.Client.PlayerPetitionResponse).ReadByte());
    }

    [Fact]
    public async Task Guild_invites_are_accepted_only_when_switched_on()
    {
        var (client, sent) = Client();
        using var _ = client;
        var settings = new SocialSettings();
        using var social = new SocialManager(client, settings);

        client.HandlePacket(Petition(PlayerPetition.GuildInvitation, 600));
        await Task.Delay(1300);
        Assert.False(Has(sent, Opcodes.Client.PlayerPetitionResponse));

        settings.Guild.AcceptAllInvites = true;
        client.HandlePacket(Petition(PlayerPetition.GuildInvitation, 600));
        Assert.True(await Until(() => Has(sent, Opcodes.Client.PlayerPetitionResponse)));
    }

    [Fact]
    public async Task Nearby_player_in_the_list_is_invited()
    {
        var (client, sent) = Client();
        using var _ = client;
        AddPlayer(client, 503, "Stranger");
        AddPlayer(client, 504, "Buddy");
        var settings = new SocialSettings();
        settings.Party.InvitePlayers = true;
        settings.Party.AcceptInviteList.Add("Buddy");
        using var social = new SocialManager(client, settings);

        Assert.True(await Until(() => Has(sent, Opcodes.Client.PartyCreation)));
        var invites = sent.Where(p => p.Opcode == Opcodes.Client.PartyCreation).ToList();
        Assert.Single(invites);
        Assert.Equal(504u, invites[0].ReadUInt32());
    }

    [Fact]
    public async Task Leaves_a_party_whose_master_is_not_a_leader()
    {
        var (client, sent) = Client();
        using var _ = client;
        var party = new Party { Setup = PartySetup.All };
        party.Members.Add(1, new PartyMember { Id = 1, Name = "Boss" });
        party.Members.Add(2, new PartyMember { Id = 2, Name = client.World.Character!.Name });
        client.World.Party = party;
        var settings = new SocialSettings();
        settings.Party.LeaveIfLeaderNotInList = true;
        settings.Party.LeaderList.Add("TrustedLeader");
        using var social = new SocialManager(client, settings);

        Assert.True(await Until(() => Has(sent, Opcodes.Client.PartyLeave)));
    }

    [Fact]
    public async Task Stays_in_a_party_led_by_a_listed_leader()
    {
        var (client, sent) = Client();
        using var _ = client;
        var party = new Party { Setup = PartySetup.All };
        party.Members.Add(1, new PartyMember { Id = 1, Name = "TrustedLeader" });
        party.Members.Add(2, new PartyMember { Id = 2, Name = client.World.Character!.Name });
        client.World.Party = party;
        var settings = new SocialSettings();
        settings.Party.LeaveIfLeaderNotInList = true;
        settings.Party.LeaderList.Add("trustedleader");
        using var social = new SocialManager(client, settings);

        await Task.Delay(1500);
        Assert.False(Has(sent, Opcodes.Client.PartyLeave));
    }

    [Fact]
    public void Party_match_list_is_read()
    {
        var (client, _) = Client();
        using var __ = client;
        PartyMatchListEventArgs? got = null;
        client.PartyMatchListReceived += (_, e) => got = e;

        var p = new Packet(Opcodes.Server.PartyMatchListResponse).WriteByte(1)
            .WriteByte(3).WriteByte(0).WriteByte(1)                    // pages, page, count
            .WriteUInt32(42).WriteUInt32(9001).WriteAscii("Leader")     // number, master join id, master
            .WriteByte(0).WriteByte(3).WriteByte((byte)PartySetup.All)  // race, members, setup
            .WriteByte((byte)PartyPurpose.Hunting).WriteByte(20).WriteByte(40).WriteAscii("Hunt at Tarim");
        client.HandlePacket(p);

        Assert.NotNull(got);
        Assert.Equal(3, got!.PageCount);
        var match = Assert.Single(got.Matches);
        Assert.Equal(42u, match.Number);
        Assert.Equal("Leader", match.MasterName);
        Assert.Equal(3, match.MemberCount);
        Assert.Equal(8, match.MemberMax);
        Assert.Equal(20, match.LevelMin);
        Assert.Equal(40, match.LevelMax);
        Assert.Equal("Hunt at Tarim", match.Title);
    }

    [Fact]
    public async Task Joins_a_match_by_title()
    {
        var (client, sent) = Client();
        using var _ = client;
        var settings = new SocialSettings();
        settings.Party.JoinByTitle = "tarim";
        using var social = new SocialManager(client, settings);

        Assert.True(await Until(() => Has(sent, Opcodes.Client.PartyMatchList)));
        client.HandlePacket(new Packet(Opcodes.Server.PartyMatchListResponse).WriteByte(1)
            .WriteByte(1).WriteByte(0).WriteByte(1)
            .WriteUInt32(77).WriteUInt32(9001).WriteAscii("Leader")
            .WriteByte(0).WriteByte(2).WriteByte((byte)PartySetup.All)
            .WriteByte((byte)PartyPurpose.Hunting).WriteByte(1).WriteByte(140).WriteAscii("Hunt at Tarim"));

        Assert.True(await Until(() => Has(sent, Opcodes.Client.PartyMatchJoin)));
        Assert.Equal(77u, sent.First(p => p.Opcode == Opcodes.Client.PartyMatchJoin).ReadUInt32());
    }
}
