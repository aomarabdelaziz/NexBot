using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Configuration;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>One stall slot to fill: an inventory slot (or part of a stack to split off first) and its price.</summary>
/// <param name="SplitOff">When set, move this many from <paramref name="InventorySlot"/> into a free slot first and stall that.</param>
public sealed record StallEntry(byte InventorySlot, string ItemServerName, string ItemName, ushort Quantity, ulong Price, ushort? SplitOff = null);

/// <summary>
/// Stalling (phBot's Stall tab): works out what can be stalled from the inventory and the price list, splits stacks
/// into the Filter quantity, creates the stall, fills it and opens it. With Auto stall on, it opens the stall by
/// itself whenever enough items can be stalled, and fills it again when everything sold.
/// </summary>
public sealed class StallManager : IDisposable
{
    private readonly SroClient _client;
    private readonly StallSettings _settings;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private DateTime _nextTry = DateTime.MinValue;

    public StallManager(SroClient client, StallSettings settings, ILogger? logger = null, bool startLoop = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullLogger.Instance;
        if (settings.AddTrailerLearned)
        {
            try
            {
                client.StallAddTrailer = Convert.FromHexString(settings.AddTrailerHex);
            }
            catch (FormatException)
            {
                settings.AddTrailerLearned = false;
            }
        }
        if (startLoop)
            _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Pause between stall requests (the server drops requests sent too fast).</summary>
    public TimeSpan StepDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>After a refused auto stall (not in town, riding ...), wait this long before trying again.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Last thing done, for the page.</summary>
    public string Status { get; private set; } = "Idle";
    public event EventHandler? StatusChanged;

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// What would go into the stall now: priced inventory items not already stalled, stacks split into the Filter
    /// quantity, at most the free stall slots (and free inventory slots for the splits).
    /// </summary>
    public static IReadOnlyList<StallEntry> Plan(SroClient client, StallSettings settings)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(settings);
        if (client.World.Character is not { } c)
            return [];
        var inventory = c.Inventory;
        var stalled = client.Stall.Slots.Select(s => s.InventorySlot).ToHashSet();
        int room = MyStall.Capacity - client.Stall.Slots.Count;
        int freeSlots = Enumerable.Range(Character.FirstInventorySlot, Math.Max(0, inventory.Capacity - Character.FirstInventorySlot))
            .Count(i => inventory[i] is null);
        var plan = new List<StallEntry>();
        // First the stacks that fit a slot as they are (including pieces split off before), then the ones to split.
        for (int i = Character.FirstInventorySlot; i < inventory.Capacity && plan.Count < room; i++)
        {
            if (inventory[i] is not { } item || stalled.Contains((byte)i) || settings.PriceOf(item.ServerName) is not { } price)
                continue;
            ushort have = Math.Max((ushort)1, item.Quantity);
            if (price.Quantity <= 0 || price.Quantity >= have)
                plan.Add(new StallEntry((byte)i, item.ServerName, item.Name, have, price.Price));
        }
        for (int i = Character.FirstInventorySlot; i < inventory.Capacity && plan.Count < room; i++)
        {
            if (inventory[i] is not { } item || stalled.Contains((byte)i) || settings.PriceOf(item.ServerName) is not { } price)
                continue;
            ushort have = Math.Max((ushort)1, item.Quantity);
            int per = price.Quantity;
            if (per <= 0 || per >= have)
                continue;
            // Split the stack: whole parts go to free slots, the rest stays where it is.
            int left = have;
            while (left > per && plan.Count < room && freeSlots > 0)
            {
                plan.Add(new StallEntry((byte)i, item.ServerName, item.Name, (ushort)per, price.Price, (ushort)per));
                left -= per;
                freeSlots--;
            }
            if (left == per && plan.Count < room)
                plan.Add(new StallEntry((byte)i, item.ServerName, item.Name, (ushort)left, price.Price));
        }
        return plan;
    }

    /// <summary>Slots needed before the stall opens (Options: 0 = any).</summary>
    private int Needed => Math.Max(1, _settings.MinItems);

    /// <summary>Creates the stall when needed, adds what <see cref="Plan"/> finds, and opens it to buyers.</summary>
    public async Task<StallResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<StallResult> OpenCoreAsync(CancellationToken token)
    {
        var plan = Plan(_client, _settings);
        if (plan.Count == 0 && _client.Stall.Slots.Count == 0)
            return Done(new StallResult(false, "Nothing to stall: set prices for items in the inventory."));

        // Split the stacks first ("Per slot"): the game refuses moving items while a stall is up (error 0x1834).
        var ready = new List<(StallEntry Entry, byte From)>();
        int skippedSplits = 0;
        foreach (var entry in plan)
        {
            if (entry.SplitOff is not { } part)
            {
                ready.Add((entry, entry.InventorySlot));
                continue;
            }
            if (_client.Stall.Exists)
            {
                skippedSplits++;
                continue;
            }
            if (FreeInventorySlot() is not { } free)
                break;
            SetStatus($"Splitting {entry.ItemName} into {part}…");
            var split = await _client.MoveInventoryItemAsync(entry.InventorySlot, free, part, cancellationToken: token).ConfigureAwait(false);
            if (!split.Ok)
            {
                _logger.LogWarning("Stall: could not split {Item}: {Message}", entry.ItemName, split.Message);
                continue;
            }
            ready.Add((entry, free));
            await Task.Delay(StepDelay, token).ConfigureAwait(false);
        }
        if (skippedSplits > 0)
            _logger.LogInformation("Stall: {Count} split stack(s) not added: stacks cannot be split while the stall is up. Close the stall and open it again to add them.", skippedSplits);
        if (ready.Count == 0 && _client.Stall.Slots.Count == 0)
            return Done(new StallResult(false, "Could not split the stacks for the stall (\"Per slot\"); see the log. Set Per slot to 0 to sell whole stacks."));

        if (!_client.Stall.Exists)
        {
            SetStatus("Creating the stall…");
            string title = _settings.Title.Length > 0 ? _settings.Title : $"{_client.World.Character?.Name}'s stall";
            var created = await _client.CreateStallAsync(title, token).ConfigureAwait(false);
            if (!created.Ok)
                return Done(created);
            await Task.Delay(StepDelay, token).ConfigureAwait(false);
            if (_settings.Note.Length > 0)
            {
                await _client.SetStallNoteAsync(_settings.Note, token).ConfigureAwait(false);
                await Task.Delay(StepDelay, token).ConfigureAwait(false);
            }
        }
        else if (_client.Stall.IsOpen && ready.Count > 0)
        {
            // Items can only be added while the stall is not open to buyers.
            await _client.OpenStallAsync(false, token).ConfigureAwait(false);
            await Task.Delay(StepDelay, token).ConfigureAwait(false);
        }

        int added = 0;
        StallResult? firstFailure = null;
        foreach (var (entry, from) in ready)
        {
            if (_client.Stall.FreeSlot() is not { } stallSlot)
                break;
            SetStatus($"Adding {entry.ItemName} x{entry.Quantity}…");
            var result = await _client.AddStallItemAsync(stallSlot, from, entry.Quantity, entry.Price, token).ConfigureAwait(false);
            if (result.Ok)
            {
                added++;
            }
            else
            {
                _logger.LogWarning("Stall: could not add {Item}: {Message}", entry.ItemName, result.Message);
                firstFailure ??= result;
                if (added == 0)
                    break; // the server refuses adding at all: the other items would fail the same way
            }
            await Task.Delay(StepDelay, token).ConfigureAwait(false);
        }

        if (_client.Stall.Slots.Count == 0)
        {
            await _client.CloseStallAsync(token).ConfigureAwait(false);
            _nextTry = DateTime.UtcNow + RetryDelay;
            string why = firstFailure is { ErrorCode: not 0 } f ? $" (error 0x{f.ErrorCode:X4})" : "";
            return Done(new StallResult(false, $"The server refused to add items to the stall{why}. " +
                "Add one item to a stall in the game client once so the bot learns this server's layout, then try again.",
                firstFailure?.ErrorCode ?? 0));
        }
        var opened = await _client.OpenStallAsync(true, token).ConfigureAwait(false);
        if (!opened.Ok)
            return Done(opened);
        _logger.LogInformation("Stall open with {Count} items", _client.Stall.Slots.Count);
        string skipped = skippedSplits > 0 ? $" {skippedSplits} split stack(s) wait: close and open the stall to add them." : "";
        return Done(new StallResult(true, $"Stall open with {_client.Stall.Slots.Count} items ({added} added).{skipped}"));
    }

    private StallResult Done(StallResult result)
    {
        SetStatus(result.Message);
        return result;
    }

    private byte? FreeInventorySlot()
    {
        var inventory = _client.World.Character?.Inventory;
        if (inventory is null)
            return null;
        for (int i = Character.FirstInventorySlot; i < inventory.Capacity; i++)
            if (inventory[i] is null)
                return (byte)i;
        return null;
    }

    public async Task<StallResult> CloseAsync(CancellationToken cancellationToken = default)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _client.CloseStallAsync(cancellationToken).ConfigureAwait(false);
            _nextTry = DateTime.UtcNow + RetryDelay; // do not reopen right away after a manual close
            return Done(result.Ok ? new StallResult(true, "Stall closed.") : result);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// One check (the loop runs it every few seconds; tests call it directly). A stall that sold everything, also
    /// one opened by hand, is filled again with what is left ("Fill again when sold out") or closed when nothing is
    /// left. With Auto stall, a stall is opened when enough priced items are in the inventory.
    /// </summary>
    public async Task StepAsync(CancellationToken token)
    {
        if (!_client.IsInGame || _client.World.Character is not { } c)
            return;
        var stall = _client.Stall;
        if (stall.Exists && stall.IsOpen && stall.Slots.Count == 0)
        {
            await SoldOutAsync(token).ConfigureAwait(false);
            return;
        }
        if (!_settings.AutoStall || DateTime.UtcNow < _nextTry || stall.Exists)
            return;
        if (c.IsRiding || c.Hp == 0)
            return;
        if (Plan(_client, _settings).Count < Needed)
            return;
        var result = await OpenAsync(token).ConfigureAwait(false);
        if (!result.Ok)
        {
            _nextTry = DateTime.UtcNow + RetryDelay;
            _logger.LogInformation("Auto stall: {Message} Trying again in {Seconds} s.", result.Message, (int)RetryDelay.TotalSeconds);
        }
    }

    /// <summary>
    /// Everything sold: the stall is closed, then opened again with what is left when "Fill again when sold out" is on
    /// and enough priced items remain. Closing first lets stacks be split for it ("Per slot"), which the game refuses
    /// while a stall is up.
    /// </summary>
    private async Task SoldOutAsync(CancellationToken token)
    {
        bool refill = _settings.RefillWhenSoldOut && Plan(_client, _settings).Count >= Needed;
        await _busy.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_client.Stall.Exists || _client.Stall.Slots.Count > 0)
                return; // changed meanwhile
            var closed = await _client.CloseStallAsync(token).ConfigureAwait(false);
            if (!closed.Ok)
            {
                _logger.LogWarning("Stall: everything sold, but closing failed: {Message}", closed.Message);
                return;
            }
        }
        finally
        {
            _busy.Release();
        }
        if (!refill)
        {
            _logger.LogInformation("Stall: everything sold and nothing more to sell: stall closed");
            _nextTry = DateTime.UtcNow + RetryDelay;
            SetStatus("Everything sold: stall closed.");
            return;
        }
        _logger.LogInformation("Stall: everything sold: filling it again with what is left");
        SetStatus("Everything sold: filling the stall again…");
        await Task.Delay(StepDelay, token).ConfigureAwait(false);
        var result = await OpenAsync(token).ConfigureAwait(false);
        if (!result.Ok)
        {
            _nextTry = DateTime.UtcNow + RetryDelay;
            _logger.LogInformation("Stall: filling again failed: {Message}", result.Message);
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                await StepAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
            {
                _logger.LogWarning("Auto stall: {Message}", ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
