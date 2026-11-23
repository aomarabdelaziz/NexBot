namespace NexBot.Configuration;

/// <summary>A point on the map in world coordinates.</summary>
public sealed record MapPoint(double X, double Y);

/// <summary>
/// Where the character trains (phBot's training area): a circle around a position, or a polygon drawn on the map.
/// The position is always kept: other features (walking back, returning to the area) use it.
/// </summary>
public sealed class TrainingAreaSettings
{
    /// <summary>The area's name in the training area list.</summary>
    public string Name { get; set; } = "";

    /// <summary>How far from the character drops are picked up in this area.</summary>
    public int PickRadius { get; set; } = 50;

    /// <summary>The walk script from town to this area (empty: walk there with the map's routes).</summary>
    public string Script { get; set; } = "";

    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Region of the position (dungeon regions use local coordinates).</summary>
    public ushort Region { get; set; }
    public int Radius { get; set; } = 50;

    /// <summary>Corners of the polygon, in drawing order (clockwise, as phBot asks).</summary>
    public List<MapPoint> Polygon { get; set; } = [];
    /// <summary>Use the polygon instead of the circle (needs at least 3 corners).</summary>
    public bool UsePolygon { get; set; }

    public bool IsSet => X != 0 || Y != 0;

    /// <summary>Copies the place, shape and options of <paramref name="other"/> (the object itself stays).</summary>
    public void CopyFrom(TrainingAreaSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        (Name, PickRadius, Script, X, Y, Region, Radius, UsePolygon) =
            (other.Name, other.PickRadius, other.Script, other.X, other.Y, other.Region, other.Radius, other.UsePolygon);
        Polygon = [.. other.Polygon];
    }

    /// <summary>The middle of the area: the position, or the polygon's corners' average.</summary>
    public (double X, double Y) Center =>
        UsePolygon && HasPolygon ? (Polygon.Average(p => p.X), Polygon.Average(p => p.Y)) : (X, Y);
    public bool HasPolygon => Polygon.Count >= 3;

    /// <summary>True when the world point is inside the area (the polygon when used, else the circle).</summary>
    public bool Contains(double x, double y)
    {
        if (UsePolygon && HasPolygon)
            return InPolygon(Polygon, x, y);
        if (!IsSet)
            return false;
        double dx = x - X, dy = y - Y;
        return dx * dx + dy * dy <= (double)Radius * Radius;
    }

    /// <summary>Even-odd ray cast: works for clockwise and counter-clockwise polygons.</summary>
    public static bool InPolygon(IReadOnlyList<MapPoint> polygon, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if (a.Y > y != b.Y > y && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}

/// <summary>Switch to a training area at a level (phBot's Conditions tab).</summary>
public sealed class TrainingCondition
{
    public int Level { get; set; }
    public string Area { get; set; } = "";
}

/// <summary>
/// phBot's Training Area page: the list of named areas (the enabled one is copied into <see cref="BotSettings.Training"/>),
/// the level conditions, and the Collision, Settings and Script tabs.
/// </summary>
public sealed class TrainingSetupSettings
{
    public List<TrainingAreaSettings> Areas { get; set; } = [];

    /// <summary>The enabled area's name.</summary>
    public string Enabled { get; set; } = "";

    public List<TrainingCondition> Conditions { get; set; } = [];

    // Collision
    /// <summary>Ignore monsters and items behind walls (needs the map data).</summary>
    public bool CollisionDetection { get; set; }
    /// <summary>Walk around obstacles (map routes) when walking to the area and inside it.</summary>
    public bool NavigateAroundObstacles { get; set; } = true;
    /// <summary>Walk to drops on map routes.</summary>
    public bool NavigateToDrops { get; set; }

    // Settings
    /// <summary>Don't walk around in the area when no monster is near.</summary>
    public bool DontWalkAround { get; set; }
    public bool UseRepairHammer { get; set; }
    /// <summary>Repair with a hammer when an equipped item's durability is under this %.</summary>
    public int RepairHammerBelow { get; set; } = 10;
    public bool ReverseReturnOnDeath { get; set; }
    public bool ReverseReturnAfterReturn { get; set; }
    public bool UseSummonScrolls { get; set; }
    public bool WaitForStrongBeforeSummon { get; set; } = true;
    public bool UseSpeedDrugs { get; set; }
    public bool SpeedDrugsOnlyInScript { get; set; }

    // Script
    public bool SkipTownScript { get; set; }
    public bool ReturnWhenCannotContinue { get; set; } = true;
    /// <summary>Wait this long at every point of a script (ms; 0 = off).</summary>
    public int ScriptWalkDelay { get; set; }
    /// <summary>Go back a point when stuck this long (s; 0 = off).</summary>
    public int GoBackIfStuck { get; set; } = 10;
    /// <summary>Return to town when stuck in the script this long (s; 0 = off).</summary>
    public int ReturnIfStuck { get; set; } = 60;
    public bool RemountInCaves { get; set; }

    public TrainingAreaSettings? Find(string name) =>
        Areas.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}
