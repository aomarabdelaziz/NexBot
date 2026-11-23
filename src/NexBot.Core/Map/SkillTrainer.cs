using Microsoft.Extensions.Logging;
using NexBot.Configuration;
using NexBot.Sessions;

namespace NexBot.Map;

/// <summary>The next thing the trainer does: raise a mastery or learn a skill level.</summary>
public sealed record TrainerStep(bool IsMastery, uint Id, string Name, uint Sp, string Key);

/// <summary>
/// Levels up the chosen skills and masteries on its own while the character is in game (with or without the game
/// client). Skills come first (they are cheap and use the mastery level already paid for), then the lowest chosen
/// mastery. Only steps that can be learned now are tried: locked skills (missing mastery level, required skill or SP)
/// are skipped, so they never hold the others up. A step the server refuses waits 1, 2, 4 ... up to 30 minutes.
/// </summary>
public sealed class SkillTrainer
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BetweenSteps = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan RefusedPause = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefusedPauseMax = TimeSpan.FromMinutes(30);

    private readonly SroClient _client;
    private readonly ILogger _logger;
    private readonly Dictionary<string, DateTime> _refused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _refusals = new(StringComparer.Ordinal);
    private volatile AutoLevelOptions _options = AutoLevelOptions.Off;

    public SkillTrainer(SroClient client, ILogger logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>What to level; set from the UI thread, read by the loop.</summary>
    public AutoLevelOptions Options
    {
        get => _options;
        set
        {
            _options = value ?? AutoLevelOptions.Off;
            lock (_refused)
            {
                _refused.Clear(); // new choices: try everything again
                _refusals.Clear();
            }
        }
    }

    /// <summary>Raised after each step with the result (on the trainer's thread).</summary>
    public event EventHandler<SkillResult>? StepDone;

    /// <summary>What auto level does now, or why it waits (shown under the skill window).</summary>
    public string Status { get; private set; } = "Auto level is off.";

    /// <summary>Raised when <see cref="Status"/> changes (on the trainer's thread).</summary>
    public event Action<string>? StatusChanged;

    private void SetStatus(string text)
    {
        if (text == Status)
            return;
        Status = text;
        if (text.StartsWith("Waiting", StringComparison.Ordinal))
            _logger.LogInformation("Auto level: {Status}", text);
        StatusChanged?.Invoke(text);
    }

    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan wait = Idle;
            try
            {
                if (await TryStepAsync(cancellationToken).ConfigureAwait(false))
                    wait = BetweenSteps;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
            {
                _logger.LogDebug("Skill trainer: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Does one step if there is one. True when something was sent.</summary>
    public async Task<bool> TryStepAsync(CancellationToken cancellationToken = default)
    {
        var options = _options;
        if (!options.Enabled)
        {
            SetStatus("Auto level is off.");
            return false;
        }
        if (!_client.IsInGame)
        {
            SetStatus("Auto level: waiting for the character to enter the game.");
            return false;
        }
        if (SkillWindow.Take(_client) is not { } window)
            return false;
        if (window.NeedsRebuild)
        {
            SetStatus("Waiting: the game database has no skill window; rebuild it on the Server page.");
            return false;
        }

        DateTime now = DateTime.UtcNow;
        HashSet<string> skip;
        lock (_refused)
        {
            foreach (var old in _refused.Where(r => r.Value < now).Select(r => r.Key).ToArray())
                _refused.Remove(old);
            skip = [.. _refused.Keys];
        }
        if (NextStep(window, options, skip) is not { } step)
        {
            SetStatus(Explain(window, options));
            return false;
        }
        SetStatus($"Learning {step.Name} ({step.Sp:N0} SP).");

        var result = step.IsMastery
            ? await _client.RaiseMasteryAsync(step.Id, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await _client.LearnSkillAsync(step.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            _logger.LogInformation("Auto level: {Message} ({Sp:N0} SP)", result.Message, step.Sp);
            lock (_refused)
                _refusals.Remove(step.Key);
        }
        else
        {
            TimeSpan pause;
            lock (_refused)
            {
                int times = _refusals[step.Key] = _refusals.GetValueOrDefault(step.Key) + 1;
                pause = RefusedPauseFor(times);
                _refused[step.Key] = now + pause;
            }
            _logger.LogWarning("Auto level: {Name}: {Message}; skipped for {Minutes:0} min, the others go on", step.Name, result.Message, pause.TotalMinutes);
        }
        StepDone?.Invoke(this, result);
        return true;
    }

    /// <summary>
    /// Picks the next step: the cheapest learnable level of a chosen skill line, otherwise the lowest chosen mastery
    /// that can be raised (and stays <see cref="AutoLevelOptions.MasteryGap"/> below the character level). Both keep
    /// <see cref="AutoLevelOptions.KeepSp"/> skill points.
    /// </summary>
    public static TrainerStep? NextStep(SkillWindow window, AutoLevelOptions options, IReadOnlySet<string>? skip = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(options);
        long spendable = (long)window.Sp - options.KeepSp;
        var pages = window.Tabs.SelectMany(t => t.Masteries).ToArray();
        var masteryIds = AutoMasteries(pages, options);

        if (options.Skills)
        {
            var skill = ChosenSkills(pages, options, masteryIds)
                .Where(s => s is { State: SkillSlotState.Learnable or SkillSlotState.Upgradable }
                            && s.NextSp <= spendable && skip?.Contains("skill:" + s.Group) != true)
                .OrderBy(s => s.NextSp)
                .FirstOrDefault();
            if (skill is not null)
                return new TrainerStep(false, skill.NextId, $"{skill.Name} Lv. {skill.Level + 1}", skill.NextSp, "skill:" + skill.Group);
        }

        if (options.Masteries)
        {
            var mastery = pages
                .Where(p => masteryIds.Contains(p.Id) && p.RaiseMissing is null && p.RaiseSp <= spendable
                            && p.Level + options.MasteryGap < window.CharacterLevel
                            && skip?.Contains("mastery:" + p.Id) != true)
                .OrderBy(p => p.Level).ThenBy(p => p.Id)
                .FirstOrDefault();
            if (mastery is not null)
                return new TrainerStep(true, mastery.Id, $"{mastery.Name} Lv. {mastery.Level + 1}", mastery.RaiseSp, "mastery:" + mastery.Id);
        }
        return null;
    }

    /// <summary>How long a step waits after its <paramref name="times"/>-th refusal in a row: 1, 2, 4, 8, 16, then 30 minutes.</summary>
    public static TimeSpan RefusedPauseFor(int times) =>
        TimeSpan.FromMinutes(Math.Min(RefusedPauseMax.TotalMinutes, RefusedPause.TotalMinutes * Math.Pow(2, Math.Clamp(times - 1, 0, 10))));

    /// <summary>The auto masteries: the chosen ones, or every mastery the character has raised when none is chosen.</summary>
    private static HashSet<uint> AutoMasteries(MasteryPage[] pages, AutoLevelOptions options) =>
        options.MasteryIds.Count > 0 ? [.. options.MasteryIds] : [.. pages.Where(p => p.Level > 0).Select(p => p.Id)];

    /// <summary>
    /// The auto skill lines: the right-clicked ones plus every line of the masteries with "ALL SKILLS"; when nothing is
    /// chosen, the lines the character has already learned.
    /// </summary>
    private static IEnumerable<SkillSlot> ChosenSkills(MasteryPage[] pages, AutoLevelOptions options, HashSet<uint> masteryIds)
    {
        var allOf = options.AllSkillsOf;
        bool learnedOnly = options.SkillGroups.Count == 0 && allOf.Count == 0;
        return pages.SelectMany(p => p.Lines.SelectMany(l => l.Slots).OfType<SkillSlot>()
            .Where(s => options.SkillGroups.Contains(s.Group) || allOf.Contains(p.Id) || (learnedOnly && s.Level > 0)));
    }

    /// <summary>Why there is nothing to do now (pure: tested).</summary>
    public static string Explain(SkillWindow window, AutoLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(options);
        var pages = window.Tabs.SelectMany(t => t.Masteries).ToArray();
        var masteryIds = AutoMasteries(pages, options);
        long spendable = (long)window.Sp - options.KeepSp;
        var reasons = new List<string>();
        if (options.Masteries)
        {
            if (masteryIds.Count == 0)
                reasons.Add("no mastery chosen (click AUTO on a mastery)");
            else if (window.MasteryTotal >= window.MasteryLimit)
                reasons.Add($"mastery level total at the game's limit ({window.MasteryTotal}/{window.MasteryLimit}): no mastery can be raised "
                            + "until one is lowered in the game");
            foreach (var p in pages.Where(p => masteryIds.Contains(p.Id) && window.MasteryTotal < window.MasteryLimit))
            {
                string why = p.RaiseMissing is { } m ? m.TrimEnd('.')
                    : p.Level + options.MasteryGap >= window.CharacterLevel ? $"gap {options.MasteryGap} below Lv. {window.CharacterLevel}"
                    : p.RaiseSp > spendable ? $"needs {p.RaiseSp:N0} SP (keeping {options.KeepSp:N0})"
                    : "refused, trying again soon";
                reasons.Add($"{p.Name} Lv. {p.Level}: {why}");
            }
        }
        if (options.Skills)
        {
            var skills = ChosenSkills(pages, options, masteryIds).ToList();
            if (skills.Count == 0)
                reasons.Add("no skill chosen (right-click a skill, or ALL SKILLS on a mastery)");
            else
            {
                int max = skills.Count(s => s.State == SkillSlotState.Max);
                var locked = skills.Where(s => s.State is SkillSlotState.Locked or SkillSlotState.Learned).ToList();
                var open = skills.Where(s => s.State is SkillSlotState.Learnable or SkillSlotState.Upgradable).OrderBy(s => s.NextSp).FirstOrDefault();
                string text = $"{skills.Count} skill{(skills.Count == 1 ? "" : "s")} chosen, {max} at max";
                if (locked.Count > 0)
                    text += $", {locked.Count} locked (skipped)";
                if (open is not null)
                    text += $"; next {open.Name}: " + (open.NextSp > spendable ? $"needs {open.NextSp:N0} SP (keeping {options.KeepSp:N0})" : "refused, trying again later");
                else if (locked.OrderBy(s => s.NextSp).FirstOrDefault() is { } first)
                    text += $"; e.g. {first.Name}: {first.Missing}";
                reasons.Add(text);
            }
        }
        return "Waiting: " + (reasons.Count == 0 ? "nothing to learn." : string.Join(" · ", reasons));
    }
}
