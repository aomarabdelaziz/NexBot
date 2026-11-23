using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Configuration;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Sessions;

namespace NexBot.Automation;

public enum AlchemyJob { Plus, Attribute, Alchemic }

/// <summary>One line of the alchemy queue: an item, what to do with it and when to stop.</summary>
/// <param name="ItemId">The item's ref ID, to find it again if it moves.</param>
/// <param name="Stone">Attribute / alchemic: the stone's server name.</param>
/// <param name="StopAt">Plus: +N. Attribute: white percent. Alchemic: blue value.</param>
public sealed record AlchemyEntry(AlchemyJob Job, byte Slot, uint ItemId, string ItemName, int StopAt, string Stone = "")
{
    public override string ToString() => Job switch
    {
        AlchemyJob.Plus => $"Plus  {ItemName}  → +{StopAt}",
        AlchemyJob.Attribute => $"White  {ItemName}  ·  {Stone}  → {StopAt} %",
        _ => $"Blue  {ItemName}  ·  {Stone}  → {StopAt}",
    };
}

/// <summary>
/// Runs the alchemy queue (phBot's Alchemy tab): elixirs until +N with lucky powder and the four plus stones from set
/// pluses, attribute stones until a white percent, alchemic stones until a blue value. One request at a time, with
/// the success / failure delays between them.
/// </summary>
public sealed class AlchemyRunner
{
    private readonly SroClient _client;
    private readonly AlchemySettings _settings;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    public AlchemyRunner(SroClient client, AlchemySettings settings, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullLogger.Instance;
    }

    public List<AlchemyEntry> Queue { get; } = [];
    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Idle";
    public event EventHandler? StatusChanged;

    /// <summary>Tests use 0 to skip the delays.</summary>
    public double DelayScale { get; set; } = 1;

    private void SetStatus(string text)
    {
        Status = text;
        _logger.LogInformation("Alchemy: {Status}", text);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private int DegreeOf(Equipable item) =>
        _settings.DegreeOverride > 0 ? _settings.DegreeOverride : AlchemyCatalog.DegreeOf(item.ServerName);

    private (byte Slot, Item Item)? Find(Func<Item, bool> match)
    {
        var inventory = _client.World.Character?.Inventory;
        if (inventory is null)
            return null;
        for (int i = Character.FirstInventorySlot; i < inventory.Capacity; i++)
            if (inventory[i] is { } item && match(item))
                return ((byte)i, item);
        return null;
    }

    /// <summary>The queued item, where it is now (it may have moved).</summary>
    private (byte Slot, Equipable Item)? Locate(AlchemyEntry entry)
    {
        var inventory = _client.World.Character?.Inventory;
        if (inventory?[entry.Slot] is Equipable same && same.Id == entry.ItemId)
            return (entry.Slot, same);
        return Find(i => i is Equipable e && e.Id == entry.ItemId) is { } found ? (found.Slot, (Equipable)found.Item) : null;
    }

    /// <summary>Every item that can be plussed (from the inventory), with the current Stop at.</summary>
    public IEnumerable<AlchemyEntry> AllPlusItems()
    {
        var inventory = _client.World.Character?.Inventory;
        if (inventory is null)
            yield break;
        for (int i = Character.FirstInventorySlot; i < inventory.Capacity; i++)
            if (inventory[i] is Equipable e && AlchemyCatalog.CanAlchemy(e) && e.Plus < _settings.StopAt)
                yield return new AlchemyEntry(AlchemyJob.Plus, (byte)i, e.Id, e.Name, _settings.StopAt);
    }

    /// <summary>Stones in the inventory that fit the item (attribute or alchemic, by degree).</summary>
    public IReadOnlyList<string> StonesFor(Equipable item, AlchemyJob job)
    {
        int degree = DegreeOf(item);
        var inventory = _client.World.Character?.Inventory;
        if (inventory is null)
            return [];
        return [.. Enumerable.Range(Character.FirstInventorySlot, Math.Max(0, inventory.Capacity - Character.FirstInventorySlot))
            .Select(i => inventory[i])
            .OfType<Item>()
            .Where(s => (job == AlchemyJob.Attribute ? AlchemyCatalog.IsAttributeStone(s) : AlchemyCatalog.IsBlueStone(s))
                        && (AlchemyCatalog.MaterialDegree(s.ServerName) == degree || degree == 0))
            .Select(s => s.ServerName)
            .Distinct()];
    }

    public void Start(Action<string>? done = null)
    {
        if (IsRunning)
            return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            string result = await RunAsync(token).ConfigureAwait(false);
            done?.Invoke(result);
        });
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>Runs the queue; finished lines leave it. Returns why it stopped.</summary>
    public async Task<string> RunAsync(CancellationToken token)
    {
        IsRunning = true;
        try
        {
            while (Queue.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var entry = Queue[0];
                var outcome = entry.Job switch
                {
                    AlchemyJob.Plus => await PlusAsync(entry, token).ConfigureAwait(false),
                    _ => await StoneAsync(entry, token).ConfigureAwait(false),
                };
                if (outcome.StopAll)
                {
                    SetStatus(outcome.Message);
                    return outcome.Message;
                }
                Queue.Remove(entry);
                SetStatus(outcome.Message);
            }
            SetStatus("Queue finished.");
            return "Queue finished.";
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
            return "Cancelled.";
        }
        finally
        {
            IsRunning = false;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Task Wait(int ms, CancellationToken token) =>
        DelayScale <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, ms) * DelayScale), token);

    private sealed record Outcome(string Message, bool StopAll = false);

    private bool _warnedNoPowder;

    private async Task<Outcome> PlusAsync(AlchemyEntry entry, CancellationToken token)
    {
        _warnedNoPowder = false;
        int attempts = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Locate(entry) is not { } at)
                return new($"{entry.ItemName} is no longer in the inventory.");
            var (slot, item) = at;
            if (item.Plus >= entry.StopAt)
                return new($"{item.Name} reached +{item.Plus}.");
            if (_settings.StopAfterOn && attempts >= _settings.StopAfterAttempts)
                return new($"{item.Name}: stopped after {attempts} attempts at +{item.Plus}.");
            int degree = DegreeOf(item);

            // The plus stones first, each once, from their plus on.
            foreach (var (kind, on, from) in new[] { (PlusStone.Immortal, _settings.ImmortalOn, _settings.ImmortalAt),
                         (PlusStone.Steady, _settings.SteadyOn, _settings.SteadyAt), (PlusStone.Astral, _settings.AstralOn, _settings.AstralAt),
                         (PlusStone.Lucky, _settings.LuckyStoneOn, _settings.LuckyStoneAt) })
            {
                if (!on || item.Plus < from || AlchemyCatalog.HasStoneOption(item, kind))
                    continue;
                if (Find(s => AlchemyCatalog.IsPlusStone(s, kind, degree)) is not { } stone)
                    continue;
                SetStatus($"{item.Name}: using {stone.Item.Name}…");
                var used = await _client.FuseStoneAsync(slot, stone.Slot, null, _settings.MagicStoneType, token).ConfigureAwait(false);
                if (!used.Answered)
                    return new($"{item.Name}: {used.Message}", StopAll: true);
                await Wait(used.Success ? _settings.SuccessDelayMs : _settings.FailureDelayMs, token).ConfigureAwait(false);
                if (Locate(entry) is not { } again)
                    return new($"{entry.ItemName} is gone.", _settings.StopAllIfDestroyed);
                (slot, item) = again;
            }

            if (Find(e => AlchemyCatalog.IsElixirFor(e, item)) is not { } elixir)
                return new($"No elixir for {item.Name} left.", StopAll: true);
            (byte Slot, Item Item)? powder = null;
            if (_settings.LuckyPowderOn && item.Plus >= _settings.LuckyPowderAt)
            {
                int powderDegree = _settings.LuckyPowderDegree > 0 ? _settings.LuckyPowderDegree : degree;
                powder = Find(p => AlchemyCatalog.IsPowderFor(p, powderDegree));
                if (powder is null && !_warnedNoPowder)
                {
                    _warnedNoPowder = true;
                    _logger.LogWarning("Alchemy: no lucky powder of degree {Degree} in the inventory for {Item} (…_PROB_UP_A_{Degree:00}); plussing without it",
                        powderDegree, item.Name);
                }
            }
            byte before = item.Plus;
            SetStatus($"{item.Name} +{before}: elixir{(powder is null ? "" : " + lucky powder")}…");
            var result = await _client.FuseElixirAsync(slot, elixir.Slot, powder?.Slot, _settings.ElixirType, token).ConfigureAwait(false);
            if (!result.Answered)
                return new($"{item.Name}: {result.Message}", StopAll: true);
            attempts++;
            if (result.Destroyed)
                return new($"{item.Name} was destroyed at +{before}.", _settings.StopAllIfDestroyed);
            if (result.Success)
            {
                _logger.LogInformation("Alchemy: {Item} +{Before} → +{After}", item.Name, before, before + 1);
                await Wait(_settings.SuccessDelayMs, token).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Alchemy: {Item} failed at +{Before}", item.Name, before);
                await Wait(_settings.FailureDelayMs, token).ConfigureAwait(false);
                if (_settings.MoveOnAfterFailure)
                    return new($"{item.Name} failed at +{before}: moving on.");
            }
        }
    }

    private async Task<Outcome> StoneAsync(AlchemyEntry entry, CancellationToken token)
    {
        bool white = entry.Job == AlchemyJob.Attribute;
        int attempts = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Locate(entry) is not { } at)
                return new($"{entry.ItemName} is no longer in the inventory.");
            var (slot, item) = at;
            int? now = white ? AlchemyCatalog.WhitePercent(item, entry.Stone) : (int)AlchemyCatalog.BlueValue(item, entry.Stone);
            if (now is { } value && value >= entry.StopAt)
                return new($"{item.Name}: {entry.Stone} reached {value}{(white ? " %" : "")}.");
            if (_settings.StopAfterOn && attempts >= _settings.StopAfterAttempts)
                return new($"{item.Name}: stopped after {attempts} stones.");
            if (Find(s => string.Equals(s.ServerName, entry.Stone, StringComparison.Ordinal)) is not { } stone)
                return new($"No {entry.Stone} left.");
            var catalyst = _settings.UseCatalyst ? Find(AlchemyCatalog.IsCatalyst) : null;
            SetStatus($"{item.Name}: {stone.Item.Name}{(now is { } v ? $" (now {v}{(white ? " %" : "")})" : "")}…");
            var result = await _client.FuseStoneAsync(slot, stone.Slot, catalyst?.Slot,
                white ? _settings.AttributeStoneType : _settings.MagicStoneType, token).ConfigureAwait(false);
            if (!result.Answered)
                return new($"{item.Name}: {result.Message}", StopAll: true);
            attempts++;
            if (result.Destroyed)
                return new($"{item.Name} was destroyed.", _settings.StopAllIfDestroyed);
            await Wait(result.Success ? _settings.SuccessDelayMs : _settings.FailureDelayMs, token).ConfigureAwait(false);
            if (now is null && result.Success)
                return new($"{item.Name}: {entry.Stone} worked (its value cannot be read, so it stops here).");
        }
    }
}
