using System.Collections.Concurrent;

namespace NexBot.Media;

/// <summary>
/// Images from Media.pk2 as PNG bytes (ported from POC-2's IconService):
/// <list type="bullet">
/// <item>minimap: open world <c>minimap\{x}x{y}.ddj</c>, dungeons <c>minimap_d\...\{layer}_{x}x{y}.ddj</c>;</item>
/// <item>icons: the paths of the items / skills tables under <c>icon\</c> (e.g. <c>item\china\weapon\blade_11.ddj</c>).</item>
/// </list>
/// Decoded once and cached. Thread-safe.
/// </summary>
public sealed class MediaImages : IDisposable
{
    private const int MaxCachedTiles = 600; // about 45 MB of PNG
    private const int MaxCachedIcons = 3000; // icons are small (32 x 32)

    private readonly Pk2Archive _media;
    private readonly ConcurrentDictionary<string, byte[]?> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<Dictionary<string, Pk2File>> _dungeonTiles;

    public MediaImages(string clientPath, string? blowfishKey = null)
    {
        _media = new Pk2Archive(MediaExtractor.ResolveMediaPath(clientPath), blowfishKey);
        _dungeonTiles = new Lazy<Dictionary<string, Pk2File>>(() => _media.Files
            .Where(f => f.Path.StartsWith("minimap_d\\", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>PNG of a minimap image, or null when there is none.</summary>
    /// <param name="layer">Dungeon prefix from <see cref="Game.MinimapLayer.For"/>, or null for the open world.</param>
    public byte[]? GetTile(string? layer, int x, int y)
    {
        if (layer is not null && (layer.Length > 64 || !layer.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
            return null;
        string name = layer is null ? $"{x}x{y}" : $"{layer}_{x}x{y}";
        if (_tiles.TryGetValue(name, out var cached))
            return cached;

        var file = layer is null
            ? _media.GetFile($"minimap\\{name}.ddj")
            : _dungeonTiles.Value.GetValueOrDefault(name + ".ddj");

        var png = Decode(file);
        if (_tiles.Count >= MaxCachedTiles)
            _tiles.Clear();
        _tiles[name] = png;
        return png;
    }

    /// <summary>
    /// PNG of an icon path from the game tables (relative to <c>icon\</c>); the game's default icon when the file is
    /// missing; null when that is missing too.
    /// </summary>
    public byte[]? GetIcon(string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath) || iconPath.Contains("..", StringComparison.Ordinal) || iconPath.Length > 260)
            return null;
        if (_icons.TryGetValue(iconPath, out var cached))
            return cached;

        // Interface pictures (character portraits) are read from their own folder, without the default icon.
        var file = iconPath.StartsWith("interface\\", StringComparison.OrdinalIgnoreCase)
            ? _media.GetFile(iconPath)
            : _media.GetFile("icon\\" + iconPath.Replace('/', '\\')) ?? _media.GetFile("icon\\icon_default.ddj");
        var png = Decode(file);
        if (_icons.Count >= MaxCachedIcons)
            _icons.Clear();
        _icons[iconPath] = png;
        return png;
    }

    private byte[]? Decode(Pk2File? file)
    {
        if (file is null)
            return null;
        try
        {
            return DdjImage.ToPng(_media.ReadBytes(file));
        }
        catch (InvalidDataException)
        {
            return null; // broken image: show the empty slot / tile
        }
    }

    public void Dispose() => _media.Dispose();
}
