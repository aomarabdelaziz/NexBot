using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Navigation;
using NexBot.Network;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// phBot's town loop. Walks the town script that starts near the character and, on the way, stops at every shop NPC
/// it passes that has business: sell (the sell list and excess potions), repair (blacksmith / armor shop) and buy
/// up to the buy list's amounts. Then walks the training script, if one is set.
/// Without a town script near the character (and <see cref="TownSettings.AutoRoute"/> on), it finds its own way to
/// each shop NPC with business over the client's map data (<see cref="PathFinder"/>), and to the training script.
/// </summary>
public sealed class TownRunner : IDisposable
{
    /// <summary>How close an NPC must be to the route to be visited.</summary>
    public const double VisitRange = 30;
    /// <summary>How far out of sight NPCs are looked for (world units: a town).</summary>
    public const double TownRange = 350;
    /// <summary>Near enough to an NPC to talk to it (it often stands behind a counter that cannot be passed).</summary>
    public const double TalkRange = 10;

    private readonly SroClient _client;
    private readonly TownSettings _settings;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly HashSet<uint> _visited = [];
    private CancellationTokenSource? _cts;

    private readonly Func<NavMap?> _navigation;

    /// <param name="navigation">The map data for routes of its own (null: none, a town script is needed).</param>
    public TownRunner(SroClient client, TownSettings settings, ILogger? logger = null, Func<NavMap?>? navigation = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullLogger.Instance;
        _navigation = navigation ?? (() => null);
    }

    /// <summary>The pick filter: what to store, sell and drop (with the name lists of the Town page), and the gold to store.</summary>
    public PickFilterSettings Filter { get; set; } = new();

    /// <summary>The enabled training area: walked to on the map's routes after town when there is no training script.</summary>
    public TrainingAreaSettings? Training { get; set; }

    /// <summary>The Training Area page's Script tab (skip the town script, walk delay, stuck ...).</summary>
    public TrainingSetupSettings? Setup { get; set; }

    /// <summary>
    /// Called when town is done, before walking to training: true when it used a reverse return scroll instead (the
    /// loop then waits for the teleport and ends).
    /// </summary>
    public Func<bool>? ReverseReturn { get; set; }

    /// <summary>The walk to training could not go on (Script tab: return when the script cannot continue).</summary>
    public event Action<string>? ReturnRequested;

    /// <summary>
    /// Walks to the training area on the map's routes (in pieces: a route covers at most a few hundred units).
    /// False when there is no map data, no area, or the character is in a dungeon.
    /// </summary>
    public bool WalkToArea(TrainingAreaSettings? to = null)
    {
        if (IsRunning)
            return true;
        if ((to ?? Training) is not { } area || !area.IsSet || _client.World.Character is not { } c || c.Position.InDungeon || _navigation() is not { } nav)
            return false;
        CancellationToken token;
        lock (_lock)
        {
            if (IsRunning)
                return true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            IsRunning = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await WalkFarAsync(nav, area, token).ConfigureAwait(false))
                    SetStatus("Could not walk to the training area.");
                else
                    SetStatus("At the training area.");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Stopped.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
            {
                SetStatus("Failed: " + ex.Message);
            }
            finally
            {
                IsRunning = false;
                Route = [];
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }, token);
        return true;
    }

    /// <summary>How far one route of a long walk reaches (the path finder searches a window of about 1200 units).</summary>
    private const double LongWalkLeg = 700;

    /// <summary>Walks to the middle of the area in legs of <see cref="LongWalkLeg"/>; false when it stops getting nearer.</summary>
    private async Task<bool> WalkFarAsync(NavMap nav, TrainingAreaSettings area, CancellationToken token)
    {
        var (gx, gy) = area.Center;
        var goal = new Position(gx, gy);
        int noProgress = 0;
        double last = double.MaxValue;
        for (int leg = 0; leg < 60; leg++)
        {
            if (_client.World.Character is not { } c)
                return false;
            var here = c.EstimatePosition();
            double distance = here.DistanceTo(goal);
            if (distance <= Math.Max(10, area.Radius / 2.0))
                return true;
            var to = goal;
            if (distance > LongWalkLeg + 100)
            {
                double f = LongWalkLeg / distance;
                to = new Position(here.PosX + (gx - here.PosX) * f, here.PosY + (gy - here.PosY) * f);
            }
            SetStatus($"Walking to the training area ({distance:0} away)");
            var walked = await WalkRouteAsync(nav, to, "the training area", null, token, closeEnough: to == goal ? 8 : 40).ConfigureAwait(false);
            if (walked == RouteResult.Stopped && token.IsCancellationRequested)
                return false;
            double now = (_client.World.Character?.EstimatePosition() ?? here).DistanceTo(goal);
            noProgress = now > last - 20 ? noProgress + 1 : 0;
            last = Math.Min(last, now);
            if (noProgress >= 3)
            {
                _logger.LogWarning("Town: no way to the training area found on the map ({Distance:0} away)", now);
                return false;
            }
        }
        return false;
    }

    /// <summary>The route being walked by <see cref="TownSettings.AutoRoute"/> (empty when none), for the map.</summary>
    public IReadOnlyList<Position> Route { get; private set; } = [];

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Idle.";
    public event EventHandler? StatusChanged;
    /// <summary>The bag was full while buying and <see cref="TownSettings.StopIfInventoryFull"/> is on.</summary>
    public event Action<string>? StopRequested;

    private void SetStatus(string status)
    {
        Status = status;
        _logger.LogInformation("Town: {Status}", status);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts the loop in the background (after a return: waits for the teleport first).</summary>
    public void Start(bool afterTeleport = false)
    {
        CancellationToken token;
        lock (_lock)
        {
            if (IsRunning)
                return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            IsRunning = true;
        }
        _ = Task.Run(() => RunAsync(afterTeleport, token));
    }

    public void Stop()
    {
        lock (_lock)
            _cts?.Cancel();
    }

    private async Task RunAsync(bool afterTeleport, CancellationToken token)
    {
        try
        {
            if (afterTeleport)
                await WaitForTeleportAsync(token).ConfigureAwait(false);
            _visited.Clear();
            await DropAsync(token).ConfigureAwait(false);
            if (_settings.Store.Any(n => n.Trim().Length > 0) && _client.World.Character is { } bag && StorePlan(bag, _settings, Filter).Count == 0)
                _logger.LogInformation("Town: nothing in the bag matches the storage list ({List}): the Storage-keeper is not visited",
                    string.Join(", ", _settings.Store.Where(n => n.Trim().Length > 0)));
            var here = _client.World.Character?.Position ?? throw new InvalidOperationException("The character is not in game.");
            bool skipTown = Setup?.SkipTownScript == true;
            var town = skipTown ? null : PickScript(here);
            var nav = !skipTown && town is null && _settings.AutoRoute && !here.InDungeon ? _navigation() : null;
            if (!skipTown && town is null && nav is null)
            {
                SetStatus(_settings.AutoRoute && !here.InDungeon
                    ? "No town script starts near the character, and the map data is not available (check the client path in the settings)."
                    : "No town script starts near the character: record one on the Script page and add it here.");
                return;
            }
            if (town is not null)
            {
                SetStatus($"Walking {town.Name}");
                var walker = new AutoWalker(_client, town, new AutoWalkOptions { Loop = false, MaxStartDistance = 300, StartSlack = 30, Guard = VisitAsync }, _logger);
                bool done = await walker.WalkAsync(token).ConfigureAwait(false);
                // The end of the script may stand at an NPC too.
                await VisitAsync(token).ConfigureAwait(false);
                if (!done)
                {
                    SetStatus($"Stopped in town: {walker.Status}");
                    return;
                }
            }
            else if (!skipTown && !await AutoTownAsync(nav!, token).ConfigureAwait(false))
            {
                return;
            }
            if (ReverseReturn?.Invoke() == true)
            {
                SetStatus("Reverse return scroll used: waiting for the teleport");
                await WaitForTeleportAsync(token).ConfigureAwait(false);
                SetStatus("Town done.");
                return;
            }
            if (Load(_settings.TrainingScript) is { } training)
            {
                nav ??= _settings.AutoRoute ? _navigation() : null;
                if (nav is not null && !await WalkToScriptAsync(nav, training, token).ConfigureAwait(false))
                    return;
                SetStatus($"Walking to training: {training.Name}");
                var options = new AutoWalkOptions { Loop = _settings.LoopTrainingScript, MaxStartDistance = 300 };
                if (Setup is { } setup)
                {
                    options.PointDelay = TimeSpan.FromMilliseconds(Math.Max(0, setup.ScriptWalkDelay));
                    options.GoBackIfStuck = TimeSpan.FromSeconds(Math.Max(0, setup.GoBackIfStuck));
                    options.GiveUpIfStuck = TimeSpan.FromSeconds(Math.Max(0, setup.ReturnIfStuck));
                }
                var toTraining = new AutoWalker(_client, training, options, _logger);
                bool walkedIt;
                try
                {
                    walkedIt = await toTraining.WalkAsync(token).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    SetStatus($"Training script stopped: {ex.Message}");
                    if (Setup?.ReturnWhenCannotContinue == true || Setup?.ReturnIfStuck > 0)
                        ReturnRequested?.Invoke("the training script cannot continue: " + ex.Message);
                    return;
                }
                if (!walkedIt)
                {
                    SetStatus($"Training script stopped: {toTraining.Status}");
                    if (Setup?.ReturnWhenCannotContinue == true && !token.IsCancellationRequested)
                        ReturnRequested?.Invoke("the training script cannot continue");
                    return;
                }
            }
            else if (Training is { IsSet: true } area && !(_client.World.Character?.Position.InDungeon ?? true)
                && (nav ?? _navigation()) is { } map)
            {
                // No training script: walk to the training area on the map's routes.
                if (!await WalkFarAsync(map, area, token).ConfigureAwait(false))
                {
                    SetStatus("Could not walk to the training area on the map's routes: record a training script.");
                    if (Setup?.ReturnWhenCannotContinue == true && !token.IsCancellationRequested)
                        ReturnRequested?.Invoke("no way to the training area");
                    return;
                }
            }
            SetStatus("Town done.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or NullReferenceException)
        {
            SetStatus("Failed: " + ex.Message);
        }
        finally
        {
            IsRunning = false;
            Route = [];
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Town without a script: walks its own route to the nearest shop NPC with business, does it, and again until no
    /// NPC in sight has business. False when stopped on the way.
    /// </summary>
    private async Task<bool> AutoTownAsync(NavMap nav, CancellationToken token)
    {
        // Server names of the NPCs done (visited, or no way / no business there): one of each per town.
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool first = true;
        while (_client.World.Character is { } c)
        {
            foreach (var n in _client.World.Npcs.Values)
                if (_visited.Contains(n.UniqueId))
                    done.Add(n.ServerName);
            var here = c.EstimatePosition();
            // The shop NPCs in sight, and those of the town out of sight (their place from the client's npcpos.txt).
            var targets = new Dictionary<string, (string Name, Position Position)>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in _client.World.Npcs.Values)
                if (!done.Contains(n.ServerName) && !n.Position.InDungeon && HasBusiness(c, n.ServerName))
                    targets.TryAdd(n.ServerName, (n.Name, n.Position));
            foreach (var spot in nav.NpcsNear(here, TownRange))
                if (_client.Data.GetModel(spot.ModelId) is { } model && !done.Contains(model.ServerName)
                    && !targets.ContainsKey(model.ServerName) && HasBusiness(c, model.ServerName))
                    targets[model.ServerName] = (model.Name, spot.Position);
            if (targets.Count == 0)
            {
                if (first)
                    ExplainNothingToDo(c);
                return true;
            }
            first = false;
            var (server, (name, position)) = targets.MinBy(t => t.Value.Position.DistanceTo(here));
            bool Visited() => _client.World.Npcs.Values.Any(n => _visited.Contains(n.UniqueId) && string.Equals(n.ServerName, server, StringComparison.OrdinalIgnoreCase));
            if (position.DistanceTo(here) > VisitRange)
            {
                var walked = await WalkRouteAsync(nav, position, name, VisitAsync, token, TalkRange, Visited).ConfigureAwait(false);
                if (walked == RouteResult.NoWay)
                {
                    _logger.LogWarning("Town: no way to {Npc} found on the map: skipping it", name);
                    done.Add(server);
                    continue;
                }
                if (walked == RouteResult.Stopped)
                {
                    SetStatus($"Stopped on the way to {name}.");
                    return false;
                }
            }
            if (Visited())
                continue;
            await WaitForNpcAsync(server, token).ConfigureAwait(false);
            if (!await VisitAsync(token).ConfigureAwait(false) && !Visited())
            {
                _logger.LogInformation("Town: nothing could be done at {Npc}: skipping it", name);
                done.Add(server);
            }
        }
        return false;
    }

    /// <summary>Logs why no NPC in sight has business: nothing needed, or no seller of what is needed in sight.</summary>
    private void ExplainNothingToDo(Character c)
    {
        var npcs = _client.World.Npcs.Values.ToList();
        var shops = npcs.Where(n => _client.Data.GetShopItems(n.ServerName).Count > 0).ToList();
        _logger.LogInformation("Town: {Npcs} NPCs in sight, {Shops} of them shops: {Names}", npcs.Count, shops.Count,
            shops.Count == 0 ? "none" : string.Join(", ", shops.Select(n => n.Name)));
        var needed = _settings.Buy.Where(b => b.Enabled && b.Quantity > CountOf(c, b.Item)).ToList();
        if (needed.Count == 0)
            _logger.LogInformation("Town: nothing to buy: every ticked item on the buy list is at its Keep amount (or Keep is 0)");
        foreach (var want in needed)
        {
            string name = _client.Data.GetItem(want.Item)?.Name ?? want.Item;
            if (!shops.Any(n => _client.Data.GetShopItems(n.ServerName).Any(x => string.Equals(x.ItemServerName, want.Item, StringComparison.OrdinalIgnoreCase))))
                _logger.LogInformation("Town: need {Count} x {Item}, but no NPC in sight sells it", want.Quantity - CountOf(c, want.Item), name);
        }
        SetStatus(shops.Count == 0 ? "No shop NPC in sight: move closer to the shops, then Run now." : "Nothing to do with the NPCs in sight (details in the log).");
    }

    /// <summary>Walks its own route to a script's first point when the script starts farther than a walker accepts.</summary>
    private async Task<bool> WalkToScriptAsync(NavMap nav, WalkScript script, CancellationToken token)
    {
        if (_client.World.Character is not { } c)
            return false;
        var here = c.EstimatePosition();
        if (script.Commands.FirstOrDefault(m => m.Type == WalkCommandType.Move) is not { } first
            || first.Position.InDungeon || here.InDungeon || script.FindNearestMove(here, 30) >= 0)
            return true; // nothing to walk, or the walker starts from here
        var walked = await WalkRouteAsync(nav, first.Position, "the start of " + script.Name, null, token).ConfigureAwait(false);
        if (walked == RouteResult.NoWay)
            _logger.LogWarning("Town: no way to the start of {Script} found on the map", script.Name);
        if (walked != RouteResult.Stopped)
            return true; // there, or let the walker try from here
        SetStatus($"Stopped on the way to {script.Name}.");
        return false;
    }

    private enum RouteResult { Arrived, NoWay, Stopped }

    /// <summary>How many times a route is found again after getting stuck, per goal.</summary>
    private const int MaxReroutes = 4;
    /// <summary>No progress for this long on a leg counts as stuck.</summary>
    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(4);
    /// <summary>
    /// A turn of the route counts as reached this near (world units) unless the way on is clear from further back:
    /// turning early at a building's corner walks into it.
    /// </summary>
    private const double CornerArrive = 1;

    /// <summary>
    /// Walks to <paramref name="goal"/> on routes of its own. When the character gets stuck on the way (the map data
    /// says it can walk where the game does not let it), the ground just ahead is marked blocked and a new route is
    /// found from where it stands, up to <see cref="MaxReroutes"/> times.
    /// </summary>
    /// <param name="closeEnough">Arrived within this many world units of the goal.</param>
    /// <param name="reached">Arrived as soon as this is true (the NPC was visited on the way).</param>
    private async Task<RouteResult> WalkRouteAsync(NavMap nav, Position goal, string name, Func<CancellationToken, Task<bool>>? guard,
        CancellationToken token, double closeEnough = 2, Func<bool>? reached = null)
    {
        try
        {
            for (int attempt = 0; attempt <= MaxReroutes; attempt++)
            {
                if (_client.World.Character is not { } c)
                    return RouteResult.Stopped;
                var here = c.EstimatePosition();
                if (reached?.Invoke() == true || here.DistanceTo(goal) <= closeEnough)
                    return RouteResult.Arrived;
                var route = await Task.Run(() => PathFinder.Find(nav, here, goal), token).ConfigureAwait(false);
                if (route is null)
                    return attempt == 0 ? RouteResult.NoWay : RouteResult.Stopped;
                if (route.Count == 0)
                    return RouteResult.Arrived;
                Route = route;
                StatusChanged?.Invoke(this, EventArgs.Empty);
                SetStatus(attempt == 0 ? $"Walking to {name} (own route, {route.Count} legs)" : $"Walking to {name} (another way, try {attempt})");
                var (result, stuckAt, towards) = await WalkLegsAsync(nav, route, guard, token, goal, closeEnough, reached).ConfigureAwait(false);
                if (result != LegResult.Stuck)
                    return result == LegResult.Arrived ? RouteResult.Arrived : RouteResult.Stopped;
                if (reached is not null && stuckAt.DistanceTo(goal) <= VisitRange)
                {
                    _logger.LogInformation("Town: cannot get nearer to {Name} (a counter?): talking from here", name);
                    return RouteResult.Arrived;
                }
                int marked = nav.MarkBlocked(stuckAt, towards);
                // Step off what holds it (back or aside), then find the way again from there.
                bool stepped = await SidestepAsync(nav, stuckAt, towards, token).ConfigureAwait(false);
                if (marked == 0 && !stepped)
                {
                    _logger.LogWarning("Town: stuck on the way to {Name} at {X:0},{Y:0} and cannot step off it: giving up on it", name, stuckAt.PosX, stuckAt.PosY);
                    return RouteResult.NoWay;
                }
                _logger.LogInformation("Town: stuck on the way to {Name} at {X:0},{Y:0}: marked {Cells} cells ahead as blocked{Stepped}, finding another way",
                    name, stuckAt.PosX, stuckAt.PosY, marked, stepped ? ", stepped off" : "");
            }
            _logger.LogWarning("Town: still stuck on the way to {Name} after {Tries} other ways: giving up on it", name, MaxReroutes);
            return RouteResult.NoWay;
        }
        finally
        {
            Route = [];
        }
    }

    private enum LegResult { Arrived, Stuck, Stopped }

    /// <summary>How far a step off an obstacle goes (world units).</summary>
    private const double SidestepLength = 3;

    /// <summary>
    /// Steps the character off whatever stopped it: back, back-left, back-right, left, right of the way it was going
    /// (open ground on the map first). True as soon as one step really moved it.
    /// </summary>
    private async Task<bool> SidestepAsync(IWalkMap nav, Position at, Position towards, CancellationToken token)
    {
        double dx = towards.PosX - at.PosX, dy = towards.PosY - at.PosY, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 0.01)
            (dx, dy, d) = (1, 0, 1);
        dx /= d;
        dy /= d;
        var steps = new[] { 180, 135, -135, 90, -90 }
            .Select(deg =>
            {
                double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
                return new Position(at.PosX + (dx * c - dy * s) * SidestepLength, at.PosY + (dx * s + dy * c) * SidestepLength);
            })
            .OrderBy(p => PathFinder.IsOpen(nav, p) ? 0 : 1)
            .ToList();
        foreach (var step in steps)
        {
            if (!_client.IsInGame || _client.World.Character is not { } c)
                return false;
            _client.Send(ClientPackets.MoveTo(step, c.IsRiding ? c.RidingUniqueId : 0));
            var until = DateTime.UtcNow.AddSeconds(1.5);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                var mover = _client.World.Mover ?? c;
                if (mover.EstimatePosition().DistanceTo(at) >= SidestepLength * 0.6)
                {
                    await Task.Delay(300, token).ConfigureAwait(false); // let the step finish
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Walks the legs of a route in order. A leg that brings the character no nearer for <see cref="StuckAfter"/>
    /// (one more move sent half way) ends the walk as stuck, with where it stands and where it was going.
    /// </summary>
    private async Task<(LegResult, Position At, Position Towards)> WalkLegsAsync(IWalkMap nav, List<Position> route, Func<CancellationToken, Task<bool>>? guard,
        CancellationToken token, Position goal, double closeEnough, Func<bool>? reached)
    {
        for (int i = 0; i < route.Count; i++)
        {
            var target = route[i];
            bool last = i == route.Count - 1;
            double best = double.MaxValue;
            var bestAt = DateTime.UtcNow;
            var lastSend = DateTime.MinValue;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!_client.IsInGame || _client.World.Character is not { } c)
                    return (LegResult.Stopped, default, default);
                if (guard is not null && await guard(token).ConfigureAwait(false))
                {
                    // A shop on the way was visited: walk on from there.
                    best = double.MaxValue;
                    lastSend = DateTime.MinValue;
                }
                var mover = _client.World.Mover ?? c;
                var here = mover.EstimatePosition();
                if (reached?.Invoke() == true || here.DistanceTo(goal) <= closeEnough)
                    return (LegResult.Arrived, default, default);
                double distance = here.DistanceTo(target);
                // Turn towards the next point early (smoother) only when the straight way there from here is clear on
                // the map; otherwise walk right up to this turn first, so the corner is not cut.
                if (last ? distance <= 2
                    : distance <= CornerArrive
                      || distance <= Math.Clamp(mover.UnitsPerMillisecond * 400, 2, 8) && PathFinder.IsClear(nav, here, route[i + 1]))
                    break;
                var now = DateTime.UtcNow;
                if (distance < best - 0.5)
                {
                    best = distance;
                    bestAt = now;
                }
                else if (now - bestAt > StuckAfter)
                {
                    return (LegResult.Stuck, here, target);
                }
                bool resend = lastSend == DateTime.MinValue || (now - bestAt > StuckAfter / 2 && now - lastSend > StuckAfter / 2);
                if (resend)
                {
                    _client.Send(ClientPackets.MoveTo(target, c.IsRiding ? c.RidingUniqueId : 0));
                    lastSend = now;
                }
                await Task.Delay(150, token).ConfigureAwait(false);
            }
        }
        return (LegResult.Arrived, default, default);
    }

    /// <summary>Waits until the return scroll (or revive) has moved the character: a new spawn, at most 90 s.</summary>
    private async Task WaitForTeleportAsync(CancellationToken token)
    {
        var spawned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSpawned(object? s, SpawnedEventArgs e) => spawned.TrySetResult();
        _client.Spawned += OnSpawned;
        try
        {
            SetStatus("Waiting for the return to town…");
            await Task.WhenAny(spawned.Task, Task.Delay(TimeSpan.FromSeconds(90), token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); // let the town spawn in
        }
        finally
        {
            _client.Spawned -= OnSpawned;
        }
    }

    private WalkScript? Load(string path)
    {
        if (path.Length == 0 || !File.Exists(path))
            return null;
        try
        {
            return WalkScript.Load(path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Town: could not read {Path}: {Message}", path, ex.Message);
            return null;
        }
    }

    /// <summary>The town script with a point nearest the character (within 300 units).</summary>
    private WalkScript? PickScript(Position here)
    {
        WalkScript? best = null;
        double bestDistance = double.MaxValue;
        foreach (string path in _settings.Scripts)
        {
            if (Load(path) is not { } script)
                continue;
            foreach (var c in script.Commands)
            {
                if (c.Type != WalkCommandType.Move || c.Position.InDungeon != here.InDungeon)
                    continue;
                double d = c.Position.DistanceTo(here);
                if (d < bestDistance && d <= 300)
                {
                    bestDistance = d;
                    best = script;
                }
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ NPC business

    /// <summary>What to buy at this NPC: shop slot, how many in all, and the stack size (pure: tested).</summary>
    public static List<(ShopSlotInfo Slot, int Need, int Stack)> BuyPlan(Character c, TownSettings s, IGameData data, string npcServerName)
    {
        var plan = new List<(ShopSlotInfo, int, int)>();
        var shop = data.GetShopItems(npcServerName);
        foreach (var want in s.Buy.Where(b => b.Enabled && b.Quantity > 0))
        {
            if (shop.FirstOrDefault(x => string.Equals(x.ItemServerName, want.Item, StringComparison.OrdinalIgnoreCase)) is not { } slot)
                continue;
            int need = want.Quantity - CountOf(c, want.Item);
            if (need > 0)
                plan.Add((slot, need, Math.Max(1, (int)(data.GetItem(want.Item)?.Stack ?? 1))));
        }
        return plan;
    }

    /// <summary>Bag slots to sell and how many of each (pure: tested).</summary>
    public static List<(int Slot, ushort Quantity)> SellPlan(Character c, TownSettings s, PickFilterSettings? filter = null)
    {
        var plan = new List<(int, ushort)>();
        var excess = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (s.SellExcess)
            foreach (var want in s.Buy.Where(b => b.Enabled && b.Quantity > 0))
                if (CountOf(c, want.Item) - want.Quantity is var over and > 0)
                    excess[want.Item] = over;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
        {
            if (c.Inventory[i] is not { } item)
                continue;
            if (!IsOnBuyList(s, item) && !IsStored(item, s, filter) && (Matches(item, s.Sell) || filter is not null && PickFilter.ShouldSell(filter, item)))
            {
                plan.Add((i, Math.Max((ushort)1, item.Quantity)));
                continue;
            }
            if (excess.TryGetValue(item.ServerName, out int over) && over > 0 && IsConsumable(item))
            {
                int amount = Math.Min(over, Math.Max(1, (int)item.Quantity));
                plan.Add((i, (ushort)amount));
                excess[item.ServerName] = over - amount;
            }
        }
        return plan;
    }

    /// <summary>Bag slots to drop on the ground (pure: tested). Items on the buy list or the storage list are never dropped.</summary>
    public static List<int> DropPlan(Character c, TownSettings s, PickFilterSettings? filter = null) =>
        ListedSlots(c, s, item => (Matches(item, s.Drop) || filter is not null && PickFilter.ShouldDrop(filter, item)) && !IsStored(item, s, filter));

    /// <summary>Bag slots to put into the storage (pure: tested). Items on the buy list are never stored.</summary>
    public static List<int> StorePlan(Character c, TownSettings s, PickFilterSettings? filter = null) =>
        ListedSlots(c, s, item => IsStored(item, s, filter));

    private static bool IsStored(Item item, TownSettings s, PickFilterSettings? filter) =>
        Matches(item, s.Store) || filter is not null && PickFilter.ShouldStore(filter, item);

    private static List<int> ListedSlots(Character c, TownSettings s, Func<Item, bool> listed)
    {
        var slots = new List<int>();
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is { } item && !IsOnBuyList(s, item) && listed(item))
                slots.Add(i);
        return slots;
    }

    /// <summary>Gold to move at the Storage-keeper (see <see cref="PickFilter.GoldToStore"/>); before the storage was opened its gold is not known.</summary>
    private long GoldPlan(Character c) =>
        PickFilter.GoldToStore(Filter, c.Gold, _client.World.StorageLoads > 0 ? _client.World.StorageGold : Filter.TakeGoldFromStorage ? ulong.MaxValue / 4 : 0);

    private static bool IsOnBuyList(TownSettings s, Item item) =>
        s.Buy.Any(b => b.Enabled && string.Equals(b.Item, item.ServerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The item's name or server name has one of the parts of names at the start of a word: "hat" matches "Carnival
    /// Hat" and "pot" matches "potion", but "hat" does not match "Global chatting".
    /// </summary>
    public static bool Matches(Item item, List<string> names) =>
        names.Any(n => n.Trim().Length > 0 && (StartsAWord(item.Name, n.Trim()) || StartsAWord(item.ServerName, n.Trim())));

    private static bool StartsAWord(string text, string part)
    {
        for (int i = text.IndexOf(part, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(part, i + 1, StringComparison.OrdinalIgnoreCase))
            if (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
                return true;
        return false;
    }

    /// <summary>
    /// The storage slot for an item (pure: tested): a stack of the same item it fits on whole, else the first empty
    /// slot; null when the storage is full.
    /// </summary>
    public static int? StorageSlotFor(SlotList<Item> storage, Item item)
    {
        if (item.QuantityMax > 1)
            for (int i = 0; i < storage.Capacity; i++)
                if (storage[i] is { } there && there.Id == item.Id && there.Quantity + item.Quantity <= there.QuantityMax)
                    return i;
        for (int i = 0; i < storage.Capacity; i++)
            if (storage[i] is null)
                return i;
        return null;
    }

    public static bool IsStorageNpc(string serverName) => serverName.Contains("_WAREHOUSE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Potions, pills, arrows and bolts (what "sell excess" may sell).</summary>
    public static bool IsConsumable(Item item) => item.TypeId2 == 3 && item.TypeId3 is 1 or 2 or 4;

    public static bool IsRepairNpc(string serverName) =>
        serverName.Contains("_SMITH", StringComparison.OrdinalIgnoreCase) || serverName.Contains("_ARMOR", StringComparison.OrdinalIgnoreCase);

    private static int CountOf(Character c, string serverName)
    {
        int count = 0;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is { } item && string.Equals(item.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
                count += Math.Max(1, (int)item.Quantity);
        return count;
    }

    private bool HasBusiness(Character c, Npc npc) => HasBusiness(c, npc.ServerName);

    private bool HasBusiness(Character c, string npcServerName)
    {
        if (IsStorageNpc(npcServerName) && (StorePlan(c, _settings, Filter).Count > 0 || GoldPlan(c) != 0))
            return true;
        return HasShopBusiness(c, npcServerName);
    }

    private bool HasShopBusiness(Character c, string npcServerName)
    {
        if (_client.Data.GetShopItems(npcServerName).Count == 0)
            return false;
        return SellPlan(c, _settings, Filter).Count > 0
            || (_settings.Repair && IsRepairNpc(npcServerName))
            || BuyPlan(c, _settings, _client.Data, npcServerName).Count > 0;
    }

    /// <summary>The walker's guard: visit a shop NPC near the route. True when it walked off the route.</summary>
    private async Task<bool> VisitAsync(CancellationToken token)
    {
        if (_client.World.Character is not { } c)
            return false;
        var here = c.Position;
        var npc = _client.World.Npcs.Values
            .Where(n => !_visited.Contains(n.UniqueId) && n.Position.InDungeon == here.InDungeon && n.Position.DistanceTo(here) <= VisitRange)
            .OrderBy(n => n.Position.DistanceTo(here))
            .FirstOrDefault(n => HasBusiness(c, n));
        if (npc is null)
            return false;
        _visited.Add(npc.UniqueId);
        await WalkNearAsync(npc, token).ConfigureAwait(false);
        if (IsStorageNpc(npc.ServerName) && (StorePlan(c, _settings, Filter).Count > 0 || GoldPlan(c) != 0))
        {
            try
            {
                await StoreAsync(npc, token).ConfigureAwait(false);
            }
            finally
            {
                _client.CloseNpc(npc.UniqueId);
                await Task.Delay(400, token).ConfigureAwait(false);
            }
            if (!HasShopBusiness(c, npc.ServerName))
                return true;
        }
        await _client.OpenNpcAsync(npc.UniqueId, ShopTarget.StoreTalk, token).ConfigureAwait(false);
        try
        {
            await SellAsync(npc, token).ConfigureAwait(false);
            if (_settings.Repair && IsRepairNpc(npc.ServerName))
                await RepairAsync(npc, token).ConfigureAwait(false);
            await BuyAsync(npc, token).ConfigureAwait(false);
        }
        finally
        {
            _client.CloseNpc(npc.UniqueId);
            await Task.Delay(400, token).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Waits a moment (at most 3 s) for an NPC that just came into sight to appear near the character.</summary>
    private async Task WaitForNpcAsync(string serverName, CancellationToken token)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < until && _client.World.Character is { } c)
        {
            var here = c.EstimatePosition();
            if (_client.World.Npcs.Values.Any(n => string.Equals(n.ServerName, serverName, StringComparison.OrdinalIgnoreCase)
                    && n.Position.DistanceTo(here) <= VisitRange))
                return;
            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Steps up to the NPC. NPCs often stand behind a counter the character cannot pass: it stops as soon as it gets no
    /// nearer for a while (the game lets it talk from there) instead of pushing against the counter.
    /// </summary>
    private async Task WalkNearAsync(Npc npc, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        DateTime lastSend = DateTime.MinValue;
        double best = double.MaxValue;
        var bestAt = DateTime.UtcNow;
        while (_client.World.Character is { } c && DateTime.UtcNow - started < TimeSpan.FromSeconds(15))
        {
            double distance = c.EstimatePosition().DistanceTo(npc.Position);
            if (distance <= 6)
                return;
            var now = DateTime.UtcNow;
            if (distance < best - 0.5)
            {
                best = distance;
                bestAt = now;
            }
            else if (now - bestAt > TimeSpan.FromSeconds(2.5))
            {
                return; // against the counter: close enough to talk
            }
            if (now - lastSend > TimeSpan.FromSeconds(2))
            {
                _client.Send(ClientPackets.MoveTo(npc.Position, c.IsRiding ? c.RidingUniqueId : 0));
                lastSend = now;
            }
            await Task.Delay(150, token).ConfigureAwait(false);
        }
    }

    /// <summary>Drops the items of the drop list (when the town loop starts).</summary>
    private async Task DropAsync(CancellationToken token)
    {
        if (_client.World.Character is not { } c)
            return;
        foreach (int slot in DropPlan(c, _settings, Filter))
        {
            var item = c.Inventory[slot];
            var result = await _client.DropItemAsync((byte)slot, token).ConfigureAwait(false);
            _logger.LogInformation("Town: drop {Quantity} x {Item}: {Result}", Math.Max(1, (int)(item?.Quantity ?? 1)), item?.Name ?? "?",
                result.Ok ? "dropped" : result.Message);
            await Task.Delay(300, token).ConfigureAwait(false);
        }
    }

    /// <summary>Puts the items of the storage list into the storage at a Storage-keeper.</summary>
    private async Task StoreAsync(Npc npc, CancellationToken token)
    {
        if (_client.World.Character is not { } c)
            return;
        string? why = await _client.OpenStorageAsync(npc.UniqueId, token).ConfigureAwait(false);
        if (why is not null || _client.World.Storage is not { } storage)
        {
            _logger.LogWarning("Town: the storage at {Npc} did not open: {Why}", npc.Name, why ?? "its contents are not known");
            return;
        }
        long gold = PickFilter.GoldToStore(Filter, c.Gold, _client.World.StorageGold);
        if (gold != 0)
        {
            var moved = await _client.StorageGoldAsync(npc.UniqueId, gold > 0, (ulong)Math.Abs(gold), token).ConfigureAwait(false);
            _logger.LogInformation("Town: {What} {Gold:N0} gold at {Npc}: {Result}", gold > 0 ? "store" : "take", Math.Abs(gold), npc.Name,
                moved.Ok ? "done" : moved.Message);
            await Task.Delay(300, token).ConfigureAwait(false);
        }
        foreach (int slot in StorePlan(c, _settings, Filter))
        {
            if (c.Inventory[slot] is not { } item)
                continue;
            if (StorageSlotFor(storage, item) is not { } to)
            {
                _logger.LogWarning("Town: the storage is full; {Item} stays in the bag", item.Name);
                return;
            }
            var result = await _client.StoreItemAsync(npc.UniqueId, (byte)slot, (byte)to, token).ConfigureAwait(false);
            _logger.LogInformation("Town: store {Quantity} x {Item} at {Npc}: {Result}", Math.Max(1, (int)item.Quantity), item.Name, npc.Name,
                result.Ok ? "stored" : result.Message);
            if (result.Message.StartsWith("The server did not answer", StringComparison.Ordinal) || result.Message.StartsWith("The bot is not", StringComparison.Ordinal))
                return;
            await Task.Delay(300, token).ConfigureAwait(false); // a refused item stays in the bag; the next is tried
        }
    }

    private async Task SellAsync(Npc npc, CancellationToken token)
    {
        if (_client.World.Character is not { } c)
            return;
        foreach (var (slot, quantity) in SellPlan(c, _settings, Filter))
        {
            string name = c.Inventory[slot]?.Name ?? "?";
            var result = await _client.SellFromInventoryAsync(npc.UniqueId, (byte)slot, quantity, token).ConfigureAwait(false);
            _logger.LogInformation("Town: sell {Quantity} x {Item} at {Npc}: {Result}", quantity, name, npc.Name, result.Ok ? "sold" : result.Message);
            await Task.Delay(300, token).ConfigureAwait(false);
        }
    }

    private async Task RepairAsync(Npc npc, CancellationToken token)
    {
        if (_settings.RepairEquippedOnly)
        {
            for (byte slot = 0; slot < Character.FirstInventorySlot; slot++)
            {
                if (_client.World.Character?.Inventory[slot] is not Equipable)
                    continue;
                _client.Send(ClientPackets.RepairItem(npc.UniqueId, slot));
                await Task.Delay(300, token).ConfigureAwait(false);
            }
        }
        else
        {
            _client.Send(ClientPackets.RepairAll(npc.UniqueId));
            await Task.Delay(800, token).ConfigureAwait(false);
        }
        _logger.LogInformation("Town: repaired at {Npc}", npc.Name);
    }

    private async Task BuyAsync(Npc npc, CancellationToken token)
    {
        if (_client.World.Character is not { } c)
            return;
        foreach (var (slot, need, stack) in BuyPlan(c, _settings, _client.Data, npc.ServerName))
        {
            string name = _client.Data.GetItem(slot.ItemServerName)?.Name ?? slot.ItemServerName;
            int bought = 0;
            for (int left = need; left > 0;)
            {
                if (ProtectionCatalog.FreeSlots(c) == 0 && !HasRoomInStack(c, slot.ItemServerName))
                {
                    _logger.LogWarning("Town: the inventory is full; could not buy {Left} more {Item}", left, name);
                    if (_settings.StopIfInventoryFull)
                    {
                        StopRequested?.Invoke("the inventory is full while buying");
                        Stop();
                    }
                    return;
                }
                ushort amount = (ushort)Math.Min(left, stack);
                var result = await _client.BuyAsync(npc.UniqueId, false, slot.Tab, slot.Slot, amount, token).ConfigureAwait(false);
                if (!result.Ok)
                {
                    _logger.LogWarning("Town: buying {Item} at {Npc} refused: {Message}", name, npc.Name, result.Message);
                    break;
                }
                bought += amount;
                left -= amount;
                await Task.Delay(300, token).ConfigureAwait(false);
            }
            if (bought > 0)
                _logger.LogInformation("Town: bought {Count} x {Item} at {Npc}", bought, name, npc.Name);
        }
    }

    private static bool HasRoomInStack(Character c, string serverName)
    {
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is { } item && string.Equals(item.ServerName, serverName, StringComparison.OrdinalIgnoreCase) && item.Quantity < item.QuantityMax)
                return true;
        return false;
    }

    public void Dispose()
    {
        Stop();
        lock (_lock)
        {
            _cts?.Dispose();
            _cts = null;
        }
    }
}
