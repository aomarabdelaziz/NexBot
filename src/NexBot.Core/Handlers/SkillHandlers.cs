using NexBot.Game;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Handlers;

/// <summary>
/// Skill window answers (formats as in RSBot's SkillLearnResponse / SkillMasteryLearnResponse / EntityUpdatePoints).
/// </summary>
internal static class SkillHandlers
{
    /// <summary>
    /// 0xB0A1: [1][uint skill ID] when a skill was learned or raised (the new level replaces the old one), or
    /// [2][ushort error].
    /// </summary>
    public static void SkillLearned(SroClient client, Packet p)
    {
        if (!p.ReadSuccess())
        {
            client.RaiseSkillLearned(false, 0, p.ReadErrorCode());
            return;
        }

        uint id = p.ReadUInt32();
        if (client.World.Character is { } c && client.Data.GetSkill(id) is { } info)
        {
            string line = SkillLine(info.ServerName);
            foreach (var old in c.Skills.Values.Where(s => SkillLine(s.ServerName) == line).ToArray())
                c.Skills.Remove(old.Id);
            c.Skills[id] = new Skill(info) { Enabled = true };
        }
        client.RaiseSkillLearned(true, id, 0);
    }

    /// <summary>0xB0A2: [1][uint mastery ID][byte new level], or [2][ushort error].</summary>
    public static void MasteryRaised(SroClient client, Packet p)
    {
        if (!p.ReadSuccess())
        {
            client.RaiseMasteryRaised(false, 0, 0, p.ReadErrorCode());
            return;
        }

        uint id = p.ReadUInt32();
        byte level = p.ReadByte();
        if (client.World.Character is { } c)
        {
            if (c.Masteries.TryGetValue(id, out var mastery))
                mastery.Level = level;
            else
                c.Masteries[id] = new Mastery(id, client.Data.GetMastery(id)) { Level = level };
        }
        client.RaiseMasteryRaised(true, id, level, 0);
    }

    /// <summary>0x304E: [byte type] then 1 gold (ulong), 2 skill points (uint), 4 berserk points (byte).</summary>
    public static void InfoUpdate(SroClient client, Packet p)
    {
        if (client.World.Character is not { } c)
            return;
        switch (p.ReadByte())
        {
            case 1:
                c.Gold = p.ReadUInt64();
                break;
            case 2:
                c.Sp = p.ReadUInt32();
                break;
            case 4:
                c.BerserkPoints = p.ReadByte();
                break;
        }
    }

    /// <summary>The skill without its level suffix: SKILL_CH_SWORD_SMASH_C_09 → SKILL_CH_SWORD_SMASH_C.</summary>
    internal static string SkillLine(string serverName)
    {
        int cut = serverName.LastIndexOf('_');
        return cut > 0 && serverName.AsSpan(cut + 1).IndexOfAnyExceptInRange('0', '9') < 0 && cut + 1 < serverName.Length
            ? serverName[..cut]
            : serverName;
    }
}
