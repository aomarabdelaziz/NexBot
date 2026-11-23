using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;

namespace NexBot.Automation;

/// <summary>What a skill is, for the Attack tab's lists.</summary>
public enum SkillKind
{
    /// <summary>An active skill: attacks, heals, taunts ... (params start with 0).</summary>
    Attack,
    /// <summary>A buff that lasts (params start with 3).</summary>
    Buff,
    /// <summary>A Chinese imbue (…_GIGONGTA_…): a short buff that adds damage to the next hits.</summary>
    Imbue,
    /// <summary>Always on, never cast (params start with 4).</summary>
    Passive,
}

/// <summary>
/// The hunting bot's choices (phBot's Attack tab): the monster types with their skill lists, which skill to cast,
/// which buff to recast, which monster to attack (pure: tested).
/// </summary>
public static class AttackPlan
{
    /// <summary>The monster types of the skill lists, as phBot names them.</summary>
    public static IReadOnlyList<string> Types { get; } =
        ["General", "Champion", "Giant", "Party General", "Party Champion", "Party Giant", "Unique", "Titan", "Strong", "Elite", "Event"];

    /// <summary>Strength of each type (for "Attack lower monsters first").</summary>
    private static readonly Dictionary<string, int> s_strength = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Event"] = 0, ["General"] = 1, ["Champion"] = 2, ["Strong"] = 3, ["Party General"] = 4, ["Giant"] = 5, ["Elite"] = 6,
        ["Party Champion"] = 7, ["Titan"] = 8, ["Party Giant"] = 9, ["Unique"] = 10,
    };

    /// <summary>The next lower type ("Use lower skills ..."): Giant → Champion → General.</summary>
    public static string? Lower(string type) => type switch
    {
        "Champion" => "General",
        "Strong" or "Giant" => "Champion",
        "Titan" or "Elite" => "Giant",
        "Unique" => "Titan",
        "Party General" => "General",
        "Party Champion" => "Party General",
        "Party Giant" => "Party Champion",
        "Event" => "General",
        _ => null,
    };

    /// <summary>The skill list type of a monster.</summary>
    public static string TypeOf(MobType type)
    {
        if (type == MobType.Event)
            return "Event";
        bool party = (byte)type >= 0x10;
        var single = (MobType)((byte)type & 0x0F);
        if (party && single is MobType.General or MobType.Champion or MobType.Giant)
            return "Party " + TypeOf(single);
        return single switch
        {
            MobType.Champion => "Champion",
            MobType.Giant => "Giant",
            MobType.Titan => "Titan",
            MobType.Strong => "Strong",
            MobType.Elite => "Elite",
            MobType.Unique or MobType.Unique2 => "Unique",
            _ => "General",
        };
    }

    public static int Strength(MobType type) => s_strength.GetValueOrDefault(TypeOf(type), 1);

    /// <summary>A skill's line: its server name without the level (SKILL_CH_SWORD_SMASH_A_01 → SKILL_CH_SWORD_SMASH_A).</summary>
    public static string LineOf(string serverName)
    {
        int at = serverName.LastIndexOf('_');
        return at > 0 && at < serverName.Length - 1 && serverName.AsSpan(at + 1).ToString().All(char.IsAsciiDigit)
            ? serverName[..at] : serverName;
    }

    public static SkillKind KindOf(SkillInfo info)
    {
        string first = info.Params.Split('|', 2)[0];
        if (first == "4" || info.ServerName.Contains("_PASSIVE", StringComparison.OrdinalIgnoreCase))
            return SkillKind.Passive;
        if (info.ServerName.Contains("_GIGONGTA_", StringComparison.OrdinalIgnoreCase))
            return SkillKind.Imbue;
        if (first == "3" || (info.Duration > 0 && first != "0"))
            return SkillKind.Buff;
        return SkillKind.Attack;
    }

    /// <summary>The skills for a monster type: its own list, else the lower types' (or General's).</summary>
    public static (string Type, IReadOnlyList<string> Skills) SkillsFor(AttackSettings s, string type)
    {
        for (string? t = type; t is not null; t = s.UseLowerSkills ? Lower(t) : (t == "General" ? null : "General"))
        {
            if (s.Skills.TryGetValue(t, out var list) && list.Count > 0)
                return (t, list);
        }
        return (type, []);
    }

    /// <summary>The weapon fits the skill (skills with no weapon fit any).</summary>
    public static bool WeaponFits(SkillInfo info, byte equippedWeapon)
    {
        if (info.WeaponFirst == WeaponType.None || (byte)info.WeaponFirst == 0)
            return true;
        return equippedWeapon == (byte)info.WeaponFirst || (info.WeaponSecond != WeaponType.None && equippedWeapon == (byte)info.WeaponSecond);
    }

    /// <summary>The learned skill of a line, or null.</summary>
    public static Skill? Learned(IEnumerable<Skill> skills, string line) =>
        skills.Where(k => string.Equals(LineOf(k.ServerName), line, StringComparison.OrdinalIgnoreCase)).MaxBy(k => k.Info.SkillLevel);

    /// <summary>The skill can be cast now: learned, ready, enough MP, the weapon fits.</summary>
    public static bool Ready(Skill skill, uint mp, byte weapon) =>
        skill.IsCastingEnabled && mp >= skill.Info.Mana && WeaponFits(skill.Info, weapon);

    /// <summary>
    /// The next attack skill: in the list's order starting after the last one cast (<paramref name="next"/>), or the
    /// first ready one. Null: none is ready (use the common attack).
    /// </summary>
    public static Skill? NextSkill(IReadOnlyList<string> lines, IEnumerable<Skill> learned, uint mp, byte weapon, bool inOrder, ref int next)
    {
        var known = learned.ToList();
        int count = lines.Count;
        int start = inOrder ? next : 0;
        for (int i = 0; i < count; i++)
        {
            int at = (start + i) % count;
            if (Learned(known, lines[at]) is { } skill && Ready(skill, mp, weapon))
            {
                next = (at + 1) % count;
                return skill;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ special skills (by their params' codes)

    /// <summary>Skill param codes: "heal", "resu" (resurrect), "cure", "tele" (Ghost Walk), "tel2" (Teleport).</summary>
    public const int HealCode = 1751474540, ResurrectCode = 1919251317, CureCode = 1668641396, GhostWalkCode = 1952803941, TeleportCode = 1952803890;

    public static bool HasCode(SkillInfo info, int code)
    {
        string text = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return info.Params.Split('|').Contains(text);
    }

    public static bool IsResurrect(SkillInfo i) => HasCode(i, ResurrectCode) || i.ServerName.Contains("_RESURRECTION_", StringComparison.OrdinalIgnoreCase);
    public static bool IsHeal(SkillInfo i) => HasCode(i, HealCode) && !IsResurrect(i);
    public static bool IsSelfHeal(SkillInfo i) => i.ServerName.Contains("SELFHEAL", StringComparison.OrdinalIgnoreCase);
    public static bool IsCure(SkillInfo i) => HasCode(i, CureCode);
    public static bool IsTeleport(SkillInfo i) => HasCode(i, GhostWalkCode) || HasCode(i, TeleportCode);
    public static bool IsDot(SkillInfo i) => i.ServerName.Contains("_DOT", StringComparison.OrdinalIgnoreCase);
    public static bool IsDevilSpirit(SkillInfo i) => i.ServerName.Contains("_NASRUN_", StringComparison.OrdinalIgnoreCase);

    public enum HealKind { None, Self, Group, Member }

    /// <summary>
    /// Who to heal (phBot's Healing tab): yourself under the self HP %, then everyone when the party's average HP %
    /// (you and the living members) is under the group %, then the party member with the lowest HP under the member % (dead ones
    /// are left to Resurrect).
    /// </summary>
    public static (HealKind Kind, string? Member) WhoToHeal(AttackSettings s, int selfHp, IReadOnlyList<(string Name, int Hp)> members)
    {
        if (s.HealSelfBelow > 0 && selfHp < s.HealSelfBelow)
            return (HealKind.Self, null);
        var alive = members.Where(m => m.Hp > 0).ToList();
        if (s.GroupHealBelow > 0 && alive.Count > 0 && (alive.Sum(m => m.Hp) + selfHp) / (double)(alive.Count + 1) < s.GroupHealBelow)
            return (HealKind.Group, null);
        if (s.HealMemberBelow > 0 && members.Where(m => m.Hp > 0 && m.Hp < s.HealMemberBelow).OrderBy(m => m.Hp).FirstOrDefault() is { Name: not null } low)
            return (HealKind.Member, low.Name);
        return (HealKind.None, null);
    }

    /// <summary>The area of the Conditions tab for this level: the highest condition at or under it, or null.</summary>
    public static string? AreaForLevel(IEnumerable<TrainingCondition> conditions, int level) =>
        conditions.Where(c => c.Level <= level && c.Area.Length > 0).OrderByDescending(c => c.Level).FirstOrDefault()?.Area;

    /// <summary>Why luring stops now (phBot's Lure tab), or null to go on.</summary>
    public static string? LureStop(AttackSettings s, int deadMembers, int partyGiants, int partyChampions, int monsters, bool partyAway)
    {
        if (s.LureStopIfDead > 0 && deadMembers > s.LureStopIfDead)
            return $"{deadMembers} party members are dead";
        if (s.LureStopIfPartyGiants > 0 && partyGiants > s.LureStopIfPartyGiants)
            return $"{partyGiants} party giants are in the area";
        if (s.LureStopIfPartyChampions > 0 && partyChampions > s.LureStopIfPartyChampions)
            return $"{partyChampions} party champions are in the area";
        if (s.LureStopIfMonsters > 0 && monsters > s.LureStopIfMonsters)
            return $"{monsters} monsters are in the area";
        if (s.LureStopIfPartyAway && partyAway)
            return "party members are not near the training area";
        return null;
    }

    /// <summary>A random point inside the training area, at most <paramref name="spread"/> of the radius from the middle.</summary>
    public static (double X, double Y) RandomPoint(TrainingAreaSettings area, Random random, double spread = 0.7)
    {
        var (cx, cy) = area.Center;
        double reach = area.UsePolygon && area.HasPolygon
            ? area.Polygon.Max(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)))
            : area.Radius;
        for (int i = 0; i < 20; i++)
        {
            double angle = random.NextDouble() * Math.PI * 2, length = Math.Sqrt(random.NextDouble()) * reach * spread;
            double x = cx + Math.Cos(angle) * length, y = cy + Math.Sin(angle) * length;
            if (area.Contains(x, y))
                return (x, y);
        }
        return (cx, cy);
    }

    /// <summary>The buff is on the character (same skill group).</summary>
    public static bool IsOn(Model who, Skill skill) =>
        who.Buffs.Values.Any(b => b.Id == skill.Id || (skill.GroupId != 0 && b.GroupId == skill.GroupId));

    /// <summary>
    /// The monster to attack:
    /// <list type="number">
    /// <item>monsters attacking you (the weakest type first with "Attack lower monsters first"), then those attacking the party,</item>
    /// <item>else by the monster preferences, then the nearest.</item>
    /// </list>
    /// </summary>
    public static Mob? ChooseTarget(AttackSettings s, IReadOnlyList<string> preferences, IReadOnlyCollection<Mob> monsters,
        Position here, ISet<uint> attackingMe, ISet<uint> attackingParty)
    {
        var mine = monsters.Where(m => attackingMe.Contains(m.UniqueId)).ToList();
        if (mine.Count > 0)
        {
            return s.AttackLowerFirst
                ? mine.OrderBy(m => Strength(m.MobType)).ThenBy(m => m.Position.DistanceTo(here)).First()
                : MonsterPreferences.Choose(preferences, mine, here);
        }
        if (s.ProtectParty && monsters.Where(m => attackingParty.Contains(m.UniqueId)).ToList() is { Count: > 0 } party)
            return MonsterPreferences.Choose(preferences, party, here);
        return MonsterPreferences.Choose(preferences, monsters, here);
    }
}
