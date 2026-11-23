using System.Globalization;
using NexBot.Data;
using NexBot.Game.Entities;
using NexBot.Handlers;
using NexBot.Sessions;

namespace NexBot.Map;

/// <summary>What a skill slot of the window shows.</summary>
public enum SkillSlotState
{
    /// <summary>Not learned, and a requirement is missing (mastery level, earlier skill or SP).</summary>
    Locked,
    /// <summary>Not learned yet, all requirements met: "+" learns level 1.</summary>
    Learnable,
    /// <summary>Learned, the next level needs something more.</summary>
    Learned,
    /// <summary>Learned, the next level can be learned now: "+".</summary>
    Upgradable,
    /// <summary>Highest level learned: "MAX".</summary>
    Max,
}

/// <summary>A skill line position in the window.</summary>
/// <param name="Level">Learned level (0 = not learned).</param>
/// <param name="NextId">ID of the next level to learn (send it with <see cref="SroClient.LearnSkillAsync"/>), or 0 at max.</param>
/// <param name="Missing">Why the next level cannot be learned now, or null.</param>
public sealed record SkillSlot(
    string Group, string Name, string? Icon, byte Level, byte MaxLevel, uint NextId, uint NextSp, byte NextMasteryLevel,
    SkillSlotState State, string? Missing);

/// <summary>One line of a mastery's window: positions left to right (null = empty socket).</summary>
public sealed record SkillLine(int Index, IReadOnlyList<SkillSlot?> Slots);

/// <summary>A mastery page: header (icon, name, level, raise) and its skill lines.</summary>
/// <param name="RaiseSp">Skill points the next mastery level costs.</param>
/// <param name="RaiseMissing">Why the mastery cannot be raised now, or null.</param>
public sealed record MasteryPage(
    uint Id, string Name, string? Icon, byte Level, uint RaiseSp, string? RaiseMissing, IReadOnlyList<SkillLine> Lines);

/// <summary>A window tab (Weapon / Force, or Physical / Magical / Assist) with its masteries.</summary>
public sealed record SkillTab(string Name, IReadOnlyList<MasteryPage> Masteries);

/// <summary>The game's skill window, built from the character and the database.</summary>
/// <param name="NeedsRebuild">True when the database has no window layout (built by an older NexBot).</param>
public sealed record SkillWindow(
    IReadOnlyList<SkillTab> Tabs, uint Sp, int MasteryTotal, int MasteryLimit, byte CharacterLevel, bool NeedsRebuild)
{
    /// <summary>Positions per line, as in the game window.</summary>
    public const int SlotsPerLine = 8;

    /// <summary>Total mastery levels a Chinese character can have (vSRO 1.188: 3 x the level cap of 110).</summary>
    public const int ChineseMasteryLimit = 330;

    /// <summary>Builds the window, or null when the character is not loaded.</summary>
    public static SkillWindow? Take(SroClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return Build(client);
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
                // the character changed while it was read: read again
            }
        }
    }

    private static SkillWindow? Build(SroClient client)
    {
        if (client.World.Character is not { } c)
            return null;
        var data = client.Data;
        bool european = c.ServerName.Contains("_EU_", StringComparison.OrdinalIgnoreCase);
        var masteryLevels = c.Masteries.Values.ToArray().ToDictionary(m => m.Id, m => m.Level);
        int total = masteryLevels.Values.Sum(l => l);
        int limit = european ? c.Level * 2 : ChineseMasteryLimit;

        var masteries = data.GetMasteries()
            .Where(m => european ? m.Id is >= 513 and < 1000 : m.Id is >= 257 and < 513)
            .ToArray();
        var trees = masteries.ToDictionary(m => m.Id, m => data.GetSkillTree(m.Id));
        if (masteries.Length == 0 || trees.Values.Any(t => t is null))
            return new SkillWindow([], c.Sp, total, limit, c.Level, NeedsRebuild: true);

        // Learned level of each skill line: the character knows one level per line.
        var allLevels = trees.Values.SelectMany(t => t!).ToArray();
        var byId = allLevels.ToDictionary(s => s.Id);
        // The game's requirement columns (ReqLearn_Skill) hold the skill GROUP ID, not a skill ID.
        var byGroupId = allLevels.Where(s => s.GroupId != 0).GroupBy(s => s.GroupId).ToDictionary(g => g.Key, g => g.First());
        var learned = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var skill in c.Skills.Values.ToArray())
        {
            if (byId.TryGetValue(skill.Id, out var entry))
                learned[entry.Group] = Math.Max(learned.GetValueOrDefault(entry.Group), entry.Level);
        }

        string? MissingFor(SkillTreeInfo next, byte masteryLevel)
        {
            var missing = new List<string>();
            if (masteryLevel < next.MasteryLevel)
                missing.Add($"mastery Lv. {next.MasteryLevel}");
            foreach (var (req, reqLevel) in new[] { (next.ReqSkill1, next.ReqSkillLevel1), (next.ReqSkill2, next.ReqSkillLevel2) })
            {
                if (req == 0)
                    continue;
                byte needed;
                if (byGroupId.TryGetValue(req, out var r))
                    needed = reqLevel;
                else if (byId.TryGetValue(req, out r))
                    needed = Math.Max(reqLevel, r.Level); // databases without group IDs
                else
                    continue;
                if (learned.GetValueOrDefault(r.Group) < needed)
                    missing.Add($"{Name(r)} Lv. {needed}");
            }
            if (c.Sp < next.Sp)
                missing.Add(string.Create(CultureInfo.CurrentCulture, $"{next.Sp:N0} SP"));
            return missing.Count == 0 ? null : "Needs " + string.Join(", ", missing);
        }

        var tabs = new List<SkillTab>();
        foreach (var tabGroup in masteries.GroupBy(m => m.Tab).OrderBy(g => g.Key))
        {
            var pages = new List<MasteryPage>();
            foreach (var mastery in tabGroup.OrderBy(m => m.Id))
            {
                var tree = trees[mastery.Id]!;
                byte level = masteryLevels.GetValueOrDefault(mastery.Id);

                var lines = new List<SkillLine>();
                foreach (var lineGroup in tree.GroupBy(s => s.Column).OrderBy(g => g.Key))
                {
                    var slots = new SkillSlot?[Math.Max(SlotsPerLine, lineGroup.Max(s => s.Row) + 1)];
                    foreach (var group in lineGroup.GroupBy(s => s.Group))
                    {
                        var levels = group.OrderBy(s => s.Level).ToArray();
                        var first = levels[0];
                        byte have = learned.GetValueOrDefault(first.Group);
                        byte max = levels[^1].Level;
                        var current = levels.LastOrDefault(s => s.Level <= have) ?? first;
                        var next = levels.FirstOrDefault(s => s.Level > have);
                        string? missing = next is null ? null : MissingFor(next, level);
                        var state = next is null ? SkillSlotState.Max
                            : missing is null ? (have == 0 ? SkillSlotState.Learnable : SkillSlotState.Upgradable)
                            : have == 0 ? SkillSlotState.Locked : SkillSlotState.Learned;
                        slots[first.Row] = new SkillSlot(first.Group, Name(current), NullIfEmpty(current.Icon), have, max,
                            next?.Id ?? 0, next?.Sp ?? 0, next?.MasteryLevel ?? 0, state, missing);
                    }
                    lines.Add(new SkillLine(lineGroup.Key, slots));
                }

                uint raiseSp = level < byte.MaxValue ? data.GetLevel((byte)(level + 1))?.MasterySp ?? 0 : 0;
                string? raiseMissing =
                    level >= c.Level ? $"Max (Lv. {c.Level})."
                    : total >= limit ? $"Mastery level total at its limit ({limit})."
                    : c.Sp < raiseSp ? string.Create(CultureInfo.CurrentCulture, $"Needs {raiseSp:N0} SP.")
                    : null;
                pages.Add(new MasteryPage(mastery.Id, MasteryName(mastery), NullIfEmpty(mastery.Icon), level, raiseSp, raiseMissing, lines));
            }
            string tabName = tabGroup.First().TabName;
            if (tabName.Length == 0 || tabName.StartsWith("UIIT", StringComparison.OrdinalIgnoreCase))
                tabName = TabFallback(european, tabGroup.Key);
            tabs.Add(new SkillTab(tabName, pages));
        }

        return new SkillWindow(tabs, c.Sp, total, limit, c.Level, NeedsRebuild: false);
    }

    private static string TabFallback(bool european, byte tab) => european
        ? tab switch { 0 => "Physical", 1 => "Magical", _ => "Assist" }
        : tab == 0 ? "Weapon" : "Force";

    private static string MasteryName(MasteryInfo m) =>
        m.Name.Length == 0 || m.Name.StartsWith("UIIT", StringComparison.OrdinalIgnoreCase) ? $"Mastery {m.Id}" : m.Name;

    private static string Name(SkillTreeInfo s) => s.Name.Length > 0 ? s.Name : SkillHandlers.SkillLine(s.ServerName);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
