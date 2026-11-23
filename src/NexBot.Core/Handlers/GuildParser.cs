using NexBot.Game;
using NexBot.Network;
using NexBot.Protocol;

namespace NexBot.Handlers;

/// <summary>
/// Reads our guild from the 0x3101 guild data (vSRO 1.188, checked against a capture):
/// <code>
/// uint id, string name, byte level, uint gathered SP,
/// notice part (uint, string title, string notice, byte; servers order it differently, see below),
/// byte member count, then per member:
///   uint id, string name, byte rank (0 = master), byte level, uint donated GP, 16 bytes,
///   string nickname, uint model, byte online, byte
/// </code>
/// The notice part is empty in the capture, so its order is not certain: each known order is tried and the one
/// where every member reads cleanly (sane names and levels, nothing left over) is kept.
/// </summary>
internal static class GuildParser
{
    private enum NoticeLayout { NumberFirst, TextFirst }

    /// <summary>The guild, with members when they could be read; null when even the guild's name is not understood.</summary>
    public static Guild? Parse(ReadOnlySpan<byte> data, out bool membersRead)
    {
        membersRead = false;
        Guild? headerOnly = null;
        foreach (var layout in Enum.GetValues<NoticeLayout>())
        {
            try
            {
                var guild = TryParse(new Packet(Opcodes.Server.GuildData, data), layout, out bool complete);
                if (guild is null)
                    return null; // the header itself is wrong: no layout helps
                if (complete)
                {
                    membersRead = true;
                    return guild;
                }
                headerOnly ??= guild;
            }
            catch (PacketException)
            {
            }
        }
        return headerOnly;
    }

    private static Guild? TryParse(Packet p, NoticeLayout layout, out bool complete)
    {
        complete = false;
        var guild = new Guild { Id = p.ReadUInt32(), Name = p.ReadAscii() };
        if (guild.Id == 0 || !Sane(guild.Name, 32))
            return null;
        guild.Level = p.Remaining > 0 ? p.ReadByte() : (byte)0;
        try
        {
            complete = ReadRest(p, guild, layout);
        }
        catch (PacketException)
        {
            // The data ends early or this order is wrong: keep the guild's name and level.
        }
        if (!complete)
        {
            // What this order read as the notice may be wrong.
            guild.Notice = guild.Message = string.Empty;
            guild.Members.Clear();
            guild.Master = null;
        }
        return guild;
    }

    /// <summary>Points, notice and members; true when every member was read cleanly.</summary>
    private static bool ReadRest(Packet p, Guild guild, NoticeLayout layout)
    {
        guild.GuildPoints = p.ReadUInt32();
        if (layout == NoticeLayout.NumberFirst)
        {
            p.ReadUInt32();
            guild.Notice = p.ReadAscii();
            guild.Message = p.ReadAscii();
        }
        else
        {
            guild.Notice = p.ReadAscii();
            guild.Message = p.ReadAscii();
            p.ReadUInt32();
        }
        p.ReadByte();
        if (!Printable(guild.Notice, lines: true) || !Printable(guild.Message, lines: true))
            return false;

        int count = p.ReadByte();
        if (count > 60)
            return false;
        var members = new List<GuildMember>(count);
        for (int i = 0; i < count; i++)
        {
            var member = new GuildMember { Id = p.ReadUInt32(), Name = p.ReadAscii() };
            member.Rank = p.ReadByte();
            member.Level = p.ReadByte();
            member.GuildPoints = p.ReadUInt32();
            p.ReadBytes(16);
            member.Nickname = p.ReadAscii();
            member.ModelId = p.ReadUInt32();
            member.IsOffline = p.ReadByte() == 0;
            p.ReadByte();
            if (!Sane(member.Name, 20) || member.Level is 0 or > 140 || !Printable(member.Nickname))
                return false;
            members.Add(member);
        }
        if (p.Remaining > 4)
            return false; // more data than the members explain: this order is wrong
        foreach (var member in members)
        {
            guild.Members[member.Id] = member;
            if (member.IsMaster)
                guild.Master = member;
        }
        return true;
    }

    private static bool Sane(string name, int max) => name.Length > 0 && name.Length <= max && Printable(name);

    /// <param name="lines">A notice may have line breaks and tabs.</param>
    private static bool Printable(string text, bool lines = false) =>
        !text.Any(c => char.IsControl(c) && !(lines && c is '\r' or '\n' or '\t'));
}
