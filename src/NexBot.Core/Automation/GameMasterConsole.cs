using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Network;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// Game master tools (GM characters only): spawn monsters by name, kill them, make items, move players, and a
/// power-leveling loop that keeps monsters spawned next to the GM for the players to kill.
/// </summary>
public sealed class GameMasterConsole : IDisposable
{
    private readonly SroClient _client;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, DateTime> _seen = [];
    private CancellationTokenSource? _loop;

    public GameMasterConsole(SroClient client, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Monster spawn types for loadmonster (the game's monster rarity).</summary>
    public static readonly (string Name, byte Value)[] SpawnTypes =
    [
        ("General", 0), ("Champion", 1), ("Giant", 4), ("Party general", 0x10), ("Party champion", 0x11), ("Party giant", 0x14),
    ];

    public bool IsGameMaster => _client.World.Character?.IsGameMaster == true;
    public bool IsLooping => _loop is not null;
    public string LoopStatus { get; private set; } = "Stopped.";
    public event Action? LoopChanged;

    // ---------------------------------------------------------------- commands

    public void Spawn(ModelInfo monster, int count, byte type)
    {
        count = Math.Clamp(count, 1, 50);
        _logger.LogInformation("GM: spawning {Count} x {Monster} (type {Type})", count, monster.Name, type);
        _client.Send(ClientPackets.GmCreateMob(monster.Id, (byte)count, type));
    }

    /// <summary>Living monsters within <paramref name="radius"/> of the character (optionally of one kind), nearest first.</summary>
    public List<Mob> MobsNear(double radius, uint? modelId = null)
    {
        if (_client.World.Character is not { } c)
            return [];
        var here = c.Position;
        return _client.World.Mobs.Values
            .Where(m => m.LifeState != LifeState.Dead && (modelId is null || m.Info.Id == modelId)
                        && m.Position.InDungeon == here.InDungeon && m.Position.DistanceTo(here) <= radius)
            .OrderBy(m => m.Position.DistanceTo(here))
            .ToList();
    }

    /// <summary>Kills every living monster within the radius, one GM kill each. Returns how many were sent.</summary>
    public int KillNear(double radius, uint? modelId = null)
    {
        var mobs = MobsNear(radius, modelId);
        foreach (var m in mobs)
            _client.Send(ClientPackets.GmKillMob(m.UniqueId));
        if (mobs.Count > 0)
            _logger.LogInformation("GM: killing {Count} monsters within {Radius}", mobs.Count, radius);
        return mobs.Count;
    }

    public void Zoe()
    {
        _logger.LogInformation("GM: zoe (kill the monsters around)");
        _client.Send(ClientPackets.GmZoe());
    }

    /// <summary>A monster by ID, server name (MOB_CH_TIGER) or the best name match.</summary>
    public ModelInfo? FindMonster(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            return _client.Data.GetModel(id);
        if (text.StartsWith("MOB_", StringComparison.OrdinalIgnoreCase) && _client.Data.GetModel(text.ToUpperInvariant()) is { } byCode)
            return byCode;
        var found = _client.Data.SearchMonsters(text, 200);
        var exact = found.FirstOrDefault(m => string.Equals(m.Model.Name, text, StringComparison.OrdinalIgnoreCase));
        return exact.Model ?? (found.Count > 0 ? found[0].Model : null);
    }

    /// <summary>An item by ID, server name or the best name match.</summary>
    public ItemInfo? FindItem(string text)
    {
        text = text.Trim();
        if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            return _client.Data.GetItem(id);
        return _client.Data.GetItem(text.ToUpperInvariant()) ?? _client.Data.SearchItems(text, 5).FirstOrDefault();
    }

    public const string Help =
        "loadmonster <name|MOB_code|id> [count] [type]  spawn monsters (type 0 general, 1 champion, 4 giant, 16 party)\n" +
        "killmob [radius]                               kill the living monsters near you, one by one (default 50)\n" +
        "zoe                                            the game's /zoe: kill the monsters around you\n" +
        "makeitem <name|ITEM_code|id> [plus/amount]     create an item\n" +
        "recall <player>  ·  moveto <player>  ·  totown <player>\n" +
        "gotown  ·  invisible  ·  invincible  ·  worldstatus  ·  help";

    /// <summary>Runs one console line; returns what to show.</summary>
    public string Execute(string line)
    {
        var parts = line.Trim().TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "";
        string cmd = parts[0].ToLowerInvariant();
        string rest = string.Join(' ', parts.Skip(1));
        if (cmd is "help" or "?")
            return Help;
        if (!_client.IsInGame)
            return "Not in game.";
        if (!IsGameMaster)
            return "This character is not a game master.";
        switch (cmd)
        {
            case "loadmonster" or "lm" or "spawn":
            {
                // Trailing numbers are the count and the type; the rest names the monster.
                var words = parts.Skip(1).ToList();
                var numbers = new List<int>();
                while (words.Count > 1 && numbers.Count < 2
                       && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                {
                    numbers.Insert(0, n);
                    words.RemoveAt(words.Count - 1);
                }
                string name = string.Join(' ', words);
                if (FindMonster(name) is not { } mob)
                    return $"No monster matches \"{name}\".";
                int count = numbers.Count > 0 ? numbers[0] : 1;
                byte type = numbers.Count > 1 ? (byte)Math.Clamp(numbers[1], 0, 255) : (byte)0;
                Spawn(mob, count, type);
                return $"Spawn {Math.Clamp(count, 1, 50)} x {mob.Name} ({mob.ServerName}, id {mob.Id}), type {type}.";
            }
            case "killmob" or "km" or "kill":
            {
                double radius = double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : 50;
                int n = KillNear(radius);
                return n == 0 ? $"No living monster within {radius}." : $"Kill sent for {n} monsters.";
            }
            case "zoe":
                Zoe();
                return "Zoe sent.";
            case "makeitem" or "mi":
            {
                var words = parts.Skip(1).ToList();
                byte plus = 0;
                if (words.Count > 1 && byte.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out byte b))
                {
                    plus = b;
                    words.RemoveAt(words.Count - 1);
                }
                string name = string.Join(' ', words);
                if (FindItem(name) is not { } item)
                    return $"No item matches \"{name}\".";
                _client.Send(ClientPackets.GmMakeItem(item.Id, plus));
                return $"Make {item.Name} ({item.ServerName}){(plus > 0 ? $", {plus}" : "")}.";
            }
            case "recall" or "recalluser":
                return Named(GameMasterCommand.RecallUser, rest, "Recall");
            case "moveto" or "movetouser" or "goto":
                return Named(GameMasterCommand.MoveToUser, rest, "Move to");
            case "totown":
                return Named(GameMasterCommand.ToTown, rest, "Send to town");
            case "gotown":
                _client.SendGameMasterCommand(GameMasterCommand.GoTown);
                return "Go to town.";
            case "invisible":
                _client.SendGameMasterCommand(GameMasterCommand.Invisible);
                return "Invisible on / off.";
            case "invincible":
                _client.SendGameMasterCommand(GameMasterCommand.Invincible);
                return "Invincible on / off.";
            case "worldstatus":
                _client.SendGameMasterCommand(GameMasterCommand.WorldStatus);
                return "World status asked (the answer shows in the game's chat).";
            default:
                return $"Unknown command \"{cmd}\". Type help.";
        }
    }

    private string Named(GameMasterCommand command, string name, string text)
    {
        if (name.Length == 0)
            return $"{text}: type the character name.";
        _client.SendGameMasterCommand(command, name);
        return $"{text} {name}.";
    }

    // ---------------------------------------------------------------- power leveling

    /// <summary>
    /// Keeps <paramref name="keepAlive"/> monsters of one kind alive within <paramref name="radius"/> of the GM, spawning
    /// more as the players kill them. With <paramref name="killAfterSeconds"/> &gt; 0, monsters still alive after that
    /// long are killed with the GM command (the server decides whether anybody gets EXP for those).
    /// </summary>
    public void StartLoop(ModelInfo monster, byte type, int keepAlive, double radius, int killAfterSeconds)
    {
        ArgumentNullException.ThrowIfNull(monster);
        StopLoop();
        var cts = new CancellationTokenSource();
        _loop = cts;
        keepAlive = Math.Clamp(keepAlive, 1, 50);
        _logger.LogInformation("GM: power leveling with {Monster}: keep {Keep} alive within {Radius}", monster.Name, keepAlive, radius);
        _ = Task.Run(async () =>
        {
            int spawned = 0, killed = 0;
            lock (_seen)
                _seen.Clear();
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (!_client.IsInGame || !IsGameMaster)
                        SetStatus("Waiting: not in game as a GM.");
                    else
                    {
                        var now = DateTime.UtcNow;
                        var alive = MobsNear(radius, monster.Id);
                        lock (_seen)
                        {
                            foreach (var gone in _seen.Keys.Except(alive.Select(m => m.UniqueId)).ToList())
                                _seen.Remove(gone);
                            foreach (var m in alive)
                                _seen.TryAdd(m.UniqueId, now);
                            if (killAfterSeconds > 0)
                                foreach (var m in alive.Where(m => now - _seen[m.UniqueId] >= TimeSpan.FromSeconds(killAfterSeconds)))
                                {
                                    _client.Send(ClientPackets.GmKillMob(m.UniqueId));
                                    _seen[m.UniqueId] = now; // try again later if it survives
                                    killed++;
                                }
                        }
                        int missing = keepAlive - alive.Count;
                        if (missing > 0)
                        {
                            _client.Send(ClientPackets.GmCreateMob(monster.Id, (byte)missing, type));
                            spawned += missing;
                        }
                        SetStatus($"{monster.Name}: {alive.Count}/{keepAlive} alive · {spawned} spawned{(killAfterSeconds > 0 ? $" · {killed} killed" : "")}");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        LoopChanged?.Invoke();
    }

    public void StopLoop()
    {
        if (_loop is null)
            return;
        _loop.Cancel();
        _loop.Dispose();
        _loop = null;
        _logger.LogInformation("GM: power leveling stopped");
        SetStatus("Stopped.");
    }

    private void SetStatus(string text)
    {
        LoopStatus = text;
        LoopChanged?.Invoke();
    }

    public void Dispose() => StopLoop();
}
