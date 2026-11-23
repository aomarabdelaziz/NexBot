namespace NexBot.Configuration;

/// <summary>
/// What the skill trainer levels up on its own: the chosen masteries (raised evenly, lowest first) and the chosen skill
/// lines (each learned up to the highest level the mastery allows). Saved in <c>settings.json</c>.
/// </summary>
public sealed class AutoLevelSettings
{
    /// <summary>Raise the chosen masteries when there are enough skill points.</summary>
    public bool Masteries { get; set; }

    /// <summary>Learn the chosen skills' next levels when there are enough skill points.</summary>
    public bool Skills { get; set; }

    /// <summary>Mastery IDs to raise (257 Bicheon, 274 Lightning ...).</summary>
    public List<uint> MasteryIds { get; set; } = [];

    /// <summary>Skill lines to learn, by code without the level (SKILL_CH_SWORD_SMASH_A).</summary>
    public List<string> SkillGroups { get; set; } = [];

    /// <summary>Keep each mastery this many levels below the character level (0 = up to the character level).</summary>
    public int MasteryGap { get; set; }

    /// <summary>Never spend below this many skill points.</summary>
    public uint KeepSp { get; set; }

    /// <summary>Masteries whose skills are all learned automatically ("ALL SKILLS" on the mastery), no right-click needed.</summary>
    public List<uint> AllSkillMasteries { get; set; } = [];

    /// <summary>An immutable copy for the trainer thread.</summary>
    public AutoLevelOptions ToOptions() => new(
        Masteries, Skills, MasteryIds.ToHashSet(), SkillGroups.ToHashSet(StringComparer.Ordinal), Math.Max(0, MasteryGap), KeepSp, AllSkillMasteries.ToHashSet());
}

/// <summary>A snapshot of <see cref="AutoLevelSettings"/>, safe to read from any thread.</summary>
public sealed record AutoLevelOptions(
    bool Masteries, bool Skills, IReadOnlySet<uint> MasteryIds, IReadOnlySet<string> SkillGroups, int MasteryGap, uint KeepSp,
    IReadOnlySet<uint>? AllSkillMasteries = null)
{
    /// <summary>Masteries whose every skill is auto.</summary>
    public IReadOnlySet<uint> AllSkillsOf => AllSkillMasteries ?? s_none;
    private static readonly IReadOnlySet<uint> s_none = new HashSet<uint>();

    public static readonly AutoLevelOptions Off = new(false, false, new HashSet<uint>(), new HashSet<string>(), 0, 0);

    /// <summary>On when a switch is on. Empty lists mean "what the character already trains" (see <c>SkillTrainer.NextStep</c>).</summary>
    public bool Enabled => Masteries || Skills;
}
