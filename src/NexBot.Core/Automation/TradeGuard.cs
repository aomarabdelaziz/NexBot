using Microsoft.Extensions.Logging;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>
/// The trader's road rules, checked by the walker between steps (phBot's Trade options):
/// <list type="bullet">
/// <item><b>Attack spawned thieves</b> (MOB_THIEF_NPC_*) within <see cref="TradeSettings.AttackRadius"/> of the
/// transport; only when not staying on the transport (a rider cannot attack).</item>
/// <item><b>Stay off transport</b>: get off and walk next to it. <b>Remount transport</b>: get off to fight, back on
/// when no thief is near. <b>Stay on transport</b>: never fight.</item>
/// </list>
/// </summary>
public sealed class TradeGuard(SroClient client, TradeSettings settings, ILogger logger)
{
    /// <summary>How long one thief may take before the next one is tried.</summary>
    public TimeSpan FightLimit { get; set; } = TimeSpan.FromSeconds(40);

    /// <summary>How often the attack is sent again while the thief lives.</summary>
    public TimeSpan AttackInterval { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>A thief that spawned to rob traders.</summary>
    public static bool IsThief(Mob mob) => mob.ServerName.StartsWith("MOB_THIEF_NPC", StringComparison.OrdinalIgnoreCase);

    /// <summary>Living thieves within the attack radius of the transport (or the character), nearest first.</summary>
    public IReadOnlyList<Mob> ThievesNear()
    {
        Model? around = client.Transport ?? (Model?)client.World.Character;
        if (around is null)
            return [];
        var centre = around.EstimatePosition();
        return [.. client.World.Mobs.Values
            .Where(mob => IsThief(mob) && mob.LifeState != LifeState.Dead && mob.Position.InDungeon == centre.InDungeon)
            .Select(mob => (Mob: mob, Distance: mob.EstimatePosition().DistanceTo(centre)))
            .Where(x => x.Distance <= settings.AttackRadius)
            .OrderBy(x => x.Distance)
            .Select(x => x.Mob)];
    }

    /// <summary>
    /// Applies the road rules once. True when it did something (fought, got on or off), so the walker re-sends its move.
    /// </summary>
    public async Task<bool> CheckAsync(CancellationToken token)
    {
        if (client.World.Character is not { } character || !client.IsInGame)
            return false;
        var mode = settings.MountMode;
        bool acted = false;

        if (settings.AttackThieves && mode != TradeMountMode.StayOn && ThievesNear() is { Count: > 0 })
        {
            if (character.IsRiding)
                await client.DismountAsync(token).ConfigureAwait(false);
            await FightAsync(token).ConfigureAwait(false);
            acted = true;
        }

        if (mode == TradeMountMode.StayOff && character.IsRiding)
        {
            await client.DismountAsync(token).ConfigureAwait(false);
            acted = true;
        }
        else if (mode == TradeMountMode.Remount && !character.IsRiding && client.Transport is not null && ThievesNear().Count == 0)
        {
            var on = await client.RideMountAsync(-1, token).ConfigureAwait(false);
            if (!on.Ok)
                logger.LogWarning("Trade: could not get back on the transport: {Message}", on.Message);
            acted = true;
        }
        return acted;
    }

    /// <summary>Attacks the thieves near the transport one by one until none is left.</summary>
    private async Task FightAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && ThievesNear() is { Count: > 0 } thieves)
        {
            var thief = thieves[0];
            logger.LogInformation("Trade: attacking {Thief} near the transport", thief.Name);
            var until = DateTime.UtcNow + FightLimit;
            while (DateTime.UtcNow < until && client.World.Mobs.TryGetValue(thief.UniqueId, out var alive) && alive.LifeState != LifeState.Dead)
            {
                client.Attack(thief.UniqueId);
                await Task.Delay(AttackInterval, token).ConfigureAwait(false);
            }
            if (client.World.Mobs.TryGetValue(thief.UniqueId, out var left) && left.LifeState != LifeState.Dead)
            {
                logger.LogWarning("Trade: could not kill {Thief}; going on", thief.Name);
                return;
            }
        }
    }
}
