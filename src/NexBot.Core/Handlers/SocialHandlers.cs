using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Handlers;

/// <summary>Chat, party, party matching and player petitions.</summary>
internal static class SocialHandlers
{
    /// <summary>0x3026: chat message.</summary>
    public static void Chat(SroClient client, Packet p)
    {
        var type = (ChatType)p.ReadByte();
        string sender;
        switch (type)
        {
            case ChatType.All:
            case ChatType.GameMaster:
            case ChatType.Npc:
                uint uniqueId = p.ReadUInt32();
                sender = client.World.GetEntity(uniqueId)?.Name ?? $"[UID:{uniqueId}]";
                break;
            case ChatType.Private:
            case ChatType.Party:
            case ChatType.Guild:
            case ChatType.Global:
            case ChatType.Stall:
            case ChatType.Union:
            case ChatType.Academy:
                sender = p.ReadAscii();
                break;
            default:
                sender = string.Empty;
                break;
        }
        string message = p.ReadAscii();
        client.RaiseChat(type, sender, message);
    }

    /// <summary>
    /// 0xB025: answer to our chat message. Failures are logged (e.g. a notice from a non-GM account, or a whisper
    /// to a character that is offline).
    /// </summary>
    public static void ChatResponse(SroClient client, Packet p)
    {
        if (p.ReadSuccess())
            return;
        ushort error = p.ReadErrorCode();
        string type = p.Remaining > 0 ? ((ChatType)p.ReadByte()).ToString() : "?";
        client.Logger.LogWarning("The server refused our {Type} chat message (error 0x{Error:X4})", type, error);
        client.RaiseChatFailed(error);
    }

    /// <summary>0x34B3: our guild's data follows (after login, and when it changes).</summary>
    public static void GuildDataBegin(SroClient client, Packet p) =>
        client.GuildDataBuffer = new Packet(Opcodes.Server.GuildData);

    /// <summary>0x3101: a chunk of guild data.</summary>
    public static void GuildData(SroClient client, Packet p) =>
        client.GuildDataBuffer?.WriteBytes(p.Payload);

    /// <summary>
    /// 0x34B4: all guild data received: the guild, its notice and members (<see cref="GuildParser"/>). The data is
    /// also logged (debug) so a server with another layout can be added.
    /// </summary>
    public static void GuildDataEnd(SroClient client, Packet p)
    {
        var buffer = client.GuildDataBuffer;
        client.GuildDataBuffer = null;
        if (buffer is null || buffer.Length < 7)
            return;
        client.Logger.LogDebug("Guild data ({Length} bytes): {Hex}", buffer.Length, Convert.ToHexString(buffer.Payload));
        if (GuildParser.Parse(buffer.Payload, out bool membersRead) is not { } guild)
        {
            client.Logger.LogWarning("Guild data not understood; turn on debug messages and send the \"Guild data\" line.");
            return;
        }
        client.World.Guild = guild;
        if (client.World.Character is { } c)
            c.GuildName = guild.Name;
        if (membersRead)
            client.Logger.LogInformation("Guild: {Name} (Lv {Level}), {Count} members, master {Master}",
                guild.Name, guild.Level, guild.Members.Count, guild.Master?.Name ?? "?");
        else
            client.Logger.LogWarning("Guild: {Name} (Lv {Level}); the member list was not understood: send the \"Guild data\" debug line.", guild.Name, guild.Level);
    }

    /// <summary>0xB069: party match created.</summary>
    public static void PartyMatchCreated(SroClient client, Packet p)
    {
        if (!p.ReadSuccess())
        {
            client.Logger.LogWarning("Party match creation failed (error 0x{Error:X4})", p.ReadErrorCode());
            return;
        }
        uint number = p.ReadUInt32();
        client.Logger.LogInformation("Party match #{Number} created", number);
        client.RaisePartyMatchCreated(number);
    }

    /// <summary>0xB06B: party match deleted.</summary>
    public static void PartyMatchDeleted(SroClient client, Packet p)
    {
        if (p.ReadSuccess())
            client.RaisePartyMatchDeleted(p.ReadUInt32());
    }

    /// <summary>0x706D: someone wants to join our party match.</summary>
    public static void PartyMatchJoinRequest(SroClient client, Packet p)
    {
        uint requestId = p.ReadUInt32();
        uint joinId = p.ReadUInt32();
        p.ReadUInt32(); // match number
        p.ReadUInt32(); // primary mastery
        p.ReadUInt32(); // secondary mastery
        p.ReadByte();
        p.ReadByte();
        p.ReadUInt32(); // join id again
        string name = p.ReadAscii();
        client.RaisePartyMatchJoinRequested(requestId, joinId, name);
    }

    /// <summary>0x3065: we joined a party (full member list).</summary>
    public static void PartyData(SroClient client, Packet p)
    {
        p.ReadUInt32();
        p.ReadUInt32();
        var party = new Party
        {
            Purpose = (PartyPurpose)p.ReadByte(),
            Setup = (PartySetup)p.ReadByte(),
        };
        byte count = p.ReadByte();
        for (int i = 0; i < count; i++)
        {
            var member = ReadMember(p);
            party.Members[member.Id] = member;
        }
        client.World.Party = party;
        client.Logger.LogInformation("Joined party: {Party}", party);
        client.RaisePartyJoined(party);
    }

    /// <summary>0x3864: party changed (dismissed, member joined/left/updated).</summary>
    public static void PartyUpdate(SroClient client, Packet p)
    {
        byte type = p.ReadByte();
        var party = client.World.Party;
        switch (type)
        {
            case 1: // dismissed
                client.World.Party = null;
                if (party is not null)
                    client.RaisePartyLeft(party);
                break;
            case 2: // member joined
                var member = ReadMember(p);
                party?.Members[member.Id] = member;
                client.RaisePartyMemberJoined(member);
                break;
            case 3: // member left
                uint id = p.ReadUInt32();
                party?.Members.Remove(id);
                break;
        }
    }

    private static PartyMember ReadMember(Packet p)
    {
        p.ReadByte();
        var member = new PartyMember
        {
            Id = p.ReadUInt32(),
            Name = p.ReadAscii(),
            ModelId = p.ReadUInt32(),
            Level = p.ReadByte(),
            HpMp = p.ReadByte(),
            Position = p.ReadRegionPosition(),
        };
        byte flag = p.ReadByte(); // 2 = an extra byte follows in party updates
        p.ReadByte();
        p.ReadByte();
        p.ReadByte();
        member.GuildName = p.ReadAscii();
        p.ReadByte();
        if (p.Opcode == Opcodes.Server.PartyUpdate && flag == 2)
            p.ReadByte();
        member.MasteryPrimaryId = p.ReadUInt32();
        member.MasterySecondaryId = p.ReadUInt32();
        return member;
    }

    /// <summary>0x3080: exchange, party, guild ... invitation.</summary>
    public static void PetitionRequest(SroClient client, Packet p)
    {
        var type = (PlayerPetition)p.ReadByte();
        uint uniqueId = p.ReadUInt32();
        if (type is PlayerPetition.PartyCreation or PlayerPetition.PartyInvitation)
        {
            var setup = (PartySetup)p.ReadByte();
            client.Logger.LogDebug("Party invitation from {UniqueId} ({Setup})", uniqueId, setup);
            client.RaisePartyInvitation(uniqueId, setup, type);
        }
        string name = client.World.GetEntity(uniqueId) is NexBot.Game.Entities.Player player ? player.Name : "";
        if (type == PlayerPetition.ExchangeRequest)
        {
            client.World.ExchangeRequest = (uniqueId, name.Length > 0 ? name : $"player {uniqueId}", DateTime.UtcNow);
            client.Logger.LogInformation("Exchange: {Name} wants to exchange (Accept or Refuse on the Inventory page)", client.World.ExchangeRequest.Value.Name);
        }
        client.RaisePetition(type, uniqueId, name);
    }

    /// <summary>0xB06C: a page of the party matching list.</summary>
    public static void PartyMatchList(SroClient client, Packet p)
    {
        if (!p.ReadSuccess())
            return;
        byte pages = p.ReadByte(), page = p.ReadByte(), count = p.ReadByte();
        var matches = new List<PartyMatch>(count);
        for (int i = 0; i < count; i++)
        {
            var match = new PartyMatch
            {
                Number = p.ReadUInt32(),
                MasterJoinId = p.ReadUInt32(),
                MasterName = p.ReadAscii(),
            };
            p.ReadByte(); // race
            match.MemberCount = p.ReadByte();
            match.Setup = (PartySetup)p.ReadByte();
            match.Purpose = (PartyPurpose)p.ReadByte();
            match.LevelMin = p.ReadByte();
            match.LevelMax = p.ReadByte();
            match.Title = p.ReadAscii();
            matches.Add(match);
        }
        client.RaisePartyMatchList(page, pages, matches);
    }
}
