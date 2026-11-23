using NexBot.Game;

namespace NexBot.Navigation;

/// <summary>
/// Finds a walking route between two open-world positions over the <see cref="NavMap"/> grid: A* on a window around
/// both ends (8 directions, no cutting past wall corners, a little extra cost next to walls so routes keep off them),
/// then straightened into as few legs as line of sight allows.
/// </summary>
public static class PathFinder
{
    private const int N = NavMap.CellsPerRegion;
    /// <summary>
    /// Extra room around the two ends, in cells (40, 120 and 250 world units): a wider window is tried when the goal
    /// cannot be reached in a narrower one (the way round a long wall to its gate).
    /// </summary>
    private static readonly int[] Margins = [80, 240, 500];
    /// <summary>Largest window searched (cells): about 3 x 3 regions.</summary>
    public const int MaxWindowCells = 1200 * 1200;
    /// <summary>How far a blocked start or goal is moved to the nearest free cell (cells: 8 world units).</summary>
    private const int SnapRadius = 16;
    /// <summary>
    /// A start that reaches fewer cells than this is in a closed pocket (inside a fountain rim, behind a counter: the
    /// map is not exact there) and steps out of it first.
    /// </summary>
    private const int PocketCells = 1500;
    /// <summary>How far from an unreachable goal the route may end (cells: 25 world units).</summary>
    private const int GoalRadius = 50;
    /// <summary>
    /// Room kept from every wall, in cells (1 world unit): the game stops a character brushing past a wall closer
    /// than that. Only when no route keeps it is a tight route searched.
    /// </summary>
    private const int Clearance = 2;

    /// <summary>
    /// The route as positions to walk to, in order (the last one is the goal or the free spot nearest it); empty when
    /// already there; null when no route exists, the ends are too far apart or in a dungeon.
    /// </summary>
    public static List<Position>? Find(IWalkMap map, Position from, Position to)
    {
        if (from.InDungeon || to.InDungeon)
            return null;
        // The goal itself with room from walls, then tightly; only then the nearest reachable spot to it.
        return Find(map, from, to, Clearance, false) ?? Find(map, from, to, 0, false)
            ?? Find(map, from, to, Clearance, true) ?? Find(map, from, to, 0, true);
    }

    /// <summary>
    /// A straight walk from <paramref name="from"/> to <paramref name="to"/> keeps room from every wall on the map:
    /// the walker may turn towards the next point of a route early only then (otherwise it cuts the corner).
    /// </summary>
    public static bool IsClear(IWalkMap map, Position from, Position to)
    {
        if (from.InDungeon || to.InDungeon)
            return false;
        var (ax, az) = CellOf(from);
        var (bx, bz) = CellOf(to);
        const int pad = Clearance + 4;
        int x0 = Math.Min(ax, bx) - pad, z0 = Math.Min(az, bz) - pad;
        int w = Math.Abs(ax - bx) + 2 * pad + 1, h = Math.Abs(az - bz) + 2 * pad + 1;
        if ((long)w * h > MaxWindowCells)
            return false;
        var window = new Window(map, x0, z0, w, h, Clearance);
        int a = window.Index(ax - x0, az - z0), b = window.Index(bx - x0, bz - z0);
        return window.IsFree(a) && window.Clear(a, b);
    }

    /// <summary>The ground at <paramref name="p"/> is walkable on the map (no room from walls asked).</summary>
    public static bool IsOpen(IWalkMap map, Position p)
    {
        if (p.InDungeon)
            return false;
        var (x, z) = CellOf(p);
        return map.GetRegion((ushort)((z / N << 8) | (x / N))) is { } grid && !grid.IsBlocked(x % N, z % N);
    }

    private static List<Position>? Find(IWalkMap map, Position from, Position to, int clearance, bool orNearest)
    {
        var (sx, sz) = CellOf(from);
        var (gx, gz) = CellOf(to);
        Window? last = null;
        int lastStart = -1;
        bool[]? lastReach = null;
        foreach (int margin in Margins)
        {
            int x0 = Math.Min(sx, gx) - margin, z0 = Math.Min(sz, gz) - margin;
            int w = Math.Abs(sx - gx) + 2 * margin + 1, h = Math.Abs(sz - gz) + 2 * margin + 1;
            if ((long)w * h > MaxWindowCells)
                break;
            var window = new Window(map, x0, z0, w, h, clearance);
            if (window.Snap(sx - x0, sz - z0) is not { } start)
                return null;
            var reach = window.Reach(start, out int reached);
            if (reached < PocketCells)
            {
                if (window.Escape(start, reach) is not { } outside)
                    return null;
                start = outside;
                reach = window.Reach(start, out _);
            }
            int goal = window.Index(gx - x0, gz - z0);
            if (reach[goal])
                return Route(window, start, goal);
            (last, lastStart, lastReach) = (window, start, reach);
        }
        // The goal itself cannot be reached (inside a building, behind a counter): end at the nearest spot that can.
        if (!orNearest || last is null || last.Nearest(gx - last.X0, gz - last.Z0, lastReach!, GoalRadius) is not { } near)
            return null;
        return Route(last, lastStart, near);
    }

    private static List<Position>? Route(Window window, int start, int goal)
    {
        if (start == goal)
            return [];
        if (window.Search(start, goal) is not { } cells)
            return null;
        var route = new List<Position>();
        foreach (int c in window.Straighten(cells).Skip(1))
            route.Add(PositionOf(window.X0 + c % window.Width, window.Z0 + c / window.Width));
        return route;
    }

    /// <summary>Global cell of a position: regions side by side, <see cref="N"/> cells each.</summary>
    internal static (int X, int Z) CellOf(Position p) =>
        (p.XSector * N + Math.Clamp(p.X, 0, TerrainMesh.Size - 1) / NavMap.CellSize,
         p.YSector * N + Math.Clamp(p.Y, 0, TerrainMesh.Size - 1) / NavMap.CellSize);

    internal static Position PositionOf(int x, int z)
    {
        int rx = x / N, rz = z / N;
        return new Position((ushort)((rz << 8) | rx), x % N * NavMap.CellSize + NavMap.CellSize / 2, 0, z % N * NavMap.CellSize + NavMap.CellSize / 2);
    }

    /// <summary>The blocked cells of a rectangle of the world, copied from the region grids.</summary>
    private sealed class Window
    {
        private static readonly (int Dx, int Dz, float Cost)[] Steps =
        [
            (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
            (1, 1, 1.4142f), (1, -1, 1.4142f), (-1, 1, 1.4142f), (-1, -1, 1.4142f),
        ];

        private readonly int _w, _h;
        private readonly bool[] _blocked;
        private readonly byte[] _nearWall;

        /// <summary>The window's south-west corner (global cells) and width.</summary>
        public int X0 { get; }
        public int Z0 { get; }
        public int Width => _w;

        /// <summary>A cell index; outside cells are clamped to the border.</summary>
        public int Index(int x, int z) => Math.Clamp(z, 0, _h - 1) * _w + Math.Clamp(x, 0, _w - 1);

        public Window(IWalkMap map, int x0, int z0, int w, int h, int clearance = 0)
        {
            X0 = x0;
            Z0 = z0;
            _w = w;
            _h = h;
            _blocked = new bool[w * h];
            var regions = new Dictionary<int, RegionGrid?>();
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    int gx = x0 + x, gz = z0 + z;
                    if (gx < 0 || gz < 0 || gx >= 256 * N || gz >= 128 * N)
                    {
                        _blocked[z * w + x] = true;
                        continue;
                    }
                    int key = (gz / N << 8) | (gx / N);
                    if (!regions.TryGetValue(key, out var grid))
                        regions[key] = grid = map.GetRegion((ushort)key);
                    _blocked[z * w + x] = grid is null || grid.IsBlocked(gx % N, gz % N);
                }
            if (clearance > 0)
                Inflate(clearance);
            // Cells within two cells of a wall cost more, so routes keep a little room from walls.
            _nearWall = new byte[w * h];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    if (!_blocked[z * w + x])
                        continue;
                    for (int dz = -2; dz <= 2; dz++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if ((uint)nx < (uint)w && (uint)nz < (uint)h)
                                _nearWall[nz * w + nx] = (byte)Math.Max(_nearWall[nz * w + nx], Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1 ? 2 : 1);
                        }
                }
        }

        /// <summary>Blocks every cell within <paramref name="radius"/> cells of a blocked one.</summary>
        private void Inflate(int radius)
        {
            var grown = (bool[])_blocked.Clone();
            for (int z = 0; z < _h; z++)
                for (int x = 0; x < _w; x++)
                {
                    if (!_blocked[z * _w + x])
                        continue;
                    for (int dz = -radius; dz <= radius; dz++)
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (dx * dx + dz * dz <= radius * radius + radius && (uint)nx < (uint)_w && (uint)nz < (uint)_h)
                                grown[nz * _w + nx] = true;
                        }
                }
            Array.Copy(grown, _blocked, grown.Length);
        }

        public bool IsFree(int cell) => !_blocked[cell];

        private bool Free(int x, int z) => (uint)x < (uint)_w && (uint)z < (uint)_h && !_blocked[z * _w + x];

        /// <summary>The cell, or the nearest free one within <see cref="SnapRadius"/>.</summary>
        public int? Snap(int x, int z)
        {
            if (Free(x, z))
                return z * _w + x;
            for (int r = 1; r <= SnapRadius; r++)
            {
                int best = -1, bestDistance = int.MaxValue;
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r || !Free(x + dx, z + dz))
                            continue;
                        int d = dx * dx + dz * dz;
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            best = (z + dz) * _w + x + dx;
                        }
                    }
                if (best >= 0)
                    return best;
            }
            return null;
        }

        /// <summary>Every cell walkable from <paramref name="start"/>.</summary>
        public bool[] Reach(int start, out int count)
        {
            var reach = new bool[_w * _h];
            var queue = new Queue<int>();
            reach[start] = true;
            queue.Enqueue(start);
            count = 0;
            while (queue.TryDequeue(out int cell))
            {
                count++;
                int x = cell % _w, z = cell / _w;
                foreach (var (dx, dz, _) in Steps)
                {
                    int nx = x + dx, nz = z + dz;
                    if (!Free(nx, nz) || dx != 0 && dz != 0 && (!Free(x + dx, z) || !Free(x, z + dz)))
                        continue;
                    int next = nz * _w + nx;
                    if (reach[next])
                        continue;
                    reach[next] = true;
                    queue.Enqueue(next);
                }
            }
            return reach;
        }

        /// <summary>The nearest free cell outside a closed pocket (within <see cref="SnapRadius"/> x 2).</summary>
        public int? Escape(int start, bool[] pocket)
        {
            int x = start % _w, z = start / _w;
            for (int r = 1; r <= 2 * SnapRadius; r++)
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r || !Free(x + dx, z + dz))
                            continue;
                        int cell = (z + dz) * _w + x + dx;
                        if (!pocket[cell])
                            return cell;
                    }
            return null;
        }

        /// <summary>The reached cell nearest to (x, z), within <paramref name="radius"/> cells.</summary>
        public int? Nearest(int x, int z, bool[] reach, int radius)
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if ((uint)nx >= (uint)_w || (uint)nz >= (uint)_h || !reach[nz * _w + nx])
                        continue;
                    double d = dx * dx + dz * dz;
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = nz * _w + nx;
                    }
                }
            return best >= 0 ? best : null;
        }

        public List<int>? Search(int start, int goal)
        {
            int count = _w * _h;
            var cost = new float[count];
            Array.Fill(cost, float.MaxValue);
            var parent = new int[count];
            var closed = new bool[count];
            var open = new PriorityQueue<int, float>();
            int gx = goal % _w, gz = goal / _w;
            cost[start] = 0;
            parent[start] = -1;
            open.Enqueue(start, Heuristic(start % _w, start / _w, gx, gz));
            while (open.TryDequeue(out int cell, out _))
            {
                if (cell == goal)
                    return Trace(parent, goal);
                if (closed[cell])
                    continue;
                closed[cell] = true;
                int x = cell % _w, z = cell / _w;
                foreach (var (dx, dz, step) in Steps)
                {
                    int nx = x + dx, nz = z + dz;
                    if (!Free(nx, nz))
                        continue;
                    if (dx != 0 && dz != 0 && (!Free(x + dx, z) || !Free(x, z + dz)))
                        continue; // no squeezing past a wall corner
                    int next = nz * _w + nx;
                    if (closed[next])
                        continue;
                    float c = cost[cell] + step * (1 + _nearWall[next]);
                    if (c >= cost[next])
                        continue;
                    cost[next] = c;
                    parent[next] = cell;
                    open.Enqueue(next, c + Heuristic(nx, nz, gx, gz));
                }
            }
            return null;
        }

        private static float Heuristic(int x, int z, int gx, int gz)
        {
            int dx = Math.Abs(x - gx), dz = Math.Abs(z - gz);
            return Math.Max(dx, dz) + 0.4142f * Math.Min(dx, dz);
        }

        private static List<int> Trace(int[] parent, int goal)
        {
            var cells = new List<int>();
            for (int c = goal; c >= 0; c = parent[c])
                cells.Add(c);
            cells.Reverse();
            return cells;
        }

        /// <summary>Keeps only the cells where the route has to turn: from each kept cell, the farthest one in sight.</summary>
        public List<int> Straighten(List<int> cells)
        {
            var kept = new List<int> { cells[0] };
            int i = 0;
            while (i < cells.Count - 1)
            {
                int far = i + 1;
                for (int j = cells.Count - 1; j > i + 1; j--)
                    if (Clear(cells[i], cells[j]))
                    {
                        far = j;
                        break;
                    }
                kept.Add(cells[far]);
                i = far;
            }
            return kept;
        }

        /// <summary>A straight walk between two cells crosses no blocked cell (nor squeezes past a corner).</summary>
        public bool Clear(int a, int b)
        {
            double ax = a % _w + 0.5, az = a / _w + 0.5, bx = b % _w + 0.5, bz = b / _w + 0.5;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(bx - ax), Math.Abs(bz - az)) * 4);
            int px = (int)ax, pz = (int)az;
            for (int s = 1; s <= steps; s++)
            {
                double t = (double)s / steps;
                int x = (int)Math.Floor(ax + (bx - ax) * t), z = (int)Math.Floor(az + (bz - az) * t);
                if (!Free(x, z) || _nearWall[z * _w + x] >= 2 && !(x == px && z == pz))
                    return false;
                if (x != px && z != pz && (!Free(x, pz) || !Free(px, z)))
                    return false;
                px = x;
                pz = z;
            }
            return true;
        }
    }
}
