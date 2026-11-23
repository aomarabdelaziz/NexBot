using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// Runs the If / Then rules (phBot's Conditions) once a second. A rule fires when all its checks become true, and
/// again every <see cref="ConditionRule.RepeatSeconds"/> while they stay true. Its actions run in order. Actions the
/// app has to do (sound, stop, disconnect) are raised as events.
/// </summary>
public sealed class ConditionEngine : IDisposable
{
    private readonly SroClient _client;
    private readonly ConditionSettings _settings;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<ConditionRule, (bool Held, DateTime Fired)> _state = [];
    private readonly HashSet<ConditionRule> _running = [];
    private (string Text, DateTime At) _lastWhisper;
    private bool _soldOut;

    public ConditionEngine(SroClient client, ConditionSettings settings, ILogger? logger = null, bool startLoop = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullLogger.Instance;
        client.ChatReceived += OnChat;
        client.StallChanged += OnStall;
        if (startLoop)
            _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Play a sound (the .wav, or empty for the Windows sound).</summary>
    public event Action<string>? SoundRequested;
    /// <summary>Stop the running automation (scripts, trade, alchemy).</summary>
    public event Action? StopRequested;
    /// <summary>Stop the bot (disconnect).</summary>
    public event Action? DisconnectRequested;

    private void OnChat(object? sender, ChatEventArgs e)
    {
        if (e.Type == ChatType.Private)
            _lastWhisper = (e.Message, DateTime.UtcNow);
    }

    private void OnStall(object? sender, EventArgs e)
    {
        var stall = _client.Stall;
        if (stall.Exists && stall.IsOpen && stall.Slots.Count == 0)
            _soldOut = true;
        else if (!stall.Exists || stall.Slots.Count > 0)
            _soldOut = stall.Exists && stall.Slots.Count == 0 && _soldOut;
    }

    /// <summary>True when the check holds now.</summary>
    public bool Holds(ConditionIf check)
    {
        if (_client.World.Character is not { } c)
            return false;
        var here = c.EstimatePosition();
        switch (check.Type)
        {
            case IfType.HpBelow:
                return c.HpMax > 0 && c.Hp * 100.0 / c.HpMax < check.Number;
            case IfType.MpBelow:
                return c.MpMax > 0 && c.Mp * 100.0 / c.MpMax < check.Number;
            case IfType.FreeSlotsBelow:
                return FreeSlots(c) < check.Number;
            case IfType.ItemCountBelow:
                return check.Text.Length > 0 && CountOf(c, check.Text) < check.Number;
            case IfType.GoldAbove:
                return c.Gold > (ulong)Math.Max(0, check.Number);
            case IfType.GoldBelow:
                return c.Gold < (ulong)Math.Max(0, check.Number);
            case IfType.LevelAtLeast:
                return c.Level >= check.Number;
            case IfType.Dead:
                return c.HpMax > 0 && c.Hp == 0;
            case IfType.PlayerNear:
                return _client.World.Players.Values.Any(p => p.UniqueId != c.UniqueId
                    && (check.Text.Length == 0 || string.Equals(p.Name, check.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                    && (check.Number <= 0 || p.Position.DistanceTo(here) <= check.Number));
            case IfType.GmNear:
                return _client.World.Players.Values.Any(SoundAlerts.IsGameMaster);
            case IfType.UniqueNear:
                return _client.World.Mobs.Values.Any(m => m.MobType.IsUnique()
                    && (check.Text.Length == 0 || m.Name.Contains(check.Text.Trim(), StringComparison.OrdinalIgnoreCase)));
            case IfType.WhisperReceived:
                return DateTime.UtcNow - _lastWhisper.At < TimeSpan.FromSeconds(3)
                       && (check.Text.Length == 0 || _lastWhisper.Text.Contains(check.Text, StringComparison.OrdinalIgnoreCase));
            case IfType.StallSoldOut:
                return _soldOut;
            case IfType.NotInParty:
                return _client.World.Party is null;
            default:
                return false;
        }
    }

    private static int FreeSlots(Character c)
    {
        int free = 0;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is null)
                free++;
        return free;
    }

    private static int CountOf(Character c, string name)
    {
        int count = 0;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is { } item && item.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
                count += Math.Max(1, (int)item.Quantity);
        return count;
    }

    /// <summary>One pass over the rules (the loop runs it every second; tests call it directly).</summary>
    public async Task StepAsync(CancellationToken token)
    {
        if (!_client.IsInGame || _client.World.Character is null)
            return;
        foreach (var rule in _settings.Rules.ToList())
        {
            if (!rule.Enabled || rule.Ifs.Count == 0 || rule.Thens.Count == 0)
                continue;
            bool holds = rule.Ifs.All(Holds);
            _state.TryGetValue(rule, out var state);
            var now = DateTime.UtcNow;
            bool fire = holds && (!state.Held || rule.RepeatSeconds > 0 && now - state.Fired >= TimeSpan.FromSeconds(rule.RepeatSeconds));
            _state[rule] = (holds, fire ? now : state.Fired);
            if (fire && _running.Add(rule))
            {
                try
                {
                    _logger.LogInformation("Condition \"{Rule}\" met", rule.Name);
                    foreach (var then in rule.Thens.ToList())
                        await DoAsync(then, token).ConfigureAwait(false);
                }
                finally
                {
                    _running.Remove(rule);
                }
            }
        }
        if (_soldOut && !_client.Stall.Exists)
            _soldOut = false;
    }

    private async Task DoAsync(ConditionThen then, CancellationToken token)
    {
        var c = _client.World.Character;
        switch (then.Type)
        {
            case ThenType.Log:
                _logger.LogInformation("Condition: {Message}", then.Text);
                break;
            case ThenType.PlaySound:
                SoundRequested?.Invoke(then.Text);
                break;
            case ThenType.UseItem when c is not null && then.Text.Length > 0:
                for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
                {
                    if (c.Inventory[i] is { } item && item.Name.Contains(then.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        var used = await _client.UseItemAsync((byte)i, cancellationToken: token).ConfigureAwait(false);
                        _logger.LogInformation("Condition: use {Item}: {Result}", item.Name, used.Ok ? "done" : used.Message);
                        break;
                    }
                }
                break;
            case ThenType.ReturnToTown when c is not null:
                for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
                {
                    if (c.Inventory[i] is { } item && AutoWalker.IsReturnScroll(item.ServerName))
                    {
                        await _client.UseItemAsync((byte)i, cancellationToken: token).ConfigureAwait(false);
                        _logger.LogInformation("Condition: return scroll used");
                        break;
                    }
                }
                break;
            case ThenType.Whisper:
            {
                int colon = then.Text.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    string to = then.Text[..colon].Trim(), message = then.Text[(colon + 1)..].Trim();
                    if (to.Length > 0 && message.Length > 0)
                        _client.Send(Network.ClientPackets.Chat(ChatType.Private, message, to));
                }
                break;
            }
            case ThenType.Wait:
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(then.Number, 0, 3600)), token).ConfigureAwait(false);
                break;
            case ThenType.StopBot:
                StopRequested?.Invoke();
                break;
            case ThenType.CloseStall when _client.Stall.Exists:
                await _client.CloseStallAsync(token).ConfigureAwait(false);
                break;
            case ThenType.LeaveParty when _client.World.Party is not null:
                _client.LeaveParty();
                break;
            case ThenType.Disconnect:
                DisconnectRequested?.Invoke();
                break;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                await StepAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
            {
                _logger.LogWarning("Conditions: {Message}", ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _client.ChatReceived -= OnChat;
        _client.StallChanged -= OnStall;
    }
}
