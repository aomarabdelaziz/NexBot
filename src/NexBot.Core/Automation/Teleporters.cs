using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Sessions;

namespace NexBot.Automation;

/// <summary>Something the character can teleport with, and where it leads.</summary>
/// <param name="Entity">The spawned gate or NPC.</param>
/// <param name="Name">Readable name ("Dimensional Gate", "Soldier Choiyoung").</param>
public sealed record TeleporterInfo(Entity Entity, string Name, IReadOnlyList<TeleportLinkInfo> Links)
{
    /// <summary>The destination's readable name, or "#id".</summary>
    public string DestinationName(uint destinationId) =>
        Links.FirstOrDefault(l => l.DestinationId == destinationId) is { } link ? Clean(link.DestinationName) : $"#{destinationId}";

    internal static string Clean(string name) => name.Replace("[Teleport]", "", StringComparison.OrdinalIgnoreCase).Trim();
}

/// <summary>
/// Teleporters come in two kinds: buildings (dimensional gates between towns, spawned as teleports) and NPCs (the
/// soldiers that move you across a town, ferry / boat / airship ticket sellers, tunnel managers, guides), spawned as
/// NPCs with teleport links on their model. Both are used the same way (0x705A with a destination).
/// </summary>
public static class Teleporters
{
    /// <summary>The teleporter with this unique ID (gate or teleport NPC), or null.</summary>
    public static TeleporterInfo? Find(SroClient client, uint uniqueId)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.World.Teleports.TryGetValue(uniqueId, out var gate))
            return new TeleporterInfo(gate, gate.TeleportName, gate.Links);
        if (client.World.GetEntity(uniqueId) is Npc npc && client.Data.GetTeleportLinks(npc.RefId) is { Count: > 0 } links)
            return new TeleporterInfo(npc, TeleporterInfo.Clean(links[0].SourceName.Length > 0 ? links[0].SourceName : npc.Name), links);
        return null;
    }

    /// <summary>The nearest spawned teleporter with this server name within <paramref name="radius"/>, or null.</summary>
    public static Entity? Nearest(SroClient client, string serverName, Position here, double radius)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.World.Teleports.Values.Cast<Entity>()
            .Concat(client.World.Npcs.Values)
            .Where(e => string.Equals(e.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
            .Select(e => (Entity: e, Distance: e.Position.DistanceTo(here)))
            .Where(x => x.Distance < radius)
            .OrderBy(x => x.Distance)
            .Select(x => x.Entity)
            .FirstOrDefault();
    }
}
