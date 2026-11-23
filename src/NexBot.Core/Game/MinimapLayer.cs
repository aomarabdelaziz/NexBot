namespace NexBot.Game;

/// <summary>
/// Which minimap images (Media.pk2) show a position: the open world uses <c>minimap\{x}x{y}.ddj</c>; dungeons use a
/// file prefix under <c>minimap_d</c> chosen by region and height (ported from xBot's map; names as in vSRO 1.188).
/// Each image covers one 192 x 192 sector.
/// </summary>
public static class MinimapLayer
{
    /// <summary>World size of one minimap image (one region sector).</summary>
    public const double TileSize = 192;

    /// <summary>
    /// The dungeon file prefix (e.g. "qt_a01_floor03"), or null for the open world. Unknown dungeons return null too,
    /// so they show the open-world tiles (usually missing: an empty map).
    /// </summary>
    public static string? For(Position position)
    {
        if (!position.InDungeon)
            return null;
        int z = position.Z;
        return position.Region switch
        {
            32769 => Floor("dh_a01_floor0", z),
            32770 => "qt_a01_floor06",
            32771 => "qt_a01_floor05",
            32772 => "qt_a01_floor04",
            32773 => "qt_a01_floor03",
            32774 => "qt_a01_floor02",
            32775 => "qt_a01_floor01",
            32778 or 32779 or 32780 or 32781 or 32782 or 32784 => "rn_sd_egypt1_01",
            32783 => "rn_sd_egypt1_02",
            32785 => Floor("fort_dungeon0", z),
            32786 => "flame_dungeon01",
            32787 => "rn_jupiter_02",
            32788 => "rn_jupiter_03",
            32789 => "rn_jupiter_04",
            32790 => "rn_jupiter_01",
            32793 => "rn_arabia_field_02_boss",
            _ => null,
        };
    }

    /// <summary>Image column / row of a world coordinate (open world sectors start at 135 x 92, dungeons at 0).</summary>
    public static (int X, int Y) TileOf(double posX, double posY, bool dungeon) =>
        ((int)Math.Floor(posX / TileSize) + (dungeon ? 0 : 135), (int)Math.Floor(posY / TileSize) + (dungeon ? 0 : 92));

    // Multi-floor dungeons: floors are 115 units apart.
    private static string Floor(string prefix, int z) => prefix + (z < 115 ? 1 : z < 230 ? 2 : z < 345 ? 3 : 4);
}
