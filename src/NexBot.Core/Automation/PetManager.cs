using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// Looks after the attack pet (phBot's Pet tab): summons it, heals it with recovery kits, feeds it HGP potions, keeps
/// it passive when asked, revives it when it dies (or asks to return to town for revive items).
/// </summary>
public sealed class PetManager : IDisposable
{
    private static readonly TimeSpan SummonEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ModeEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReviveEvery = TimeSpan.FromSeconds(5);

    private readonly SroClient _client;
    private readonly PetSettings _settings;
    private readonly TrainingAreaSettings _area;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastSummon, _lastHeal, _lastFeed, _lastMode, _lastRevive;
    private bool _returnAsked, _reviveLimitLogged, _deadEpisode;
    private int _failures;
    private string _lastFailure = "";

    /// <summary>Failed revives / summons in a row after the pet died before giving up and returning to town.</summary>
    public const int FailuresBeforeReturn = 3;

    public PetManager(SroClient client, PetSettings settings, TrainingAreaSettings area, ILogger? logger = null, bool startLoop = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _area = area ?? throw new ArgumentNullException(nameof(area));
        _logger = logger ?? NullLogger.Instance;
        client.PetDied += OnPetDied;
        if (startLoop)
            _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>True when the character is in a town.</summary>
    public Func<bool> InTown { get; set; } = () => false;

    /// <summary>Asks the protection to return to town, with the reason.</summary>
    public event Action<string>? ReturnRequested;

    /// <summary>Revives done this session.</summary>
    public int Revives { get; private set; }

    /// <summary>What it does now, for the Pet page.</summary>
    public string Status { get; private set; } = "";

    /// <summary>The attack pet scroll to summon (alive) and the dead one, -1 when none.</summary>
    public static (int Alive, int Dead) Scrolls(Character c)
    {
        int alive = -1, dead = -1;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
        {
            if (c.Inventory[i] is not CosItem scroll || !SroClient.IsAttackPetScroll(scroll))
                continue;
            if (scroll.State == PetItemState.Dead)
            {
                if (dead < 0)
                    dead = i;
            }
            else if (alive < 0)
            {
                alive = i;
            }
        }
        return (alive, dead);
    }

    /// <summary>
    /// The attack pet scroll to use: the preferred pets first, in their order (then the others, alive ones first). The
    /// first one that is alive is summoned; a dead one is revived when <paramref name="reviveAllowed"/>, else skipped.
    /// Null: nothing to summon or revive.
    /// </summary>
    public static (int Slot, bool Revive)? Choose(Character c, IReadOnlyList<string> preferred, bool reviveAllowed)
    {
        var scrolls = new List<(int Slot, bool Dead, int Rank)>();
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
        {
            if (c.Inventory[i] is not CosItem scroll || !SroClient.IsAttackPetScroll(scroll))
                continue;
            int rank = -1;
            for (int r = 0; r < preferred.Count && rank < 0; r++)
                if (string.Equals(preferred[r], scroll.ServerName, StringComparison.OrdinalIgnoreCase))
                    rank = r;
            scrolls.Add((i, scroll.State == PetItemState.Dead, rank < 0 ? int.MaxValue : rank));
        }
        foreach (var (slot, dead, _) in scrolls.OrderBy(x => x.Rank).ThenBy(x => x.Dead).ThenBy(x => x.Slot))
        {
            if (!dead)
                return (slot, false);
            if (reviveAllowed)
                return (slot, true);
        }
        return null;
    }

    /// <summary>All attack pets in the game (summon scrolls, type 2/1/1), the ones in the bag first.</summary>
    public static IReadOnlyList<TradeOption> AllAttackPets(Data.IGameData data, Character? character) =>
        [.. data.GetItemsOfType(2, 1).Where(i => i.Tid4 == 1)
            .Select(i => new TradeOption(i, character is null ? 0 : Enumerable.Range(Character.FirstInventorySlot, Math.Max(0, character.Inventory.Capacity - Character.FirstInventorySlot))
                .Count(s => character.Inventory[s]?.Id == i.Id), character is null || character.Level >= i.Level))
            .OrderByDescending(o => o.Owned > 0).ThenBy(o => o.Info.Level).ThenBy(o => o.Info.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>What to do with a dead attack pet: "return", "revive" or why nothing (pure: tested).</summary>
    public static string DeadPetPlan(PetSettings s, int revivesDone, int reviveItems, int recoveryKits)
    {
        if (s.ReturnWhenDies && (!s.ReturnOnlyIfNoRevive || reviveItems == 0))
            return "return";
        if (!s.AutoRevive)
            return "auto revive is off";
        if (s.ReviveTimes > 0 && revivesDone >= s.ReviveTimes)
            return $"revived {revivesDone} times already (the limit)";
        if (reviveItems == 0)
            return "no pet revive item in the bag";
        if (s.ReviveOnlyWithPotions && recoveryKits == 0)
            return "no pet recovery kits (revive only with potions)";
        return "revive";
    }

    /// <summary>Why the attack pet is not summoned now (null: summon it).</summary>
    public static string? SummonRefusal(PetSettings s, bool inTown, bool inTrainingArea) =>
        !s.AutoSummon ? "auto summon is off"
            : s.SummonOnlyInTown && !inTown ? "not in town (summon only in town)"
            : s.SummonOnlyAtTrainingArea && !inTrainingArea ? "not at the training area (summon only there)"
            : null;

    private void OnPetDied(object? sender, EntityEventArgs e)
    {
        if (e.Entity is not AttackPet pet || _client.World.Character is not { } c)
            return;
        // The scroll of the pet that was out is dead now (the server tells the state again only at login).
        CosItem? scroll = null;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity && scroll is null; i++)
            if (c.Inventory[i] is CosItem s && SroClient.IsAttackPetScroll(s)
                && (s.State == PetItemState.Summoned || (s.ModelId != 0 && s.ModelId == pet.RefId)))
                scroll = s;
        scroll ??= Enumerable.Range(Character.FirstInventorySlot, c.Inventory.Capacity - Character.FirstInventorySlot)
            .Select(i => c.Inventory[i]).OfType<CosItem>().FirstOrDefault(SroClient.IsAttackPetScroll);
        if (scroll is not null)
            scroll.State = PetItemState.Dead;
        _deadEpisode = true;
        _failures = 0;
        _logger.LogWarning("Pet: the attack pet {Name} died", pet.Name.Length > 0 ? pet.Name : pet.Info.Name);
    }

    /// <summary>One pass (the loop runs it twice a second; tests call it directly).</summary>
    public async Task StepAsync(CancellationToken token)
    {
        if (!_settings.UseAttackPet)
        {
            Status = "Off: turn on Use attack pet.";
            return;
        }
        if (!_client.IsInGame || _client.World.Character is not { } c || c.LifeState == LifeState.Dead)
            return;
        var now = DateTime.UtcNow;
        if (_client.AttackPet is { } pet)
        {
            _returnAsked = false;
            _deadEpisode = false;
            _failures = 0;
            Status = $"{(pet.Name.Length > 0 ? pet.Name : pet.Info.Name)} is out.";
            KeepMode(pet, now);
            await HealAsync(c, pet, now, token).ConfigureAwait(false);
            return;
        }

        var (alive, dead) = Scrolls(c);
        if (alive < 0 && dead < 0)
        {
            Status = "No attack pet scroll in the bag.";
            return;
        }
        int reviveItems = ProtectionCatalog.Count(c, Supply.PetRevive);
        string plan = dead >= 0 ? DeadPetPlan(_settings, Revives, reviveItems, ProtectionCatalog.Count(c, Supply.PetRecoveryKit)) : "";
        // The preferred pets in their order: summon the first one that is alive, or revive it when it is dead.
        var choice = Choose(c, _settings.Preferred, plan == "revive");
        if (choice is { Revive: false } summon)
        {
            string? why = SummonRefusal(_settings, InTown(), _area.Contains(c.Position.PosX, c.Position.PosY));
            if (why is not null)
            {
                Status = $"Not summoned: {why}.";
                return;
            }
            if (now - _lastSummon < SummonEvery)
                return;
            _lastSummon = now;
            var summoned = await _client.SummonPetAsync((byte)summon.Slot, token).ConfigureAwait(false);
            Status = summoned.Ok ? $"Summoning {c.Inventory[summon.Slot]?.Name}." : $"Summon failed: {summoned.Message}";
            _logger.LogInformation("Pet: summon {Item}: {Result}", c.Inventory[summon.Slot]?.Name, summoned.Message);
            // A revived pet cannot be summoned while the character is attacked (0x1878): it keeps trying, and after
            // a few refusals in a row returns to town when asked to (the scroll stays alive: no second revive).
            if (!summoned.Ok && _deadEpisode)
                Failed(summoned.Message);
            return;
        }
        if (choice is null)
        {
            if (plan == "return")
            {
                Status = "The attack pet is dead: returning to town.";
                if (!_returnAsked)
                {
                    _returnAsked = true;
                    ReturnRequested?.Invoke("the attack pet died" + (reviveItems == 0 ? " and there are no revive items" : ""));
                }
                return;
            }
            Status = $"The attack pet is dead: {plan}.";
            if (!_reviveLimitLogged)
                _logger.LogInformation("Pet: the attack pet is dead and is not revived: {Why}", plan);
            _reviveLimitLogged = true;
            return;
        }
        dead = choice.Value.Slot;
        if (now - _lastRevive < ReviveEvery)
            return;
        _lastRevive = now;
        int reviveSlot = ProtectionCatalog.Find(c, Supply.PetRevive);
        var revived = await _client.RevivePetAsync((byte)reviveSlot, (byte)dead, token).ConfigureAwait(false);
        if (revived.Ok)
        {
            Revives++;
            _reviveLimitLogged = false;
            if (c.Inventory[dead] is CosItem scroll)
                scroll.State = PetItemState.Unsummoned;
            _lastSummon = default; // summon right away
            Status = "Revived the attack pet.";
            _logger.LogInformation("Pet: revived the attack pet ({Count} this session)", Revives);
        }
        else
        {
            Status = $"Revive failed: {revived.Message}";
            _logger.LogWarning("Pet: revive failed: {Message}", revived.Message);
            Failed(revived.Message);
        }
    }

    /// <summary>
    /// The dead pet could not be revived, or the revived pet not summoned (refused while the character is attacked). After
    /// <see cref="FailuresBeforeReturn"/> in a row it asks to return to town, when that option is on.
    /// </summary>
    private void Failed(string message)
    {
        _failures++;
        _lastFailure = message;
        if (!GiveUp(_settings, _failures) || _returnAsked)
            return;
        _returnAsked = true;
        Status = "The attack pet could not be brought back: returning to town.";
        ReturnRequested?.Invoke($"the attack pet died and could not be revived or summoned ({_failures} tries: {_lastFailure.TrimEnd('.')})");
    }

    /// <summary>Whether to stop trying and return to town (pure: tested).</summary>
    public static bool GiveUp(PetSettings s, int failuresInARow) => s.ReturnWhenDies && failuresInARow >= FailuresBeforeReturn;

    private void KeepMode(AttackPet pet, DateTime now)
    {
        if (!_settings.DontAttack || pet.Settings == AttackPetSettings.Disabled || now - _lastMode < ModeEvery)
            return;
        _lastMode = now;
        _client.SetAttackPetMode(pet.UniqueId, offensive: false);
        _logger.LogInformation("Pet: {Name} set to not attack monsters", pet.Name);
    }

    private async Task HealAsync(Character c, AttackPet pet, DateTime now, CancellationToken token)
    {
        uint hpMax = pet.HpMax > 0 ? pet.HpMax : pet.Info.Hp;
        if (_settings.HealBelow > 0 && hpMax > 0 && pet.Hp * 100.0 / hpMax < _settings.HealBelow && now - _lastHeal >= HealEvery)
        {
            _lastHeal = now;
            int kit = ProtectionCatalog.Find(c, Supply.PetRecoveryKit);
            if (kit >= 0)
            {
                var used = await _client.UseItemOnPetAsync((byte)kit, pet.UniqueId, token).ConfigureAwait(false);
                if (!used.Ok)
                    _logger.LogWarning("Pet: recovery kit: {Message}", used.Message);
            }
        }
        // HGP counts to 10000 (100%).
        if (_settings.FeedBelow > 0 && pet.Hgp / 100.0 < _settings.FeedBelow && now - _lastFeed >= HealEvery)
        {
            _lastFeed = now;
            int food = ProtectionCatalog.Find(c, Supply.PetFeed);
            if (food >= 0)
            {
                var used = await _client.UseItemOnPetAsync((byte)food, pet.UniqueId, token).ConfigureAwait(false);
                if (!used.Ok)
                    _logger.LogWarning("Pet: HGP potion: {Message}", used.Message);
            }
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);
                await StepAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or IndexOutOfRangeException)
            {
                _logger.LogWarning("Pet: {Message}", ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _client.PetDied -= OnPetDied;
    }
}
