using System.Drawing.Imaging;
using NexBot.Media;

namespace NexBot.App.Views;

/// <summary>
/// Item / skill icons from Media.pk2 as bitmaps, decoded in the background. <see cref="Get"/> returns null until the
/// icon is ready, then <see cref="Loaded"/> fires on the UI thread so the slots can redraw.
/// </summary>
internal sealed class IconCache(Control owner) : IDisposable
{
    private readonly Dictionary<string, Bitmap?> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _decoders = new(2);
    private MediaImages? _images;
    private int _generation;

    public event Action? Loaded;

    public bool Available => _images is not null;

    public MediaImages? Images
    {
        get => _images;
        set
        {
            _images = value;
            Clear();
            Loaded?.Invoke();
        }
    }

    public Bitmap? Get(string? path)
    {
        if (path is null || _images is not { } images)
            return null;
        if (_bitmaps.TryGetValue(path, out var bitmap))
            return bitmap;
        if (!_loading.Add(path))
            return null;

        int generation = _generation;
        _ = Task.Run(async () =>
        {
            Bitmap? decoded = null;
            await _decoders.WaitAsync().ConfigureAwait(false);
            try
            {
                if (images.GetIcon(path) is { } png)
                    decoded = Decode(png);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or ArgumentException)
            {
                // missing or broken icon: the slot shows the item name
            }
            finally
            {
                _decoders.Release();
            }
            Deliver(generation, path, decoded);
        });
        return null;
    }

    private static Bitmap Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var image = new Bitmap(stream);
        var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(copy))
            g.DrawImage(image, 0, 0, image.Width, image.Height);
        return copy;
    }

    private void Deliver(int generation, string path, Bitmap? bitmap)
    {
        if (owner.IsDisposed || !owner.IsHandleCreated)
        {
            bitmap?.Dispose();
            return;
        }
        try
        {
            owner.BeginInvoke(() =>
            {
                _loading.Remove(path);
                if (generation != _generation)
                {
                    bitmap?.Dispose();
                    return;
                }
                _bitmaps[path] = bitmap;
                Loaded?.Invoke();
            });
        }
        catch (InvalidOperationException)
        {
            bitmap?.Dispose();
        }
    }

    /// <summary>Forgets the icons that could not be read, so they are read again (Inventory's Refresh).</summary>
    public void RetryMissing()
    {
        foreach (var path in _bitmaps.Where(b => b.Value is null).Select(b => b.Key).ToList())
            _bitmaps.Remove(path);
        Loaded?.Invoke();
    }

    private void Clear()
    {
        _generation++;
        foreach (var bitmap in _bitmaps.Values)
            bitmap?.Dispose();
        _bitmaps.Clear();
        _loading.Clear();
    }

    public void Dispose()
    {
        Clear();
        _images = null;
    }
}
