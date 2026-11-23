using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Sessions;

namespace NexBot.Map;

/// <summary>What a marker on the map is.</summary>
public enum MapEntityKind { Player, Mob, Unique, Npc, Pet, Drop, Teleport, PartyMember }

/// <summary>A marker on the map (world coordinates).</summary>
public sealed record MapEntity(uint Id, MapEntityKind Kind, string Name, double X, double Y, string? Extra);

/// <summary>Our character on the map.</summary>
/// <param name="Layer">Dungeon minimap prefix, or null for the open world.</param>
public sealed record MapMe(string Name, double X, double Y, int Z, ushort Region, bool Dungeon, string? Layer, string? RegionName);

/// <summary>Character status shown next to the map.</summary>
public sealed record CharacterStatus(
    string Name, byte Level, uint Hp, uint HpMax, uint Mp, uint MpMax, double ExpPercent, ulong Gold, uint Sp,
    bool IsRiding, bool IsDead);

/// <summary>Everything the map draws, taken at one moment.</summary>
/// <param name="TileSize">World size of one minimap image.</param>
/// <param name="OffsetX">Added to the tile column to get the file name (135 in the open world, 0 in dungeons).</param>
public sealed record MapSnapshot(
    MapMe Me, CharacterStatus Status, double TileSize, int OffsetX, int OffsetY, IReadOnlyList<MapEntity> Entities)
{
    /// <summary>Longest click-to-walk distance (same limit as POC-2).</summary>
    public const double MaxClickWalk = 1500;

    /// <summary>
    /// Builds a snapshot of the world (ported from POC-2's DashboardService). Safe to call from the UI thread:
    /// positions are read with <c>EstimatePosition()</c>, collections are concurrent.
    /// </summary>
    public static MapSnapshot? Take(SroClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.World.Character is not { } c)
            return null;

        var me = (client.World.Mover ?? c).EstimatePosition();
        var world = client.World;
        var entities = new List<MapEntity>();
        static MapEntity Marker(Entity e, Position p, MapEntityKind kind, string? extra = null) =>
            new(e.UniqueId, kind, e.Name, Math.Round(p.PosX, 1), Math.Round(p.PosY, 1), extra);
        bool Near(Position p) => p.Region == me.Region || !me.InDungeon && !p.InDungeon;

        var party = world.Party?.Members.Values.Where(m => !string.Equals(m.Name, c.Name, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        bool InParty(string name) => party.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        foreach (var p in world.Players.Values)
            if (p.UniqueId != c.UniqueId && p.EstimatePosition() is var pos && Near(pos))
                entities.Add(Marker(p, pos, InParty(p.Name) ? MapEntityKind.PartyMember : MapEntityKind.Player,
                    p.GuildName.Length > 0 ? p.GuildName : null));
        // Party members out of sight: the party updates their position.
        foreach (var m in party)
            if (!world.Players.ContainsKey(m.Name) && (m.Position.PosX != 0 || m.Position.PosY != 0) && Near(m.Position))
                entities.Add(new MapEntity(m.Id, MapEntityKind.PartyMember, m.Name, Math.Round(m.Position.PosX, 1), Math.Round(m.Position.PosY, 1),
                    $"Lv. {m.Level}"));
        foreach (var m in world.Mobs.Values)
            if (m.EstimatePosition() is var pos && Near(pos))
                entities.Add(Marker(m, pos, m.MobType.IsUnique() ? MapEntityKind.Unique : MapEntityKind.Mob, m.MobType.ToString()));
        foreach (var n in world.Npcs.Values)
            if (n is not Mob && n.EstimatePosition() is var pos && Near(pos))
                entities.Add(Marker(n, pos, n is Cos ? MapEntityKind.Pet : MapEntityKind.Npc));
        foreach (var d in world.Drops.Values)
            if (Near(d.Position))
                entities.Add(Marker(d, d.Position, MapEntityKind.Drop));
        foreach (var t in world.Teleports.Values)
            if (Near(t.Position))
                entities.Add(Marker(t, t.Position, MapEntityKind.Teleport));

        var status = new CharacterStatus(c.Name, c.Level, c.Hp, c.HpMax, c.Mp, c.MpMax, c.ExpPercent, c.Gold, c.Sp,
            c.IsRiding, c.Hp == 0 && c.HpMax > 0);

        return new MapSnapshot(
            new MapMe(c.Name, Math.Round(me.PosX, 1), Math.Round(me.PosY, 1), me.Z, me.Region, me.InDungeon,
                MinimapLayer.For(me), client.Data.GetRegionName(me.Region)),
            status,
            MinimapLayer.TileSize,
            me.InDungeon ? 0 : 135,
            me.InDungeon ? 0 : 92,
            entities);
    }

    /// <summary>
    /// Walks to a world point (click on the map). While riding, the mount moves. Returns an error message or null.
    /// </summary>
    public static string? WalkTo(SroClient client, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.World.Character is not { } c || !client.IsInGame)
            return "The character is not in game.";
        var here = c.EstimatePosition();
        if (!double.IsFinite(x) || !double.IsFinite(y) || here.DistanceTo(x, y) > MaxClickWalk)
            return $"Choose a point within {MaxClickWalk} units.";
        var target = here.InDungeon ? Position.FromWorld(x, y, here.Region, here.Z) : new Position(x, y);
        bool sent = c.IsRiding ? client.MovePetTo(c.RidingUniqueId, target) : client.MoveTo(target);
        return sent ? null : "The bot is not connected.";
    }
}
