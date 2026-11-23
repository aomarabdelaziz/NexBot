using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Game;
using NexBot.Media;

namespace NexBot.Navigation;

/// <summary>Where an NPC stands, from the client's <c>npcpos.txt</c> (also when it is out of sight).</summary>
public readonly record struct NpcSpot(uint ModelId, Position Position);

/// <summary>Region walk grids by region id (the client's map data, or a test map).</summary>
public interface IWalkMap
{
    /// <summary>The region's grid, or null where there is none (dungeons).</summary>
    RegionGrid? GetRegion(ushort region);
}

/// <summary>Where one region can be walked: a grid of <see cref="NavMap.CellSize"/> unit cells, true = blocked.</summary>
public sealed class RegionGrid(ushort region, bool[] blocked)
{
    public ushort Region { get; } = region;

    /// <summary>Row by row from the region's south-west corner: index z * <see cref="NavMap.CellsPerRegion"/> + x.</summary>
    internal bool[] Blocked { get; } = blocked;

    public bool IsBlocked(int x, int z) => Blocked[z * NavMap.CellsPerRegion + x];

    /// <summary>A region nobody can walk (no navmesh: sea, outside the map).</summary>
    public static RegionGrid Closed(ushort region) => new(region, Enumerable.Repeat(true, NavMap.CellsPerRegion * NavMap.CellsPerRegion).ToArray());
}

/// <summary>
/// The walkable ground of the open world, read from the client's Data.pk2: each region's terrain navmesh (blocked
/// tiles and cliff edges) plus the walls of the objects standing in it (buildings, town walls, fences, trees, rocks).
/// Region grids are built on first use and kept (a town needs a handful). Dungeons are not covered.
/// </summary>
public sealed class NavMap : IWalkMap, IDisposable
{
    /// <summary>Grid cell size in region units (0.5 world unit; a region is 1920 units).</summary>
    public const int CellSize = 5;
    public const int CellsPerRegion = TerrainMesh.Size / CellSize;
    private const int MaxCachedRegions = 48;

    private readonly Pk2Archive _data;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<ushort, RegionGrid> _regions = [];
    private readonly Dictionary<string, ObjectMesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, string>? _objects;

    public NavMap(Pk2Archive data, ILogger? logger = null, IReadOnlyList<NpcSpot>? npcs = null)
    {
        _data = data;
        _logger = logger ?? NullLogger.Instance;
        Npcs = npcs ?? [];
    }

    /// <summary>Every NPC's place in the world (Media.pk2 <c>npcpos.txt</c>; empty when it could not be read).</summary>
    public IReadOnlyList<NpcSpot> Npcs { get; }

    /// <summary>NPCs standing within <paramref name="range"/> world units (same area: field or dungeon).</summary>
    public IEnumerable<NpcSpot> NpcsNear(Position here, double range) =>
        Npcs.Where(n => n.Position.InDungeon == here.InDungeon && n.Position.DistanceTo(here) <= range);

    /// <summary>
    /// npcpos.txt: one NPC per line, tab separated: model id, region, x, height, z (region units, as on the wire).
    /// </summary>
    public static List<NpcSpot> ReadNpcPositions(string text)
    {
        var spots = new List<NpcSpot>();
        foreach (string line in text.Split('\n'))
        {
            var f = line.Split('\t', StringSplitOptions.TrimEntries);
            if (f.Length < 5
                || !uint.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id)
                || !ushort.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort region)
                || !float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float height)
                || !float.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                continue;
            spots.Add(new NpcSpot(id, new Position(region, (int)Math.Round(x), (int)Math.Round(height), (int)Math.Round(z))));
        }
        return spots;
    }

    /// <summary>Opens Data.pk2 next to the client's Media.pk2 (the client folder or a .pk2 inside it).</summary>
    public static NavMap Open(string clientPath, string? blowfishKey = null, ILogger? logger = null)
    {
        string folder = File.Exists(clientPath) ? Path.GetDirectoryName(Path.GetFullPath(clientPath))! : clientPath;
        string path = Path.Combine(folder, "Data.pk2");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Data.pk2 not found in '{folder}'.");
        List<NpcSpot>? npcs = null;
        string media = Path.Combine(folder, "Media.pk2");
        try
        {
            using var archive = new Pk2Archive(media, blowfishKey);
            npcs = ReadNpcPositions(archive.ReadText("server_dep\\silkroad\\textdata\\npcpos.txt"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger?.LogWarning("NPC positions (npcpos.txt) could not be read: only NPCs in sight are known: {Message}", ex.Message);
        }
        return new NavMap(new Pk2Archive(path, blowfishKey), logger, npcs);
    }

    /// <summary>The region's grid (a closed one when the region has no navmesh), or null for dungeons.</summary>
    public RegionGrid? GetRegion(ushort region)
    {
        if (region > short.MaxValue)
            return null;
        lock (_lock)
        {
            if (_regions.TryGetValue(region, out var cached))
                return cached;
            var grid = Build(region);
            if (_regions.Count >= MaxCachedRegions)
                _regions.Clear();
            _regions[region] = grid;
            return grid;
        }
    }

    private RegionGrid Build(ushort region)
    {
        string path = $"navmesh\\nv_{region:x4}.nvm";
        if (_data.GetFile(path) is not { } file)
            return RegionGrid.Closed(region);
        TerrainMesh terrain;
        try
        {
            terrain = TerrainMesh.Read(_data.ReadBytes(file));
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            _logger.LogWarning("Navmesh {Path} could not be read: {Message}", path, ex.Message);
            return RegionGrid.Closed(region);
        }

        var raster = new Raster();
        // 1. Blocked terrain tiles (20 units = 4 cells each).
        const int per = TerrainMesh.TileSize / CellSize;
        for (int tz = 0; tz < TerrainMesh.Tiles; tz++)
            for (int tx = 0; tx < TerrainMesh.Tiles; tx++)
                if (terrain.IsTileBlocked(tx, tz))
                    raster.FillRect(tx * per, tz * per, per, per, true);

        // 2. Object floors are walkable (bridges over water, stairs); solid objects (walls all round: signs, lamps,
        // trunks) are blocked over their whole footprint. 3. Then every wall.
        var placed = new List<(ObjectMesh Mesh, NavObject Placement)>();
        foreach (var o in terrain.Objects)
            if (MeshOf(o.Id) is { } mesh)
                placed.Add((mesh, o));
        foreach (var (mesh, o) in placed.OrderBy(p => p.Mesh.IsSolid)) // floors first, so a solid object on a floor stays solid
            foreach (var (a, b, c) in mesh.Triangles)
                raster.FillTriangle(Place(o, mesh.Vertices[a]), Place(o, mesh.Vertices[b]), Place(o, mesh.Vertices[c]), mesh.IsSolid);
        foreach (var e in terrain.Edges)
            if (e.IsBlocked)
                raster.Line((e.X0, e.Z0), (e.X1, e.Z1));
        foreach (var (mesh, o) in placed)
            foreach (var e in mesh.Edges)
                if (e.IsBlocked)
                    raster.Line(Place(o, mesh.Vertices[e.V0]), Place(o, mesh.Vertices[e.V1]));
        return new RegionGrid(region, raster.Cells);
    }

    /// <summary>
    /// Marks a strip of ground as blocked for the rest of the session: where the character got stuck although the map
    /// data says it can walk (a counter, a raised floor, an object the data does not describe well). The strip starts
    /// <paramref name="ahead"/> world units in front of <paramref name="from"/> towards <paramref name="towards"/>,
    /// is <paramref name="length"/> long and 2 x <paramref name="halfWidth"/> wide. Returns how many cells it blocked.
    /// </summary>
    public int MarkBlocked(Position from, Position towards, double ahead = 1, double length = 3, double halfWidth = 1)
    {
        if (from.InDungeon || towards.InDungeon)
            return 0;
        double dx = towards.PosX - from.PosX, dy = towards.PosY - from.PosY;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 0.01)
            return 0;
        dx /= d;
        dy /= d;
        int marked = 0;
        var (fx, fz) = PathFinder.CellOf(from);
        const double cellsPerUnit = 10.0 / CellSize;
        lock (_lock)
        {
            const double step = 0.25; // world units: half a cell
            for (double a = ahead; a <= ahead + length; a += step)
                for (double w = -halfWidth; w <= halfWidth; w += step)
                {
                    int gx = (int)Math.Floor(fx + 0.5 + (dx * a - dy * w) * cellsPerUnit);
                    int gz = (int)Math.Floor(fz + 0.5 + (dy * a + dx * w) * cellsPerUnit);
                    if (gx < 0 || gz < 0)
                        continue;
                    ushort region = (ushort)((gz / CellsPerRegion << 8) | (gx / CellsPerRegion));
                    if (!_regions.TryGetValue(region, out var grid))
                        continue; // never built: nothing walked there
                    int index = gz % CellsPerRegion * CellsPerRegion + gx % CellsPerRegion;
                    if (!grid.Blocked[index])
                    {
                        grid.Blocked[index] = true;
                        marked++;
                    }
                }
        }
        return marked;
    }

    /// <summary>An object's vertex in region units: rotated by the object's yaw, then moved to its position.</summary>
    internal static (float X, float Z) Place(NavObject o, (float X, float Y, float Z) v)
    {
        // Counter-clockwise seen from above (checked against the minimap: Jangan's blacksmith, turned a quarter, and
        // the weapon shop sign, turned 63 degrees; the other way round they land on the wrong side).
        double c = Math.Cos(o.Yaw), s = Math.Sin(o.Yaw);
        return ((float)(o.X + v.X * c - v.Z * s), (float)(o.Z + v.X * s + v.Z * c));
    }

    private ObjectMesh? MeshOf(uint id)
    {
        _objects ??= LoadObjectList();
        if (!_objects.TryGetValue(id, out var resource))
            return null;
        if (_meshes.TryGetValue(resource, out var cached))
            return cached;
        ObjectMesh? mesh = null;
        try
        {
            if (_data.GetFile(resource) is { } bsr
                && NavObjectFiles.ReadCollisionPath(_data.ReadBytes(bsr)) is { } collision
                && _data.GetFile(collision) is { } bms)
                mesh = ObjectMesh.Read(_data.ReadBytes(bms));
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            _logger.LogDebug("Object navmesh {Resource} could not be read: {Message}", resource, ex.Message);
        }
        _meshes[resource] = mesh;
        return mesh;
    }

    private Dictionary<uint, string> LoadObjectList()
    {
        try
        {
            return NavObjectFiles.ReadObjectList(_data.ReadText("navmesh\\object.ifo"));
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("navmesh\\object.ifo is missing: buildings and walls are not known");
            return [];
        }
    }

    public void Dispose() => _data.Dispose();

    /// <summary>Draws into one region's cells, in region units (outside parts are cut off).</summary>
    private sealed class Raster
    {
        private const int N = CellsPerRegion;
        public bool[] Cells { get; } = new bool[N * N];

        private void Set(int x, int z, bool value)
        {
            if ((uint)x < N && (uint)z < N)
                Cells[z * N + x] = value;
        }

        public void FillRect(int x, int z, int w, int h, bool value)
        {
            for (int j = z; j < z + h; j++)
                for (int i = x; i < x + w; i++)
                    Set(i, j, value);
        }

        /// <summary>Every cell the segment touches (sampled at a quarter cell, so diagonal walls have no gaps).</summary>
        public void Line((float X, float Z) a, (float X, float Z) b)
        {
            double ax = a.X / CellSize, az = a.Z / CellSize, bx = b.X / CellSize, bz = b.Z / CellSize;
            if (Math.Max(ax, bx) < -1 || Math.Min(ax, bx) > N + 1 || Math.Max(az, bz) < -1 || Math.Min(az, bz) > N + 1)
                return;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(bx - ax), Math.Abs(bz - az)) * 4) + 1;
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                Set((int)Math.Floor(ax + (bx - ax) * t), (int)Math.Floor(az + (bz - az) * t), true);
            }
        }

        /// <summary>Cells whose centre is inside the triangle.</summary>
        public void FillTriangle((float X, float Z) a, (float X, float Z) b, (float X, float Z) c, bool value)
        {
            double ax = a.X / CellSize, az = a.Z / CellSize, bx = b.X / CellSize, bz = b.Z / CellSize, cx = c.X / CellSize, cz = c.Z / CellSize;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(N - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int z0 = Math.Max(0, (int)Math.Floor(Math.Min(az, Math.Min(bz, cz)))), z1 = Math.Min(N - 1, (int)Math.Ceiling(Math.Max(az, Math.Max(bz, cz))));
            double area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            if (Math.Abs(area) < 1e-9)
                return;
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    double px = x + 0.5, pz = z + 0.5;
                    double w0 = ((bx - px) * (cz - pz) - (bz - pz) * (cx - px)) / area;
                    double w1 = ((cx - px) * (az - pz) - (cz - pz) * (ax - px)) / area;
                    if (w0 >= 0 && w1 >= 0 && w0 + w1 <= 1)
                        Set(x, z, value);
                }
        }
    }
}
