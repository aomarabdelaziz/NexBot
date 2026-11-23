using System.Text;
using NexBot.Game;
using NexBot.Navigation;
using Xunit;

namespace NexBot.Tests;

/// <summary>The client's map data (navmesh files) and routes found over it.</summary>
public class NavigationTests
{
    private const ushort Region = 0x61A8; // Jangan's region
    private const int N = NavMap.CellsPerRegion;

    /// <summary>A test map: one open region, everything around it closed.</summary>
    private sealed class TestMap : IWalkMap
    {
        public bool[] Blocked { get; } = new bool[N * N];
        public void Block(int x0, int z0, int x1, int z1)
        {
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    Blocked[z * N + x] = true;
        }
        public RegionGrid? GetRegion(ushort region) => region == Region ? new RegionGrid(region, Blocked) : RegionGrid.Closed(region);
    }

    private static Position At(int cellX, int cellZ) =>
        new(Region, cellX * NavMap.CellSize + NavMap.CellSize / 2, 0, cellZ * NavMap.CellSize + NavMap.CellSize / 2);

    private static (int X, int Z) CellOf(Position p) => (p.X / NavMap.CellSize, p.Y / NavMap.CellSize);

    /// <summary>True when every straight leg of the route stays off blocked cells.</summary>
    private static bool StaysFree(TestMap map, Position from, List<Position> route)
    {
        var points = new[] { from }.Concat(route).Select(CellOf).ToList();
        for (int i = 1; i < points.Count; i++)
        {
            var (ax, az) = points[i - 1];
            var (bx, bz) = points[i];
            int steps = Math.Max(Math.Abs(bx - ax), Math.Abs(bz - az)) * 4 + 1;
            for (int s = 0; s <= steps; s++)
            {
                int x = (int)Math.Floor(ax + 0.5 + (bx - ax) * (double)s / steps), z = (int)Math.Floor(az + 0.5 + (bz - az) * (double)s / steps);
                if (map.Blocked[z * N + x])
                    return false;
            }
        }
        return true;
    }

    [Fact]
    public void A_straight_way_is_clear_only_with_room_from_walls()
    {
        var map = new TestMap();
        map.Block(100, 100, 120, 120); // a building
        Assert.True(PathFinder.IsClear(map, At(90, 90), At(90, 130)));    // well past its west side
        Assert.False(PathFinder.IsClear(map, At(95, 125), At(125, 95)));  // across its corner
        Assert.False(PathFinder.IsClear(map, At(99, 90), At(99, 130)));   // brushing its side
        Assert.False(PathFinder.IsClear(map, At(90, 90), new Position(0x8000, 0, 0, 0)));
        Assert.True(PathFinder.IsOpen(map, At(90, 90)));
        Assert.False(PathFinder.IsOpen(map, At(110, 110)));
    }

    [Fact]
    public void A_straight_way_is_one_leg()
    {
        var map = new TestMap();
        var route = PathFinder.Find(map, At(100, 100), At(150, 120))!;
        Assert.Single(route);
        Assert.Equal((150, 120), CellOf(route[0]));
    }

    [Fact]
    public void A_wall_is_walked_round_through_its_gate_even_far_away()
    {
        // A wall across the region with one gate, 100 cells (50 units) north of the straight line.
        var map = new TestMap();
        map.Block(200, 0, 201, 299);
        map.Block(200, 311, 201, N - 1);
        var from = At(170, 150);
        var route = PathFinder.Find(map, from, At(230, 150))!;

        Assert.Equal((230, 150), CellOf(route[^1]));
        Assert.Contains(route, p => CellOf(p).Z is >= 295 and <= 315);
        Assert.True(StaysFree(map, from, route));
    }

    [Fact]
    public void Routes_keep_room_from_walls_and_squeeze_through_only_when_they_must()
    {
        // A post right beside the straight line: the route goes round it with at least 2 cells (1 unit) of room.
        var map = new TestMap();
        map.Block(150, 101, 151, 102);
        var from = At(100, 100);
        var route = PathFinder.Find(map, from, At(200, 100))!;
        var points = new[] { from }.Concat(route).Select(CellOf).ToList();
        for (int i = 1; i < points.Count; i++)
            for (double t = 0; t <= 1; t += 0.01)
            {
                double x = points[i - 1].X + (points[i].X - points[i - 1].X) * t, z = points[i - 1].Z + (points[i].Z - points[i - 1].Z) * t;
                Assert.True(Math.Abs(x - 150.5) > 2 || Math.Abs(z - 101.5) > 2, $"passes the post at {x:0},{z:0}");
            }

        // A door 3 cells wide (too narrow for that room) is still used rather than ending outside.
        var walled = new TestMap();
        walled.Block(200, 0, 200, 99);
        walled.Block(200, 103, 200, N - 1);
        var through = PathFinder.Find(walled, At(170, 50), At(230, 50))!;
        Assert.Equal((230, 50), CellOf(through[^1]));
    }

    [Fact]
    public void A_mesh_walled_all_round_is_solid_and_one_with_a_way_in_is_a_floor()
    {
        var solid = ObjectMesh.Read(Model(withNavmesh: true, entrance: false))!;
        var floor = ObjectMesh.Read(Model(withNavmesh: true, entrance: true))!;
        Assert.True(solid.IsSolid);
        Assert.False(floor.IsSolid);
    }

    [Fact]
    public void A_goal_inside_a_closed_building_ends_at_the_nearest_reachable_spot()
    {
        var map = new TestMap();
        map.Block(200, 100, 230, 100);
        map.Block(200, 130, 230, 130);
        map.Block(200, 100, 200, 130);
        map.Block(230, 100, 230, 130);
        var route = PathFinder.Find(map, At(100, 115), At(215, 115))!;

        // Just outside the box (keeping room from its wall), on the side nearest the goal.
        var end = CellOf(route[^1]);
        bool outside = end.X < 200 || end.X > 230 || end.Z < 100 || end.Z > 130;
        Assert.True(outside && Math.Abs(end.X - 215) + Math.Abs(end.Z - 115) <= 20, $"ends at {end}");
    }

    [Fact]
    public void A_start_in_a_small_closed_pocket_steps_out_first()
    {
        // A fountain rim round the character: the pocket inside is free but closed.
        var map = new TestMap();
        map.Block(95, 95, 105, 95);
        map.Block(95, 105, 105, 105);
        map.Block(95, 95, 95, 105);
        map.Block(105, 95, 105, 105);
        var route = PathFinder.Find(map, At(100, 100), At(160, 100));

        Assert.NotNull(route);
        Assert.Equal((160, 100), CellOf(route![^1]));
    }

    [Fact]
    public void No_route_into_a_closed_area_or_a_dungeon()
    {
        var map = new TestMap();
        map.Block(0, 200, N - 1, 201); // a wall right across the region: the north half cannot be reached
        Assert.Null(PathFinder.Find(map, At(100, 100), At(100, 300)));
        Assert.Null(PathFinder.Find(map, At(100, 100), new Position(0x8000, 100, 0, 100)));
    }

    [Fact]
    public void An_object_is_turned_by_its_yaw_then_moved_to_its_place()
    {
        var o = new NavObject(1, 500, 0, 600, (float)(Math.PI / 2), Region);
        var (x, z) = NavMap.Place(o, (10, 0, 0));
        Assert.Equal(500, x, 3);
        Assert.Equal(610, z, 3);
    }

    [Fact]
    public void A_terrain_navmesh_is_read()
    {
        using var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write(Encoding.ASCII.GetBytes("JMXVNVM 1000"));
        w.Write((ushort)1); // one object
        w.Write(702u); w.Write(860.5f); w.Write(-1.5f); w.Write(-155.5f); w.Write((ushort)0xFFFF); w.Write(1.5f);
        w.Write(new byte[6]); w.Write((ushort)0x60A8); w.Write((ushort)1); w.Write(new byte[6]); // one link
        w.Write(1u); w.Write(1u); // one cell
        w.Write(0f); w.Write(0f); w.Write(1920f); w.Write(1920f); w.Write((byte)1); w.Write((ushort)0);
        w.Write(1u); // one global edge
        w.Write(0f); w.Write(1920f); w.Write(20f); w.Write(1920f); w.Write((byte)0x08); w.Write(new byte[10]);
        w.Write(1u); // one internal edge, blocked
        w.Write(580f); w.Write(560f); w.Write(600f); w.Write(560f); w.Write((byte)0x02); w.Write(new byte[6]);
        for (int i = 0; i < 96 * 96; i++)
        {
            w.Write(0u);
            w.Write((ushort)(i == 5 * 96 + 7 ? 0x601 : 0x600));
            w.Write((ushort)0);
        }
        w.Write(new byte[97 * 97 * 4 + 36 + 144]);

        var mesh = TerrainMesh.Read(stream.ToArray());

        var o = Assert.Single(mesh.Objects);
        Assert.Equal((702u, 860.5f, -155.5f, 1.5f, (ushort)0x60A8), (o.Id, o.X, o.Z, o.Yaw, o.Region));
        Assert.Equal(2, mesh.Edges.Count);
        Assert.False(mesh.Edges[0].IsBlocked);
        Assert.True(mesh.Edges[1].IsBlocked);
        Assert.True(mesh.IsTileBlocked(7, 5));
        Assert.False(mesh.IsTileBlocked(5, 7));
    }

    private static byte[] Model(bool withNavmesh, bool entrance = true)
    {
        using var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write(Encoding.ASCII.GetBytes("JMXVBMS 0110"));
        int navOffset = 12 + 10 * 4 + 16;
        for (int i = 0; i < 10; i++)
            w.Write(i == 7 && withNavmesh ? navOffset : 0);
        w.Write(new byte[16]);
        if (withNavmesh)
        {
            w.Write(3u);
            foreach (var (x, z) in new[] { (0f, 0f), (10f, 0f), (0f, 10f) })
            {
                w.Write(x); w.Write(0f); w.Write(z); w.Write((byte)0);
            }
            w.Write(1u); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)0);
            w.Write(2u); // outline: a wall and an entrance
            w.Write((ushort)0); w.Write((ushort)1); w.Write(0u); w.Write((byte)0x03);
            w.Write((ushort)1); w.Write((ushort)2); w.Write(0u); w.Write(entrance ? (byte)0x10 : (byte)0x03);
            w.Write(0u); // inline
        }
        return stream.ToArray();
    }

    [Fact]
    public void An_object_navmesh_is_read_and_its_blocked_edges_are_walls()
    {
        var mesh = ObjectMesh.Read(Model(withNavmesh: true))!;
        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Single(mesh.Triangles);
        Assert.Equal(new[] { true, false }, mesh.Edges.Select(e => e.IsBlocked));
        Assert.Null(ObjectMesh.Read(Model(withNavmesh: false)));
    }

    [Fact]
    public void Npc_positions_are_read_from_npcpos_txt()
    {
        // Lines as in the vSRO 1.188 client: model id, region, x, height, z (tab separated).
        var spots = NavMap.ReadNpcPositions("2031\t25000\t1009.54\t0.0\t1902.64\r\n2015\t25001\t183.8\t-35.990002\t913.08002\r\nbad line\r\n");

        Assert.Equal(2, spots.Count);
        Assert.Equal((2031u, (ushort)25000, 1010, 1903), (spots[0].ModelId, spots[0].Position.Region, spots[0].Position.X, spots[0].Position.Y));
        Assert.Equal(-36, spots[1].Position.Z);
    }

    [Fact]
    public void The_object_list_and_collision_model_paths_are_read()
    {
        var list = NavObjectFiles.ReadObjectList("JMXVOBJI1000\r\n2\r\n00000 0x00000001 \"res\\bldg\\a.bsr\"\r\n00702 0x00000001 \"res\\bldg\\china\\jangan_enter\\cj_s.bsr\"\r\n");
        Assert.Equal(@"res\bldg\china\jangan_enter\cj_s.bsr", list[702]);

        using var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write(Encoding.ASCII.GetBytes("JMXVRES 0109"));
        const string collision = @"prim\mesh\bldg\china\jangan_enter\cj_s_stair.bms";
        int offset = 12 + 8 * 4 + 8;
        for (int i = 0; i < 8; i++)
            w.Write(i == 7 ? offset : 0);
        w.Write(new byte[8]);
        w.Write(collision.Length);
        w.Write(Encoding.ASCII.GetBytes(collision));
        Assert.Equal(collision, NavObjectFiles.ReadCollisionPath(stream.ToArray()));
        Assert.Null(NavObjectFiles.ReadCollisionPath(new byte[10]));
    }
}
