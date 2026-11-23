using NexBot.Game;
using NexBot.Game.Entities;

namespace NexBot.Automation;

/// <summary>
/// Monster types (General, Champion, Giant ...) and the monster preferences (phBot's Protection, Monster Preferences):
/// an ordered list of types and exact monsters; the higher in the list, the more preferred (pure: tested).
/// </summary>
public static class MonsterPreferences
{
    /// <summary>The monster types, as the Protection page names them.</summary>
    public static IReadOnlyList<string> Types { get; } = ["General", "Champion", "Giant", "Titan", "Strong", "Elite", "Unique", "Party"];

    /// <summary>
    /// The monster is of this type. The party versions count as their type too (a party champion is a Champion);
    /// "Party" is any party monster.
    /// </summary>
    public static bool IsType(MobType type, string wanted)
    {
        if (type == MobType.Event)
            return false;
        bool party = (byte)type >= 0x10;
        var single = (MobType)((byte)type & 0x0F);
        return wanted.Trim() switch
        {
            "Party" => party,
            "General" => single == MobType.General,
            "Champion" => single == MobType.Champion,
            "Unique" => single is MobType.Unique or MobType.Unique2,
            "Giant" => single == MobType.Giant,
            "Titan" => single == MobType.Titan,
            "Strong" => single == MobType.Strong,
            "Elite" => single == MobType.Elite,
            _ => false,
        };
    }

    /// <summary>The monster is of one of the types in a comma-separated list ("Giant,Unique").</summary>
    public static bool IsAnyType(MobType type, string wanted) =>
        wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(t => IsType(type, t));

    /// <summary>The type the game gives a monster, for showing it ("Party Giant", "Unique").</summary>
    public static string TypeName(MobType type)
    {
        string single = Types.FirstOrDefault(t => t != "Party" && IsType((MobType)((byte)type & 0x0F), t)) ?? type.ToString();
        return (byte)type >= 0x10 && type != MobType.Event ? "Party " + single : single;
    }

    /// <summary>A preference that is a monster type (else an exact monster, by its server name MOB_...).</summary>
    public static bool IsTypeEntry(string entry) => Types.Contains(entry.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// This preference fits the monster: a type, an exact monster (server name MOB_...), or, for lists made before
    /// the monster search, a part of its name.
    /// </summary>
    public static bool Matches(string entry, Mob mob)
    {
        entry = entry.Trim();
        if (entry.Length == 0)
            return false;
        if (IsTypeEntry(entry))
            return IsType(mob.MobType, Types.First(t => string.Equals(t, entry, StringComparison.OrdinalIgnoreCase)));
        if (entry.StartsWith("MOB_", StringComparison.OrdinalIgnoreCase))
            return string.Equals(mob.ServerName, entry, StringComparison.OrdinalIgnoreCase);
        return mob.Name.Contains(entry, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The monster's place in the preferences (0 = most preferred); int.MaxValue when none fits.</summary>
    public static int Rank(IReadOnlyList<string> preferences, Mob mob)
    {
        for (int i = 0; i < preferences.Count; i++)
            if (Matches(preferences[i], mob))
                return i;
        return int.MaxValue;
    }

    /// <summary>The monster to attack: the most preferred, then the nearest. Null when there is none.</summary>
    public static Mob? Choose(IReadOnlyList<string> preferences, IEnumerable<Mob> monsters, Position here) =>
        monsters.OrderBy(m => Rank(preferences, m)).ThenBy(m => m.Position.DistanceTo(here)).FirstOrDefault();

    /// <summary>
    /// "Switch monster based on position in the list": a more preferred monster than the one attacked now, or null.
    /// </summary>
    public static Mob? BetterThan(IReadOnlyList<string> preferences, Mob current, IEnumerable<Mob> monsters, Position here)
    {
        int now = Rank(preferences, current);
        return monsters.Where(m => m.UniqueId != current.UniqueId && Rank(preferences, m) < now)
            .OrderBy(m => Rank(preferences, m)).ThenBy(m => m.Position.DistanceTo(here)).FirstOrDefault();
    }
}
