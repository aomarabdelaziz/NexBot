using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// Picks up the drops the pick filter asks for: with the grab pet when it is out, else with the character when nothing
/// else moves it (no script, trade route or town loop). One item at a time; an item that is not picked up after a few
/// tries is left alone.
/// </summary>
public sealed class PickManager : IDisposable
{
    /// <summary>How far from the character drops are picked up (world units).</summary>
    public const double PickRange = 60;
    private const int MaxTries = 3;
    /// <summary>How near the character walks before it sends the pick-up (the server does not walk it there).</summary>
    public const double PickReach = 3;
    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PetWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CharacterWait = TimeSpan.FromSeconds(5);

    private readonly SroClient _client;
    private readonly PickFilterSettings _settings;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<uint, int> _tries = [];
    private readonly HashSet<uint> _explained = [];
    private uint _current, _walkingTo;
    private DateTime _sentAt, _walkStarted, _walkSent;

    public PickManager(SroClient client, PickFilterSettings settings, ILogger? logger = null, bool startLoop = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullLogger.Instance;
        client.ItemPicked += OnItemPicked;
        client.GoldPicked += OnGoldPicked;
        if (startLoop)
            _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>One item (or gold) picked up this session.</summary>
    public sealed record PickedUp(DateTime At, string Name, string? Icon, int Quantity, string By);

    private readonly List<PickedUp> _history = [];
    private readonly Lock _historyLock = new();

    /// <summary>What was picked up this session, newest first (the last 200).</summary>
    public IReadOnlyList<PickedUp> History
    {
        get
        {
            lock (_historyLock)
                return [.. _history];
        }
    }

    private void Remember(PickedUp entry)
    {
        lock (_historyLock)
        {
            _history.Insert(0, entry);
            if (_history.Count > 200)
                _history.RemoveAt(_history.Count - 1);
        }
    }

    /// <summary>How far drops are picked up (the training area's pick radius).</summary>
    public Func<double> Range { get; set; } = () => PickRange;

    /// <summary>The map data, for collision detection and walking to drops on routes (null: not loaded).</summary>
    public Func<NexBot.Navigation.IWalkMap?> Map { get; set; } = () => null;

    /// <summary>Training Area, Collision tab: leave drops behind walls / walk to drops on routes.</summary>
    public Func<bool> CollisionDetection { get; set; } = () => false;
    public Func<bool> NavigateToDrops { get; set; } = () => false;

    /// <summary>Drops wait for the character to pick them up (the grab pet is not doing it).</summary>
    public bool CharacterWork { get; private set; }

    /// <summary>True when the character may walk to a drop (no script, trade route or town loop moves it).</summary>
    public Func<bool> CharacterMayPick { get; set; } = () => false;

    /// <summary>What it does now, for the Pick Filter page.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Items picked up this session.</summary>
    public int Picked { get; private set; }

    private void OnItemPicked(object? sender, ItemPickedEventArgs e)
    {
        Picked++;
        Remember(new PickedUp(DateTime.Now, PickFilter.Label(e.Item.Name, e.Item.ServerName), e.Item.Info.Icon.Length > 0 ? e.Item.Info.Icon : null,
            Math.Max(1, (int)e.Item.Quantity), e.IntoPet ? "grab pet (its bag)" : e.ByPet ? "grab pet" : "character"));
    }

    private void OnGoldPicked(object? sender, GoldPickedEventArgs e)
    {
        Picked++;
        Remember(new PickedUp(DateTime.Now, $"{e.Gold:N0} gold", null, 1, e.ByPet ? "grab pet" : "character"));
    }

    /// <summary>The drops to pick up, nearest first (pure: tested).</summary>
    public static List<Drop> Wanted(PickFilterSettings s, Character c, IEnumerable<Drop> drops, IEnumerable<uint> partyJoinIds) =>
        [.. Review(s, c, drops, partyJoinIds).Where(x => x.Why is null).Select(x => x.Drop)];

    /// <summary>The drops near the character, nearest first, each with why it is not picked up (null: it is).</summary>
    public static List<(Drop Drop, string? Why)> Review(PickFilterSettings s, Character c, IEnumerable<Drop> drops, IEnumerable<uint> partyJoinIds,
        double range = PickRange)
    {
        bool european = PickFilter.IsEuropean(c);
        int ammo = 0;
        for (int i = 0; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is { } item && PickFilter.IsOwnAmmo(item.ServerName, european))
                ammo += Math.Max(1, (int)item.Quantity);
        var here = c.EstimatePosition();
        var party = partyJoinIds.ToList();
        return [.. drops
            .Where(d => d.Position.InDungeon == here.InDungeon && d.Position.DistanceTo(here) <= range)
            .OrderBy(d => d.Position.DistanceTo(here))
            .Select(d => (d, PickFilter.WhyNot(s, d, european, ammo, c.JoinId, party)))];
    }

    /// <summary>Room in the bag for this drop: gold always fits; else an empty slot or a stack of it that is not full.</summary>
    public static bool HasRoom(SlotList<Item> bag, int firstSlot, Drop drop)
    {
        if (drop.IsGold)
            return true;
        for (int i = firstSlot; i < bag.Capacity; i++)
        {
            if (bag[i] is not { } item)
                return true;
            if (item.Id == drop.Info.Id && item.QuantityMax > 1 && item.Quantity < item.QuantityMax)
                return true;
        }
        return false;
    }

    private void Step()
    {
        CharacterWork = false;
        if (!_settings.Enabled || _settings.DontPickItems)
        {
            Status = _settings.DontPickItems ? "Don't pick items is on." : "Picking up is off.";
            return;
        }
        if (!_client.IsInGame || _client.World.Character is not { } c || c.LifeState == LifeState.Dead)
        {
            Status = "Not in game.";
            return;
        }
        var now = DateTime.UtcNow;
        if (_current != 0)
        {
            bool gone = !_client.World.Drops.ContainsKey(_current);
            if (!gone && now - _sentAt < (_client.PickPet is null ? CharacterWait : PetWait))
            {
                CharacterWork = _walkingTo == 0 && (_client.PickPet is null || !_settings.UsePickPet);
                return;
            }
            _current = 0;
        }
        foreach (uint gone in _tries.Keys.Where(k => !_client.World.Drops.ContainsKey(k)).ToList())
            _tries.Remove(gone);
        _explained.RemoveWhere(k => !_client.World.Drops.ContainsKey(k));

        var party = _client.World.Party?.Members.Keys ?? Enumerable.Empty<uint>();
        var review = Review(_settings, c, _client.World.Drops.Values, party, Range());
        if (CollisionDetection() && Map() is { } walls && !c.Position.InDungeon)
        {
            var from = c.EstimatePosition();
            review = [.. review.Select(x => x.Why is null && !NexBot.Navigation.PathFinder.IsClear(walls, from, x.Drop.Position) ? (x.Drop, (string?)"behind a wall") : x)];
        }
        // Every drop near that is not picked up is logged once, with why (to check the filter).
        foreach (var (skipped, why) in review)
            if (why is not null && _explained.Add(skipped.UniqueId))
                _logger.LogInformation("Pick: {Item} is not picked up: {Why}", NameOf(skipped), why);
        var wanted = review.Where(x => x.Why is null && _tries.GetValueOrDefault(x.Drop.UniqueId) < MaxTries).Select(x => x.Drop).ToList();
        if (wanted.Count == 0)
        {
            var first = review.FirstOrDefault(x => x.Why is not null || _tries.GetValueOrDefault(x.Drop.UniqueId) >= MaxTries);
            Status = $"Nothing to pick up. Picked up this session: {Picked}."
                + (first.Drop is null ? "" : $"\nNear: {NameOf(first.Drop)}: {first.Why ?? $"left alone after {MaxTries} tries"}"
                    + (review.Count > 1 ? $" (+{review.Count - 1} more, see the log)" : ""));
            return;
        }

        var pet = _settings.UsePickPet ? _client.PickPet : null;
        var drop = wanted[0];
        string name = NameOf(drop);
        if (pet is not null && HasRoom(pet.Inventory, 0, drop))
        {
            Send(drop, () => _client.PetPickUp(pet.UniqueId, drop.UniqueId), $"The grab pet picks up {name}.");
            return;
        }
        if (_settings.UsePickPet && !_settings.PickWithCharacterIfNoPet)
        {
            Status = pet is null ? "Waiting for the grab pet (summon it)." : "The grab pet's bag is full.";
            return;
        }
        if (!HasRoom(c.Inventory, Character.FirstInventorySlot, drop))
        {
            Status = "The bag is full.";
            return;
        }
        CharacterWork = true;
        if (!CharacterMayPick())
        {
            Status = $"{wanted.Count} item(s) to pick up: the character is busy (script, trade or town loop).";
            return;
        }
        // Walk to the drop first, as the game client does: a pick-up sent from afar ends with nothing (0xB074 [02 00]).
        double distance = c.EstimatePosition().DistanceTo(drop.Position);
        if (distance > PickReach)
        {
            if (_walkingTo != drop.UniqueId)
                (_walkingTo, _walkStarted, _walkSent) = (drop.UniqueId, now, DateTime.MinValue);
            if (now - _walkStarted > WalkTimeout)
            {
                _walkingTo = 0;
                _tries[drop.UniqueId] = _tries.GetValueOrDefault(drop.UniqueId) + 1;
                _logger.LogInformation("Pick: could not walk to {Item} ({Distance:0} away)", name, distance);
                return;
            }
            if (now - _walkSent > TimeSpan.FromSeconds(2))
            {
                var step = drop.Position;
                if (NavigateToDrops() && Map() is { } map && !c.Position.InDungeon
                    && NexBot.Navigation.PathFinder.Find(map, c.EstimatePosition(), drop.Position) is { Count: > 0 } route)
                    step = route[0];
                _client.Send(ClientPackets.MoveTo(step, c.IsRiding ? c.RidingUniqueId : 0));
                _walkSent = now;
            }
            Status = $"Walking to {name} ({distance:0} away).";
            return;
        }
        _walkingTo = 0;
        Send(drop, () => _client.PickUp(drop.UniqueId), $"Picking up {name}.");
    }

    private void Send(Drop drop, Func<bool> send, string status)
    {
        if (!send())
        {
            Status = "The bot is not connected.";
            return;
        }
        _current = drop.UniqueId;
        _sentAt = DateTime.UtcNow;
        _tries[drop.UniqueId] = _tries.GetValueOrDefault(drop.UniqueId) + 1;
        Status = status;
        if (_tries[drop.UniqueId] == MaxTries)
            _logger.LogInformation("Pick: {Item} was not picked up after {Tries} tries; it is left alone", NameOf(drop), MaxTries);
    }

    private static string NameOf(Drop drop) =>
        drop.IsGold ? $"{drop.Gold:N0} gold" : PickFilter.Label(drop.Name, drop.ServerName) + (drop.Plus > 0 ? $" +{drop.Plus}" : "");

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                Step();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or IndexOutOfRangeException)
            {
                _logger.LogWarning("Pick: {Message}", ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _client.ItemPicked -= OnItemPicked;
        _client.GoldPicked -= OnGoldPicked;
    }
}
