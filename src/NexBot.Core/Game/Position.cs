using System.Globalization;

namespace NexBot.Game;

/// <summary>
/// A world position. Holds both the region-local coordinates sent on the wire (Region, X, Y, Z) and the global
/// world coordinates (PosX, PosY) used for distances.
/// </summary>
/// <remarks>An immutable value type, so no garbage is created while movement is interpolated (the legacy class allocated per update).</remarks>
public readonly struct Position : IEquatable<Position>
{
    private const double SectorSize = 192.0;

    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public double PosX { get; }
    public double PosY { get; }
    public ushort Region { get; }
    public byte XSector { get; }
    public byte YSector { get; }

    /// <summary>Creates a position from world coordinates (outside dungeons).</summary>
    public Position(double posX, double posY)
    {
        PosX = posX;
        PosY = posY;
        X = LocalCoord(posX);
        Y = LocalCoord(posY);
        XSector = (byte)Math.Round((posX - X / 10.0) / SectorSize + 135);
        YSector = (byte)Math.Round((posY - Y / 10.0) / SectorSize + 92);
        Region = (ushort)((YSector << 8) | XSector);
        Z = 0;
    }

    /// <summary>Creates a position from region-local coordinates as they appear on the wire.</summary>
    public Position(ushort region, int x, int z, int y)
    {
        Region = region;
        if (IsDungeonRegion(region))
        {
            PosX = 128 * 192 + x / 10;
            PosY = 128 * 192 + y / 10;
            XSector = (byte)((128.0 * SectorSize + PosX) / SectorSize - 128);
            YSector = (byte)((128.0 * SectorSize + PosY) / SectorSize - 128);
        }
        else
        {
            XSector = (byte)(region & 0xFF);
            YSector = (byte)(region >> 8);
            PosX = (XSector - 135) * 192 + x / 10;
            PosY = (YSector - 92) * 192 + y / 10;
        }
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Creates a position from world coordinates inside a known region (required for dungeons).</summary>
    public static Position FromWorld(double posX, double posY, ushort region, int z = 0) => new(posX, posY, region, z);

    private Position(double posX, double posY, ushort region, int z)
    {
        PosX = posX;
        PosY = posY;
        Region = region;
        if (IsDungeonRegion(region))
        {
            XSector = (byte)((128.0 * SectorSize + posX) / SectorSize - 128);
            YSector = (byte)((128.0 * SectorSize + posY) / SectorSize - 128);
            X = (int)Math.Round((posX - 128.0 * SectorSize) * 10.0);
            Y = (int)Math.Round((posY - 128.0 * SectorSize) * 10.0);
        }
        else
        {
            X = LocalCoord(posX);
            Y = LocalCoord(posY);
            XSector = (byte)Math.Round((posX - X / 10.0) / SectorSize + 135);
            YSector = (byte)Math.Round((posY - Y / 10.0) / SectorSize + 92);
        }
        Z = z;
    }

    private static int LocalCoord(double world)
    {
        int local = (int)(Math.Abs(world) % SectorSize * 10.0);
        return world < 0.0 ? 1920 - local : local;
    }

    public bool InDungeon => IsDungeonRegion(Region);

    public static bool IsDungeonRegion(ushort region) => region > short.MaxValue;

    public double DistanceTo(Position other) => DistanceTo(other.PosX, other.PosY);

    public double DistanceTo(double posX, double posY)
    {
        double dx = posX - PosX, dy = posY - PosY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>True when both positions are within one world unit of each other.</summary>
    public bool IsNear(Position other) => DistanceTo(other) <= 1;

    /// <summary>Milliseconds needed to travel to <paramref name="other"/> at <paramref name="unitsPerMs"/>.</summary>
    public long TimeTo(Position other, double unitsPerMs) =>
        unitsPerMs <= 0 ? long.MaxValue : (long)Math.Round(DistanceTo(other) / unitsPerMs);

    public bool Equals(Position other) =>
        Region == other.Region && X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is Position p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(Region, X, Y, Z);
    public static bool operator ==(Position left, Position right) => left.Equals(right);
    public static bool operator !=(Position left, Position right) => !left.Equals(right);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"(X: {Math.Round(PosX)} Y: {Math.Round(PosY)}) [R: {Region} X: {X} Y: {Y} Z: {Z}]");
}
