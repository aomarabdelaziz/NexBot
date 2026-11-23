using Microsoft.Extensions.Logging;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Network;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>Walking behaviour options.</summary>
public sealed class AutoWalkOptions
{
    /// <summary>Start at the script's MOVE nearest the character, if one is within this many units (else line 1).</summary>
    public double MaxStartDistance { get; set; } = 150;

    /// <summary>
    /// Start at the earliest script point within this many units of the nearest one (0: the nearest itself). For
    /// routes that end where they start (town scripts), so the walk does not begin at the last line.
    /// </summary>
    public double StartSlack { get; set; }

    /// <summary>A waypoint counts as reached within this distance.</summary>
    public double ArriveDistance { get; set; } = 3;

    /// <summary>Re-send the move if the character has not progressed for this long.</summary>
    public TimeSpan ResendInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Give up on a waypoint (and go to the next one) after this long.</summary>
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>Wait this long at every point (Training Area, Script tab: script walk delay).</summary>
    public TimeSpan PointDelay { get; set; }

    /// <summary>Not getting nearer for this long: walk back to the point before, once, then on (zero: off).</summary>
    public TimeSpan GoBackIfStuck { get; set; }

    /// <summary>Not getting nearer for this long: the walk stops as stuck (zero: off; the town loop may then return).</summary>
    public TimeSpan GiveUpIfStuck { get; set; }

    /// <summary>Start over at the end of the script (in addition to REPEAT lines).</summary>
    public bool Loop { get; set; } = true;

    /// <summary>How long a teleport / return scroll may take, loading screen included.</summary>
    public TimeSpan TeleportTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>How often the position is checked.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// When the next line is another MOVE, it is sent this long before reaching the current point (at the current
    /// speed), so the character turns towards it without stopping.
    /// </summary>
    public TimeSpan LookAhead { get; set; } = TimeSpan.FromMilliseconds(450);

    /// <summary>Preferred trade transport and job suit, used by RIDE,TRANSPORT.</summary>
    public TradeSettings Trade { get; set; } = new();

    /// <summary>Pause between two purchases / sales.</summary>
    public TimeSpan ShopPause { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Checked between steps (trade road rules: fight thieves, get on / off). True when it did something, so the
    /// walker sends its move again.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? Guard { get; set; }
}

/// <summary>
/// Walks a character along a <see cref="WalkScript"/> (POC-2's walker, plus TELEPORT and RETURN lines). The move is
/// re-sent only when the character stops making progress; the mount moves while riding. Through a loading screen
/// (teleport, return scroll) it waits until the character is back in game.
/// </summary>
public sealed class AutoWalker : IDisposable
{
    private readonly SroClient _client;
    private readonly WalkScript _script;
    private readonly AutoWalkOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;

    public AutoWalker(SroClient client, WalkScript script, AutoWalkOptions? options = null, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _script = script ?? throw new ArgumentNullException(nameof(script));
        _options = options ?? new AutoWalkOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public WalkScript Script => _script;

    public bool IsRunning { get; private set; }

    /// <summary>Index into <see cref="WalkScript.Commands"/> of the line being done, or -1.</summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>What it is doing now ("Walking to line 12", "Teleporting: Jangan → Donwhang" ...).</summary>
    public string Status { get; private set; } = "Stopped";

    /// <summary>Raised (on a background thread) when <see cref="Status"/> or <see cref="IsRunning"/> changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>A return scroll item (not the reverse return scroll).</summary>
    public static bool IsReturnScroll(string serverName) =>
        serverName.Contains("RETURN", StringComparison.OrdinalIgnoreCase)
        && serverName.Contains("SCROLL", StringComparison.OrdinalIgnoreCase)
        && !serverName.Contains("REVERSE", StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        CancellationToken token;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }
        _ = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    /// <summary>Walks the script once to the end (or until stopped). True when it finished.</summary>
    public Task<bool> WalkAsync(CancellationToken token) => RunAsync(token);

    public void Stop()
    {
        lock (_lock)
            _cts?.Cancel();
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> RunAsync(CancellationToken token)
    {
        try
        {
            if (_client.World.Character is null || !_client.IsInGame)
            {
                SetStatus("Not started: the character is not in game.");
                return false;
            }
            if (_script.Commands.Count == 0)
            {
                SetStatus("Not started: the script is empty.");
                return false;
            }
            IsRunning = true;
            if (await RunScriptAsync(token).ConfigureAwait(false))
            {
                SetStatus("Finished.");
                _logger.LogInformation("Auto walk finished");
                return true;
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped.");
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            _logger.LogError(ex, "Auto walk failed");
            SetStatus("Failed: " + ex.Message);
            return false;
        }
        finally
        {
            IsRunning = false;
            CurrentIndex = -1;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Walks the script. False when it stopped on a line that failed (the status says why).</summary>
    private async Task<bool> RunScriptAsync(CancellationToken token)
    {
        int index = _script.FindStartMove(Here(token), _options.MaxStartDistance, _options.StartSlack);
        if (index < 0)
        {
            index = 0;
            _logger.LogInformation("Auto walk: no script point near the character, starting at the first line");
        }
        _logger.LogInformation("Auto walk started at line {Line} of {Script}", _script.Commands[index].LineNumber, _script.Name);

        while (!token.IsCancellationRequested)
        {
            if (index >= _script.Commands.Count)
            {
                if (!_options.Loop)
                    return true;
                index = 0;
                _logger.LogInformation("Auto walk: end of script, starting over");
            }

            CurrentIndex = index;
            var command = _script.Commands[index];
            switch (command.Type)
            {
                case WalkCommandType.Move:
                    SetStatus($"Walking · line {command.LineNumber}");
                    // Keep going without a stop when the next line is a point too.
                    int next = index + 1 < _script.Commands.Count ? index + 1 : _options.Loop ? 0 : -1;
                    bool chain = next >= 0 && _script.Commands[next].Type is WalkCommandType.Move or WalkCommandType.Repeat;
                    var previous = _script.Commands.Take(index).LastOrDefault(m => m.Type == WalkCommandType.Move)?.Position;
                    await WalkToAsync(command.Position, $"line {command.LineNumber}", token, chain, previous).ConfigureAwait(false);
                    if (_options.PointDelay > TimeSpan.Zero)
                        await Task.Delay(_options.PointDelay, token).ConfigureAwait(false);
                    index++;
                    break;
                case WalkCommandType.Wait:
                    SetStatus($"Waiting {command.Delay.TotalSeconds:0.#} s · line {command.LineNumber}");
                    await Task.Delay(command.Delay, token).ConfigureAwait(false);
                    index++;
                    break;
                case WalkCommandType.Teleport:
                    SetStatus($"Teleporting{(command.Label.Length > 0 ? ": " + command.Label : "")} · line {command.LineNumber}");
                    if (!await TeleportAsync(command, token).ConfigureAwait(false))
                    {
                        SetStatus($"Stopped at line {command.LineNumber}: the teleport did not work.");
                        return false;
                    }
                    index++;
                    break;
                case WalkCommandType.Return:
                    SetStatus($"Using a return scroll · line {command.LineNumber}");
                    if (!await ReturnAsync(token).ConfigureAwait(false))
                    {
                        SetStatus($"Stopped at line {command.LineNumber}: no return scroll, or it did not work.");
                        return false;
                    }
                    index++;
                    break;
                case WalkCommandType.Repeat:
                    index = 0;
                    break;
                case WalkCommandType.Buy:
                    SetStatus($"Buying at {command.Npc} · line {command.LineNumber}");
                    if (await BuyAsync(command, token).ConfigureAwait(false) is { } buyError)
                    {
                        SetStatus($"Stopped at line {command.LineNumber}: {buyError}");
                        return false;
                    }
                    index++;
                    break;
                case WalkCommandType.Sell:
                    SetStatus($"Selling at {command.Npc} · line {command.LineNumber}");
                    if (await SellAsync(command, token).ConfigureAwait(false) is { } sellError)
                    {
                        SetStatus($"Stopped at line {command.LineNumber}: {sellError}");
                        return false;
                    }
                    index++;
                    break;
                case WalkCommandType.Ride:
                    SetStatus($"Getting on the mount · line {command.LineNumber}");
                    if (await RideAsync(command, token).ConfigureAwait(false) is { } rideError)
                    {
                        SetStatus($"Stopped at line {command.LineNumber}: {rideError}");
                        return false;
                    }
                    index++;
                    break;
                case WalkCommandType.Visit:
                    SetStatus($"At the {(command.Target.Length > 0 ? command.Target.ToLowerInvariant() : "NPC")} · line {command.LineNumber}");
                    if (_options.Guard is { } visit)
                    {
                        // The NPC may come into sight a moment after arriving: wait for one (at most 3 s); with one in
                        // sight and nothing to do with it, walk on.
                        for (int tries = 0; tries < 10 && !await visit(token).ConfigureAwait(false); tries++)
                        {
                            var at = Here(token);
                            if (_client.World.Npcs.Values.Any(n => !n.IsMob && n.Position.InDungeon == at.InDungeon && n.Position.DistanceTo(at) <= 30))
                                break;
                            await Task.Delay(300, token).ConfigureAwait(false);
                        }
                    }
                    index++;
                    break;
                case WalkCommandType.Dismount:
                    SetStatus($"Getting off the mount · line {command.LineNumber}");
                    var off = await _client.DismountAsync(token).ConfigureAwait(false);
                    if (!off.Ok)
                        _logger.LogWarning("Auto walk: {Message}", off.Message);
                    index++;
                    break;
            }
        }
        return false;
    }

    /// <summary>The character's position, estimated from the mount while riding (it moves at the mount's speed).</summary>
    private Position Here(CancellationToken token) =>
        (_client.World.Mover ?? throw new OperationCanceledException(token)).EstimatePosition();

    /// <summary>Waits while the character is not in game (loading screen), up to <paramref name="limit"/>.</summary>
    private async Task<bool> WaitInGameAsync(TimeSpan limit, CancellationToken token)
    {
        var until = DateTime.UtcNow + limit;
        while (!_client.IsInGame || _client.World.Character is null)
        {
            if (DateTime.UtcNow > until)
                return false;
            await Task.Delay(_options.PollInterval, token).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Sends <paramref name="action"/> and waits for the loading screen it causes to start and end (teleport, return
    /// scroll). The state change is caught by event, so a short loading screen is not missed. False if none started.
    /// </summary>
    private async Task<bool> DoAndWaitForLoadingAsync(Action action, TimeSpan startWithin, CancellationToken token)
    {
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(object? sender, ClientStateChangedEventArgs e)
        {
            if (e.Current != ClientState.InGame)
                left.TrySetResult();
        }
        _client.StateChanged += OnState;
        try
        {
            action();
            try
            {
                await left.Task.WaitAsync(startWithin, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        finally
        {
            _client.StateChanged -= OnState;
        }
        if (!await WaitInGameAsync(_options.TeleportTimeout, token).ConfigureAwait(false))
            throw new InvalidOperationException("The character did not come back in game after the loading screen.");
        await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); // let the spawns arrive
        return true;
    }

    private async Task<bool> TeleportAsync(WalkCommand command, CancellationToken token)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            // The teleporter (gate or teleport NPC) may spawn a moment after arriving: look for it for a few seconds.
            Entity? teleporter = null;
            for (int look = 0; look < 25 && teleporter is null; look++)
            {
                teleporter = Teleporters.Nearest(_client, command.Teleporter, Here(token), 150);
                if (teleporter is null)
                    await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
            }
            if (teleporter is null)
            {
                _logger.LogWarning("Auto walk: teleporter {Teleporter} is not near the character", command.Teleporter);
                return false;
            }

            if (teleporter.Position.DistanceTo(Here(token)) > 10)
                await WalkToAsync(teleporter.Position, teleporter.Name, token).ConfigureAwait(false);

            // Like the game client: select the gate / NPC first, then ask for the destination.
            _logger.LogInformation("Auto walk: teleport {Label}", command.Label.Length > 0 ? command.Label : command.Teleporter);
            _client.SelectEntity(teleporter.UniqueId);
            await Task.Delay(TimeSpan.FromMilliseconds(400), token).ConfigureAwait(false);
            var gate = teleporter;
            if (await DoAndWaitForLoadingAsync(() => _client.UseTeleport(gate.UniqueId, command.DestinationId), TimeSpan.FromSeconds(8), token)
                    .ConfigureAwait(false))
                return true;
            _logger.LogWarning("Auto walk: the teleport did not start (attempt {Attempt} of 3)", attempt);
        }
        return false;
    }

    /// <summary>The NPC with this server name near the character (waits a few seconds for it to spawn), or null.</summary>
    private async Task<Npc?> FindNpcAsync(string serverName, CancellationToken token)
    {
        for (int look = 0; look < 25; look++)
        {
            var here = Here(token);
            var npc = _client.World.Npcs.Values
                .Where(n => string.Equals(n.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Position.DistanceTo(here))
                .FirstOrDefault(n => n.Position.DistanceTo(here) < 150);
            if (npc is not null)
                return npc;
            await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Finds the shop NPC, walks up to it and opens it. Null when it is not there.</summary>
    private async Task<Npc?> OpenShopAsync(WalkCommand command, CancellationToken token)
    {
        if (await FindNpcAsync(command.Npc, token).ConfigureAwait(false) is not { } npc)
        {
            _logger.LogWarning("Auto walk: {Npc} is not near the character", command.Npc);
            return null;
        }
        if (npc.Position.DistanceTo(Here(token)) > 8)
            await WalkToAsync(npc.Position, npc.Name, token).ConfigureAwait(false);
        await _client.OpenNpcAsync(npc.UniqueId, command.Talk, token).ConfigureAwait(false);
        return npc;
    }

    /// <summary>BUY: the recorded purchases (or until refused). Null when done, else why it stopped.</summary>
    private async Task<string?> BuyAsync(WalkCommand command, CancellationToken token)
    {
        bool toTransport = command.Target == ShopTarget.Transport;
        if (toTransport && _client.Transport is null)
            return "no transport is summoned to carry the goods.";
        if (await OpenShopAsync(command, token).ConfigureAwait(false) is not { } npc)
            return $"{command.Npc} is not here.";
        int bought = 0;
        try
        {
            var mode = _options.Trade.BuyMode;
            if (toTransport && mode == TradeBuyMode.GoodsList && GoodsToBuy(npc.ServerName) is { Count: > 0 } goods)
            {
                foreach (var (slot, quantity, stack) in goods)
                {
                    for (int left = quantity; left > 0;)
                    {
                        ushort amount = (ushort)Math.Min(left, stack);
                        var result = await _client.BuyAsync(npc.UniqueId, true, slot.Tab, slot.Slot, amount, token).ConfigureAwait(false);
                        if (!result.Ok)
                        {
                            _logger.LogInformation("Auto walk: stopped buying {Item} at {Npc}: {Message}", slot.ItemServerName, npc.Name, result.Message);
                            left = 0;
                            continue;
                        }
                        bought++;
                        left -= amount;
                        await Task.Delay(_options.ShopPause, token).ConfigureAwait(false);
                    }
                }
                if (bought == 0)
                    return $"could not buy any goods at {npc.Name}.";
                _logger.LogInformation("Auto walk: bought {Count} lots from the goods list at {Npc}", bought, npc.Name);
                return null;
            }
            bool fill = command.Times == 0 || (toTransport && mode == TradeBuyMode.FillTransport);
            int times = fill ? 1000 : command.Times;
            for (int i = 0; i < times; i++)
            {
                var result = await _client.BuyAsync(npc.UniqueId, toTransport, command.Tab, command.Slot, command.Quantity, token).ConfigureAwait(false);
                if (!result.Ok)
                {
                    // "Until full" ends at the first refusal; otherwise keep what was bought and go on.
                    if (!fill)
                        _logger.LogWarning("Auto walk: purchase {N} of {Times} at {Npc} refused: {Message}", i + 1, times, command.Npc, result.Message);
                    if (bought == 0)
                        return $"could not buy at {npc.Name}: {result.Message}";
                    break;
                }
                bought++;
                await Task.Delay(_options.ShopPause, token).ConfigureAwait(false);
            }
        }
        finally
        {
            _client.CloseNpc(npc.UniqueId);
        }
        _logger.LogInformation("Auto walk: bought {Count} x {Quantity} at {Npc}", bought, command.Quantity, npc.Name);
        return null;
    }

    /// <summary>The enabled goods of the goods list this trader sells: shop slot, quantity, and how many per purchase.</summary>
    private List<(NexBot.Data.ShopSlotInfo Slot, int Quantity, int Stack)> GoodsToBuy(string npcServerName)
    {
        var shop = _client.Data.GetShopItems(npcServerName);
        var list = new List<(NexBot.Data.ShopSlotInfo, int, int)>();
        foreach (var good in _options.Trade.Goods.Where(g => g.Enabled && g.Quantity > 0))
        {
            if (shop.FirstOrDefault(s => string.Equals(s.ItemServerName, good.Item, StringComparison.OrdinalIgnoreCase)) is not { } slot)
                continue;
            int stack = Math.Max(1, (int)(_client.Data.GetItem(good.Item)?.Stack ?? 1));
            list.Add((slot, good.Quantity, stack));
        }
        return list;
    }

    /// <summary>SELL: all the transport's cargo, or an inventory item. Nothing to sell is only a warning.</summary>
    private async Task<string?> SellAsync(WalkCommand command, CancellationToken token)
    {
        bool cargo = command.Target == ShopTarget.Transport;
        if (cargo && _client.Transport is not { } transport)
        {
            _logger.LogWarning("Auto walk: SELL at {Npc}: no transport is summoned, nothing to sell", command.Npc);
            return null;
        }
        if (await OpenShopAsync(command, token).ConfigureAwait(false) is not { } npc)
            return $"{command.Npc} is not here.";
        int sold = 0;
        try
        {
            if (cargo)
            {
                var goods = _client.Transport!.Inventory;
                for (int slot = 0; slot < goods.Capacity; slot++)
                {
                    if (goods[slot] is not { } item)
                        continue;
                    var result = await _client.SellFromTransportAsync(npc.UniqueId, (byte)slot, item.Quantity, token).ConfigureAwait(false);
                    if (!result.Ok)
                    {
                        _logger.LogWarning("Auto walk: selling {Item} at {Npc} refused: {Message}", item.Name, npc.Name, result.Message);
                        continue;
                    }
                    sold++;
                    await Task.Delay(_options.ShopPause, token).ConfigureAwait(false);
                }
            }
            else
            {
                int left = command.Quantity == 0 ? int.MaxValue : command.Quantity;
                var bag = _client.World.Character?.Inventory;
                for (int slot = Character.FirstInventorySlot; bag is not null && slot < bag.Capacity && left > 0; slot++)
                {
                    if (bag[slot] is not { } item || !string.Equals(item.ServerName, command.Target, StringComparison.OrdinalIgnoreCase))
                        continue;
                    ushort amount = (ushort)Math.Min(item.QuantityMax > 1 ? item.Quantity : 1, left);
                    var result = await _client.SellFromInventoryAsync(npc.UniqueId, (byte)slot, amount, token).ConfigureAwait(false);
                    if (!result.Ok)
                    {
                        _logger.LogWarning("Auto walk: selling {Item} at {Npc} refused: {Message}", item.Name, npc.Name, result.Message);
                        break;
                    }
                    left -= amount;
                    sold++;
                    await Task.Delay(_options.ShopPause, token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _client.CloseNpc(npc.UniqueId);
        }
        _logger.LogInformation("Auto walk: sold {Count} stack(s) at {Npc}", sold, npc.Name);
        return null;
    }

    /// <summary>RIDE: the preferred trade transport (job suit first if set), a named scroll, or the mount already out.</summary>
    private async Task<string?> RideAsync(WalkCommand command, CancellationToken token)
    {
        var character = _client.World.Character ?? throw new OperationCanceledException(token);
        if (character.IsRiding)
            return null;
        int slot = -1;
        if (command.Target == ShopTarget.Transport)
        {
            var trade = _options.Trade;
            if (trade.WearJobSuit && character.Inventory[8] is null)
            {
                int suit = TradeCatalog.FindTraderSuit(character, trade.JobSuit);
                if (suit < 0)
                    _logger.LogWarning("Auto walk: no trader suit in the bag");
                else if (await _client.WearJobSuitAsync(suit, token).ConfigureAwait(false) is { Ok: false } worn)
                    _logger.LogWarning("Auto walk: could not wear the job suit: {Message}", worn.Message);
            }
            if (_client.Mount is null)
            {
                slot = TradeCatalog.FindTransportScroll(character, trade.Transport);
                if (slot < 0)
                    return "no trade transport scroll in the bag.";
            }
        }
        else if (command.Target.Length > 0 && _client.Mount is null)
        {
            slot = character.Inventory.FindIndex(i => i is not null && string.Equals(i.ServerName, command.Target, StringComparison.OrdinalIgnoreCase),
                Character.FirstInventorySlot);
            if (slot < 0)
                return $"no {command.Target} in the bag.";
        }
        var result = await _client.RideMountAsync(slot, token).ConfigureAwait(false);
        if (!result.Ok)
            return result.Message;
        await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false); // the mount's position arrives
        if (command.Target == ShopTarget.Transport && _options.Trade.MountMode == TradeMountMode.StayOff)
            await _client.DismountAsync(token).ConfigureAwait(false); // summoned; walk next to it
        return null;
    }

    private async Task<bool> ReturnAsync(CancellationToken token)
    {
        var character = _client.World.Character ?? throw new OperationCanceledException(token);
        int slot = -1;
        for (int i = Character.FirstInventorySlot; i < character.Inventory.Capacity; i++)
        {
            if (character.Inventory[i] is { } item && IsReturnScroll(item.ServerName))
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            _logger.LogWarning("Auto walk: no return scroll in the inventory");
            return false;
        }
        _logger.LogInformation("Auto walk: using a return scroll");
        return await DoAndWaitForLoadingAsync(() => _client.UseItem((byte)slot), TimeSpan.FromSeconds(30), token) // the scroll casts first
            .ConfigureAwait(false);
    }

    /// <summary>Walks to <paramref name="target"/>. Returns false if it could not be reached in time.</summary>
    /// <param name="chain">The next line is another point: count as arrived <see cref="AutoWalkOptions.LookAhead"/> early.</param>
    private async Task<bool> WalkToAsync(Position target, string description, CancellationToken token, bool chain = false, Position? previous = null)
    {
        var started = DateTime.UtcNow;
        var lastSend = DateTime.MinValue;
        double lastDistance = double.MaxValue, best = double.MaxValue;
        var progressAt = DateTime.UtcNow;
        bool wentBack = false;

        while (!token.IsCancellationRequested)
        {
            if (!_client.IsInGame && !await WaitInGameAsync(_options.TeleportTimeout, token).ConfigureAwait(false))
                throw new InvalidOperationException("The character left the game.");

            if (_options.Guard is { } guard && await guard(token).ConfigureAwait(false))
            {
                lastSend = DateTime.MinValue; // walk on from wherever the fight / mounting left us
                lastDistance = double.MaxValue;
                started = DateTime.UtcNow;
            }

            var character = _client.World.Character ?? throw new OperationCanceledException(token);
            var mover = _client.World.Mover ?? character;
            var here = mover.EstimatePosition();
            if (here.InDungeon != target.InDungeon)
            {
                _logger.LogWarning("Auto walk: {Target} is in another area (dungeon / field). Skipping it.", description);
                return false;
            }
            double distance = here.DistanceTo(target);
            double arrive = chain
                ? Math.Clamp(mover.UnitsPerMillisecond * _options.LookAhead.TotalMilliseconds, _options.ArriveDistance, 12)
                : _options.ArriveDistance;
            if (distance <= arrive)
                return true;

            var now = DateTime.UtcNow;
            if (distance < best - 0.5)
            {
                best = distance;
                progressAt = now;
            }
            if (_options.GiveUpIfStuck > TimeSpan.Zero && now - progressAt > _options.GiveUpIfStuck)
                throw new InvalidOperationException($"stuck for {_options.GiveUpIfStuck.TotalSeconds:0} seconds on the way to {description}");
            if (_options.GoBackIfStuck > TimeSpan.Zero && !wentBack && previous is { } back && now - progressAt > _options.GoBackIfStuck)
            {
                wentBack = true;
                _logger.LogInformation("Auto walk: stuck on the way to {Target}: going back a point", description);
                uint mount0 = character.IsRiding ? character.RidingUniqueId : 0;
                _client.Send(ClientPackets.MoveTo(back, mount0));
                for (int i = 0; i < 80 && mover.EstimatePosition().DistanceTo(back) > 3; i++)
                    await Task.Delay(100, token).ConfigureAwait(false);
                (lastSend, lastDistance, best, progressAt) = (DateTime.MinValue, double.MaxValue, double.MaxValue, DateTime.UtcNow);
                continue;
            }
            if (now - started > _options.StepTimeout)
            {
                _logger.LogWarning("Auto walk: could not reach {Target}, {Distance:0} units away. Skipping it.", description, distance);
                return false;
            }

            // Send the first time, and again only if we are not getting closer.
            bool stalled = distance >= lastDistance - 0.5;
            if (lastSend == DateTime.MinValue || (stalled && now - lastSend >= _options.ResendInterval))
            {
                uint mount = character.IsRiding ? character.RidingUniqueId : 0;
                _client.Send(ClientPackets.MoveTo(target, mount));
                lastSend = now;
            }
            lastDistance = distance;

            await Task.Delay(_options.PollInterval, token).ConfigureAwait(false);
        }
        return false;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
