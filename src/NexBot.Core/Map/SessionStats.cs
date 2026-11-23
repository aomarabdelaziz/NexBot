using System.Collections.Concurrent;
using NexBot.Sessions;

namespace NexBot.Map;

/// <summary>Numbers for the map's statistics box.</summary>
public sealed record StatsSnapshot(
    TimeSpan Time, ulong ExpGained, double ExpPerHour, long GoldChange, double GoldPerHour,
    int Kills, double KillsPerHour, int Deaths, int ItemsPicked, int Uniques);

/// <summary>
/// Counts what happened since the session started (phBot's general statistics on the map): experience and gold
/// gained, monsters we killed, deaths, items picked up. Experience and gold are read from the character on
/// <see cref="Sample"/>, which the map calls on every refresh.
/// </summary>
public sealed class SessionStats : IDisposable
{
    private readonly SroClient _client;
    private readonly ConcurrentDictionary<uint, DateTime> _hit = new();
    private readonly object _gate = new();
    private DateTime _started = DateTime.UtcNow;
    private bool _haveBase;
    private byte _level;
    private ulong _exp, _expMax, _gold;
    private ulong _expGained;
    private long _goldChange;
    private int _kills, _deaths, _picked, _uniques;

    public SessionStats(SroClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        client.DamageDealt += OnDamage;
        client.MobDied += OnMobDied;
        client.CharacterDied += OnDied;
        client.ItemPicked += OnPicked;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _started = DateTime.UtcNow;
            _haveBase = false;
            _expGained = 0;
            _goldChange = 0;
            _kills = _deaths = _picked = _uniques = 0;
            _hit.Clear();
        }
    }

    private void OnDamage(object? sender, DamageEventArgs e)
    {
        if (_client.World.Character is { } me && e.SourceId == me.UniqueId)
            _hit[e.TargetId] = DateTime.UtcNow;
    }

    private void OnMobDied(object? sender, EntityEventArgs e)
    {
        // Our kill when we hit it in the last minute.
        if (!_hit.TryRemove(e.Entity.UniqueId, out var at) || DateTime.UtcNow - at > TimeSpan.FromMinutes(1))
            return;
        lock (_gate)
        {
            _kills++;
            if (e.Entity is Game.Entities.Mob mob && Game.MobTypeExtensions.IsUnique(mob.MobType))
                _uniques++;
        }
    }

    private void OnDied(object? sender, CharacterDiedEventArgs e)
    {
        lock (_gate)
            _deaths++;
    }

    private void OnPicked(object? sender, ItemPickedEventArgs e)
    {
        lock (_gate)
            _picked++;
    }

    /// <summary>Reads experience and gold from the character and returns the totals.</summary>
    public StatsSnapshot Sample()
    {
        lock (_gate)
        {
            if (_client.World.Character is { } c)
            {
                if (!_haveBase)
                {
                    (_level, _exp, _expMax, _gold, _haveBase) = (c.Level, c.Exp, c.ExpMax, c.Gold, true);
                }
                else
                {
                    if (c.Level == _level && c.Exp > _exp)
                        _expGained += c.Exp - _exp;
                    else if (c.Level > _level)
                        _expGained += (_expMax > _exp ? _expMax - _exp : 0) + c.Exp; // one level up (more is rare)
                    _goldChange += (long)c.Gold - (long)_gold;
                    (_level, _exp, _expMax, _gold) = (c.Level, c.Exp, c.ExpMax, c.Gold);
                }
            }
            var time = DateTime.UtcNow - _started;
            double hours = Math.Max(time.TotalHours, 1 / 60.0); // rates settle after the first minute
            return new StatsSnapshot(time, _expGained, _expGained / hours, _goldChange, _goldChange / hours,
                _kills, _kills / hours, _deaths, _picked, _uniques);
        }
    }

    public void Dispose()
    {
        _client.DamageDealt -= OnDamage;
        _client.MobDied -= OnMobDied;
        _client.CharacterDied -= OnDied;
        _client.ItemPicked -= OnPicked;
    }
}
