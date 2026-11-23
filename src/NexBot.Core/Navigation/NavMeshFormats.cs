using System.Text;

namespace NexBot.Navigation;

/// <summary>An object placed in a terrain region (a building, wall, tree, rock ...).</summary>
/// <param name="Id">Index into <c>navmesh\object.ifo</c>.</param>
/// <param name="X">Position relative to the region that lists it (0..1920 inside it, outside for big objects).</param>
/// <param name="Height">Height of the object's origin.</param>
/// <param name="Z">Position relative to the region that lists it (the region's "y" on the wire).</param>
/// <param name="Yaw">Rotation around the vertical axis, radians.</param>
public readonly record struct NavObject(uint Id, float X, float Height, float Z, float Yaw, ushort Region);

/// <summary>A border between two terrain cells (or the region's edge).</summary>
public readonly record struct NavEdge(float X0, float Z0, float X1, float Z1, byte Flag)
{
    /// <summary>Movement over the edge is blocked in at least one direction (cliffs, the edge of the world).</summary>
    public bool IsBlocked => (Flag & 0x03) != 0;
}

/// <summary>
/// One region's terrain navigation mesh (<c>navmesh\nv_{region:x4}.nvm</c>, "JMXVNVM 1000"): the objects standing in
/// it, cell borders and a 96 x 96 tile map (20 units a tile) whose flag bit 1 marks blocked ground.
/// </summary>
public sealed class TerrainMesh
{
    public const int Size = 1920;
    public const int Tiles = 96;
    public const int TileSize = Size / Tiles;

    public required IReadOnlyList<NavObject> Objects { get; init; }
    public required IReadOnlyList<NavEdge> Edges { get; init; }

    /// <summary>Tile flags, row by row (index z * 96 + x).</summary>
    public required ushort[] TileFlags { get; init; }

    public bool IsTileBlocked(int x, int z) => (TileFlags[z * Tiles + x] & 0x0001) != 0;

    public static TerrainMesh Read(byte[] data)
    {
        var r = new BinaryReader(new MemoryStream(data, writable: false));
        string signature = Encoding.ASCII.GetString(r.ReadBytes(12));
        if (signature != "JMXVNVM 1000")
            throw new InvalidDataException($"Not a terrain navmesh ({signature}).");

        int objectCount = r.ReadUInt16();
        var objects = new List<NavObject>(objectCount);
        for (int i = 0; i < objectCount; i++)
        {
            uint id = r.ReadUInt32();
            float x = r.ReadSingle(), height = r.ReadSingle(), z = r.ReadSingle();
            r.ReadUInt16(); // type
            float yaw = r.ReadSingle();
            r.ReadBytes(6); // local id, unknown, big, struct
            ushort region = r.ReadUInt16();
            int links = r.ReadUInt16();
            r.ReadBytes(links * 6);
            objects.Add(new NavObject(id, x, height, z, yaw, region));
        }

        uint cellCount = r.ReadUInt32();
        r.ReadUInt32(); // open cells
        for (int i = 0; i < cellCount; i++)
        {
            r.ReadBytes(16); // min, max
            int n = r.ReadByte();
            r.ReadBytes(n * 2);
        }

        var edges = new List<NavEdge>();
        uint globalEdges = r.ReadUInt32();
        for (int i = 0; i < globalEdges; i++)
        {
            edges.Add(new NavEdge(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadByte()));
            r.ReadBytes(2 + 8); // directions, cells, regions
        }
        uint internalEdges = r.ReadUInt32();
        for (int i = 0; i < internalEdges; i++)
        {
            edges.Add(new NavEdge(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadByte()));
            r.ReadBytes(2 + 4); // directions, cells
        }

        var flags = new ushort[Tiles * Tiles];
        for (int i = 0; i < flags.Length; i++)
        {
            r.ReadUInt32(); // cell
            flags[i] = r.ReadUInt16();
            r.ReadUInt16(); // texture
        }
        return new TerrainMesh { Objects = objects, Edges = edges, TileFlags = flags };
    }
}

/// <summary>An edge of an object's navigation mesh (indices into its vertices).</summary>
/// <param name="Outline">An edge of the mesh's border (not one between two of its triangles).</param>
public readonly record struct ObjectEdge(ushort V0, ushort V1, byte Flag, bool Outline = true)
{
    public bool IsBlocked => (Flag & 0x03) != 0;
}

/// <summary>
/// The navigation mesh inside an object's collision model (".bms", "JMXVBMS 0110"): walkable triangles and their
/// edges; blocked outline edges are the object's walls.
/// </summary>
public sealed class ObjectMesh
{
    /// <summary>Vertex positions in the object's own space: x, height, z.</summary>
    public required (float X, float Y, float Z)[] Vertices { get; init; }
    public required (ushort A, ushort B, ushort C)[] Triangles { get; init; }
    public required ObjectEdge[] Edges { get; init; }

    /// <summary>
    /// Walls on every side of its border and no way in: the triangles are the top of something solid (a sign, a lamp,
    /// a tree trunk), not a floor to walk on.
    /// </summary>
    public bool IsSolid => Edges.Where(e => e.Outline).All(e => e.IsBlocked);

    /// <summary>The mesh, or null when the model has no navigation mesh (most decoration).</summary>
    public static ObjectMesh? Read(byte[] data)
    {
        var r = new BinaryReader(new MemoryStream(data, writable: false));
        string signature = Encoding.ASCII.GetString(r.ReadBytes(12));
        if (!signature.StartsWith("JMXVBMS ", StringComparison.Ordinal))
            throw new InvalidDataException($"Not a model ({signature}).");
        r.BaseStream.Position = 12 + 7 * 4;
        uint offset = r.ReadUInt32();
        if (offset == 0 || offset >= data.Length)
            return null;
        r.BaseStream.Position = offset;

        var vertices = new (float, float, float)[r.ReadUInt32()];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = (r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            r.ReadByte(); // normal index
        }
        var triangles = new (ushort, ushort, ushort)[r.ReadUInt32()];
        for (int i = 0; i < triangles.Length; i++)
        {
            triangles[i] = (r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16());
            r.ReadUInt16(); // flag
        }
        var edges = new List<ObjectEdge>();
        for (int part = 0; part < 2; part++) // outline, then inline
        {
            uint count = r.ReadUInt32();
            for (int i = 0; i < count; i++)
            {
                ushort v0 = r.ReadUInt16(), v1 = r.ReadUInt16();
                r.ReadUInt32(); // cells
                edges.Add(new ObjectEdge(v0, v1, r.ReadByte(), part == 0));
            }
        }
        foreach (var e in edges)
            if (e.V0 >= vertices.Length || e.V1 >= vertices.Length)
                throw new InvalidDataException("Navmesh edge points past the vertices.");
        foreach (var t in triangles)
            if (t.Item1 >= vertices.Length || t.Item2 >= vertices.Length || t.Item3 >= vertices.Length)
                throw new InvalidDataException("Navmesh triangle points past the vertices.");
        return new ObjectMesh { Vertices = vertices, Triangles = triangles, Edges = edges.ToArray() };
    }
}

/// <summary>Lookups in <c>navmesh\object.ifo</c> and the object resources (".bsr").</summary>
public static class NavObjectFiles
{
    /// <summary>object.ifo ("JMXVOBJI1000"): id → resource path.</summary>
    public static Dictionary<uint, string> ReadObjectList(string text)
    {
        var list = new Dictionary<uint, string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int quote = line.IndexOf('"');
            if (quote < 0 || line.LastIndexOf('"') <= quote)
                continue;
            int space = line.IndexOf(' ');
            if (space <= 0 || !uint.TryParse(line.AsSpan(0, space), out uint id))
                continue;
            list[id] = line[(quote + 1)..line.LastIndexOf('"')];
        }
        return list;
    }

    /// <summary>The collision model path of a resource ("JMXVRES 0109": 8th header offset), or null.</summary>
    public static string? ReadCollisionPath(byte[] bsr)
    {
        if (bsr.Length < 12 + 8 * 4 || !Encoding.ASCII.GetString(bsr, 0, 8).StartsWith("JMXVRES", StringComparison.Ordinal))
            return null;
        int offset = BitConverter.ToInt32(bsr, 12 + 7 * 4);
        if (offset <= 0 || offset + 4 > bsr.Length)
            return null;
        int length = BitConverter.ToInt32(bsr, offset);
        if (length <= 0 || length > 260 || offset + 4 + length > bsr.Length)
            return null;
        return Encoding.ASCII.GetString(bsr, offset + 4, length);
    }
}
