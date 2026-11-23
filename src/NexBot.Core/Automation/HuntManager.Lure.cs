using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Network;

namespace NexBot.Automation;

/// <summary>
/// Luring (phBot's Lure tab): walk out of the middle of the area (a random way, or along a lure script), cast the lure
/// skill (Howling Shout ...) at the edge, wait, come back halfway, wait, back to the middle, wait; then again. Monsters
/// that follow are attacked as usual (unless "Don't attack monsters" is on).
/// </summary>
public sealed partial class HuntManager
{
    private enum LureState { Idle, Out, Edge, Half, Back, Center }

    private LureState _lure;
    private DateTime _lureUntil, _lureSent;
    private Position _lureGoal;
    private List<Position> _lurePoints = [];
    private int _lurePoint;
    private string? _lureStopped;
    private string _lureScriptLoaded = "";

    /// <summary>One step of luring; false when luring is off or stopped (the caller walks around instead).</summary>
    private bool LureStep(Character c, List<Skill> skills, byte weapon, Position here, DateTime now)
    {
        if (!_settings.LureEnabled || !_area.IsSet)
            return false;
        var (cx, cy) = _area.Center;
        string? stop = LureStopReason(cx, cy);
        if (stop is not null)
        {
            if (_lureStopped != stop)
                _logger.LogInformation("Attack: luring stops: {Why}", stop);
            _lureStopped = stop;
            _lure = LureState.Idle;
            return false;
        }
        if (_lureStopped is not null)
            _logger.LogInformation("Attack: luring goes on");
        _lureStopped = null;
        var center = Here(cx, cy);
        switch (_lure)
        {
            case LureState.Idle:
                LoadLureScript();
                _lurePoint = 0;
                if (_lurePoints.Count > 0)
                    _lureGoal = _lurePoints[0];
                else
                {
                    double angle = _random.NextDouble() * Math.PI * 2, length = _settings.LureWalkDistance > 0 ? _settings.LureWalkDistance : _area.Radius;
                    _lureGoal = Here(cx + Math.Cos(angle) * length, cy + Math.Sin(angle) * length);
                }
                Go(c, _lureGoal, LureState.Out, now, TimeSpan.FromSeconds(20));
                Status = "Luring: walking out.";
                return true;
            case LureState.Out:
                if (!Arrived(c, here, _lureGoal, now))
                {
                    Status = "Luring: walking out.";
                    return true;
                }
                if (_lurePoints.Count > 0 && ++_lurePoint < _lurePoints.Count)
                {
                    _lureGoal = _lurePoints[_lurePoint];
                    Go(c, _lureGoal, LureState.Out, now, TimeSpan.FromSeconds(20));
                    return true;
                }
                if (_settings.LureSkill.Length > 0 && AttackPlan.Learned(skills, _settings.LureSkill) is { } shout && AttackPlan.Ready(shout, c.Mp, weapon))
                    CastSelf(c, shout, "lure skill", now);
                (_lure, _lureUntil) = (LureState.Edge, now + TimeSpan.FromMilliseconds(_settings.LureEdgeDelay));
                Status = "Luring: at the edge.";
                return true;
            case LureState.Edge:
                if (now < _lureUntil)
                    return true;
                _lureGoal = Here((here.PosX + cx) / 2, (here.PosY + cy) / 2);
                Go(c, _lureGoal, LureState.Half, now, TimeSpan.FromSeconds(15));
                Status = "Luring: coming back.";
                return true;
            case LureState.Half:
                if (!Arrived(c, here, _lureGoal, now))
                    return true;
                (_lure, _lureUntil) = (LureState.Back, now + TimeSpan.FromMilliseconds(_settings.LureHalfDelay));
                return true;
            case LureState.Back:
                if (now < _lureUntil)
                    return true;
                if (_lureGoal != center)
                {
                    _lureGoal = center;
                    Go(c, center, LureState.Back, now, TimeSpan.FromSeconds(15));
                    _lureUntil = DateTime.MinValue;
                    return true;
                }
                if (!Arrived(c, here, center, now))
                    return true;
                (_lure, _lureUntil) = (LureState.Center, now + TimeSpan.FromMilliseconds(_settings.LureCenterDelay));
                Status = "Luring: waiting in the middle.";
                return true;
            case LureState.Center:
                if (now >= _lureUntil)
                    _lure = LureState.Idle;
                Status = "Luring: waiting in the middle.";
                return true;
        }
        return false;
    }

    private Position Here(double x, double y) =>
        Position.IsDungeonRegion(_area.Region) ? Position.FromWorld(x, y, _area.Region) : new Position(x, y);

    private void Go(Character c, Position to, LureState next, DateTime now, TimeSpan timeout)
    {
        Send(ClientPackets.MoveTo(to, c.IsRiding ? c.RidingUniqueId : 0));
        (_lure, _lureSent, _lureUntil) = (next, now, now + timeout);
    }

    /// <summary>Arrived (or gave up after the timeout); the move is sent again every 3 seconds.</summary>
    private bool Arrived(Character c, Position here, Position goal, DateTime now)
    {
        if (here.DistanceTo(goal) <= 3 || (_lureUntil != DateTime.MinValue && now > _lureUntil))
            return true;
        if (now - _lureSent > TimeSpan.FromSeconds(3))
        {
            Send(ClientPackets.MoveTo(goal, c.IsRiding ? c.RidingUniqueId : 0));
            _lureSent = now;
        }
        return false;
    }

    private void LoadLureScript()
    {
        string path = _settings.LureScript;
        if (path == _lureScriptLoaded)
            return;
        _lureScriptLoaded = path;
        _lurePoints = [];
        if (path.Length == 0)
            return;
        try
        {
            _lurePoints = [.. WalkScript.Load(path).Commands.Where(m => m.Type == WalkCommandType.Move).Select(m => m.Position)];
            _logger.LogInformation("Attack: lure script {Script}: {Points} points", Path.GetFileName(path), _lurePoints.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _logger.LogWarning("Attack: the lure script {Script} cannot be read: {Message}", path, ex.Message);
        }
    }

    private string? LureStopReason(double cx, double cy)
    {
        var party = _client.World.Party?.Members.Values.ToList() ?? [];
        int dead = party.Count(m => _client.World.Players.TryGetValue(m.Name, out var p) ? p.LifeState == LifeState.Dead : m.HpPercent == 0);
        var inArea = _client.World.Mobs.Values.Where(m => m.LifeState != LifeState.Dead && _area.Contains(m.Position.PosX, m.Position.PosY)).ToList();
        int giants = inArea.Count(m => AttackPlan.TypeOf(m.MobType) == "Party Giant");
        int champions = inArea.Count(m => AttackPlan.TypeOf(m.MobType) == "Party Champion");
        bool away = party.Where(m => m.Position != default).Any(m => m.Position.DistanceTo(cx, cy) > Math.Max(_area.Radius, 50) + 100);
        return AttackPlan.LureStop(_settings, dead, giants, champions, inArea.Count, away);
    }
}
