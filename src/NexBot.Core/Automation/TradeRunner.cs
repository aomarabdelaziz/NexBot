using Microsoft.Extensions.Logging;
using NexBot.Configuration;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// Auto trade (phBot's Trade tab): runs the trade loops in order (each a script recorded on the Script page, e.g.
/// Jangan → Donwhang with RIDE / BUY / SELL), optionally a return scroll after the last one, and repeats them
/// <see cref="TradeSettings.LoopCount"/> times (0 = until stopped). The road rules (<see cref="TradeGuard"/>) are
/// checked while walking.
/// </summary>
public sealed class TradeRunner : IDisposable
{
    private readonly SroClient _client;
    private readonly TradeSettings _settings;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;

    public TradeRunner(SroClient client, TradeSettings settings, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Guard = new TradeGuard(client, settings, _logger);
    }

    public TradeGuard Guard { get; }

    public bool IsRunning { get; private set; }

    public string Status { get; private set; } = "Stopped.";

    /// <summary>The walker of the loop being run (for its line and status), or null.</summary>
    public AutoWalker? Current { get; private set; }

    /// <summary>Raised (on a background thread) when <see cref="Status"/> or <see cref="IsRunning"/> changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Pause between walker steps (tests make it short).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Starts auto trade. Null when it started, else why not.</summary>
    public string? Start()
    {
        if (IsRunning)
            return "Auto trade is already running.";
        if (_settings.Loops.Count == 0)
            return "Add at least one trade loop (a script recorded on the Script page).";
        if (_settings.Loops.FirstOrDefault(l => !File.Exists(l)) is { } missing)
            return $"The loop script {Path.GetFileName(missing)} is missing.";
        if (!_client.IsInGame || _client.World.Character is null)
            return "The character is not in game.";
        CancellationToken token;
        lock (_lock)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }
        IsRunning = true;
        _ = Task.Run(() => RunAsync(token), CancellationToken.None);
        return null;
    }

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

    /// <summary>Runs the loops (rounds × loops). True when every round finished.</summary>
    public async Task<bool> RunAsync(CancellationToken token)
    {
        IsRunning = true;
        try
        {
            int rounds = !_settings.RepeatLoops ? 1 : _settings.LoopCount <= 0 ? int.MaxValue : _settings.LoopCount;
            for (int round = 1; round <= rounds; round++)
            {
                string of = rounds == int.MaxValue ? "" : $" of {rounds}";
                for (int i = 0; i < _settings.Loops.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string path = _settings.Loops[i];
                    string name = Path.GetFileNameWithoutExtension(path);
                    SetStatus($"Round {round}{of} · loop {i + 1}/{_settings.Loops.Count}: {name}");
                    _logger.LogInformation("Auto trade: round {Round}, loop {Loop}", round, name);
                    if (!await WalkAsync(WalkScript.Load(path), name, round, of, token).ConfigureAwait(false))
                        return false;
                }
                if (_settings.ReturnAfterLoops)
                {
                    SetStatus($"Round {round}{of} · using a return scroll");
                    if (!await WalkAsync(WalkScript.Parse("RETURN", "return"), "return scroll", round, of, token).ConfigureAwait(false))
                        return false;
                }
            }
            SetStatus("Auto trade finished.");
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Auto trade stopped.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Auto trade stopped: " + ex.Message);
            return false;
        }
        finally
        {
            Current = null;
            IsRunning = false;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<bool> WalkAsync(WalkScript script, string name, int round, string of, CancellationToken token)
    {
        using var walker = new AutoWalker(_client, script, new AutoWalkOptions
        {
            Loop = false,
            Trade = _settings,
            Guard = Guard.CheckAsync,
            PollInterval = PollInterval,
        }, _logger);
        Current = walker;
        walker.StatusChanged += (_, _) => SetStatus($"Round {round}{of} · {name}: {walker.Status}");
        bool finished = await walker.WalkAsync(token).ConfigureAwait(false);
        Current = null;
        if (!finished)
        {
            token.ThrowIfCancellationRequested();
            SetStatus($"Auto trade stopped in {name}: {walker.Status}");
            _logger.LogWarning("Auto trade stopped in {Loop}: {Status}", name, walker.Status);
        }
        return finished;
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
