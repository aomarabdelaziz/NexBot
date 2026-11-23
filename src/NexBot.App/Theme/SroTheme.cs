using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NexBot.App.Theme;

/// <summary>
/// The Silkroad look (ported from POC-2's Avalonia theme): black backdrop, metal-framed panels with a gold title,
/// ochre parchment strips, steel and gold buttons. Colors, fonts and painting helpers.
/// </summary>
internal static class SroTheme
{
    public static readonly Color Bg = Hex("#050505");
    public static readonly Color Line = Hex("#3a352a");
    public static readonly Color Frame = Hex("#77736a");
    public static readonly Color FrameDark = Hex("#2b2a27");
    public static readonly Color InputLine = Hex("#4a4436");
    public static readonly Color InputBack = Hex("#090807");
    public static readonly Color Gold = Hex("#e8c97a");
    public static readonly Color GoldDim = Hex("#9a7a3c");
    public static readonly Color Text = Hex("#e9e2cf");
    public static readonly Color Muted = Hex("#a39a85");
    public static readonly Color Value = Hex("#f0dca0");
    public static readonly Color Good = Hex("#7fcf6a");
    public static readonly Color Bad = Hex("#e06464");
    public static readonly Color Warn = Hex("#e0b04f");
    public static readonly Color Sox = Hex("#f2c94c");
    public static readonly Color Blue = Hex("#8fb4ff");

    private static readonly PrivateFontCollection s_fonts = new();
    private static readonly List<IntPtr> s_fontMemory = [];
    private static FontFamily? s_cinzel;
    private static TextureBrush? s_noise;

    public static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        int argb = Convert.ToInt32(hex, 16);
        return hex.Length == 8
            ? Color.FromArgb(argb)                                      // AARRGGBB
            : Color.FromArgb(255, (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
    }

    /// <summary>Cinzel (titles), embedded in the exe; falls back to a serif font.</summary>
    public static FontFamily TitleFamily
    {
        get
        {
            if (s_cinzel is not null)
                return s_cinzel;
            try
            {
                foreach (string name in new[] { "Cinzel-Bold.ttf", "Cinzel-SemiBold.ttf" })
                {
                    using var stream = typeof(SroTheme).Assembly.GetManifestResourceStream($"NexBot.App.Assets.{name}");
                    if (stream is null)
                        continue;
                    byte[] data = new byte[stream.Length];
                    stream.ReadExactly(data);
                    IntPtr memory = Marshal.AllocCoTaskMem(data.Length); // must live as long as the font
                    Marshal.Copy(data, 0, memory, data.Length);
                    s_fonts.AddMemoryFont(memory, data.Length);
                    s_fontMemory.Add(memory);
                }
                s_cinzel = s_fonts.Families.FirstOrDefault() ?? new FontFamily("Georgia");
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException)
            {
                s_cinzel = new FontFamily("Georgia");
            }
            return s_cinzel;
        }
    }

    public static Font Title(float size, FontStyle style = FontStyle.Bold) => new(TitleFamily, size, style, GraphicsUnit.Point);

    public static readonly Font Body = new("Segoe UI", 9.5f);
    public static readonly Font BodyBold = new("Segoe UI", 9.5f, FontStyle.Bold);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Tiny = new("Segoe UI", 7.5f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9f);

    /// <summary>The fine grain over panels (blurred random alpha, 45% black), as a 180 px tile.</summary>
    public static TextureBrush Noise => s_noise ??= CreateNoise();

    private static TextureBrush CreateNoise()
    {
        const int size = 180;
        var random = new Random(1234);
        var values = new double[size * size];
        for (int i = 0; i < values.Length; i++)
            values[i] = random.NextDouble();
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        sum += values[(y + dy + size) % size * size + (x + dx + size) % size] * (dx == 0 && dy == 0 ? 4 : 1);
                double v = sum / 12;
                double a = Math.Clamp(0.5 + (v - 0.5) * 2.2, 0, 1) * 0.45;
                bitmap.SetPixel(x, y, Color.FromArgb((int)Math.Round(a * 255), 0, 0, 0));
            }
        }
        return new TextureBrush(bitmap, WrapMode.Tile);
    }

    public static LinearGradientBrush Vertical(Rectangle rect, Color top, Color bottom) =>
        new(new Rectangle(rect.X, rect.Y - 1, Math.Max(1, rect.Width), Math.Max(1, rect.Height + 2)), top, bottom, LinearGradientMode.Vertical);

    public static LinearGradientBrush Vertical(Rectangle rect, params (Color Color, float At)[] stops)
    {
        var brush = Vertical(rect, stops[0].Color, stops[^1].Color);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = stops.Select(s => s.Color).ToArray(),
            Positions = stops.Select(s => s.At).ToArray(),
        };
        return brush;
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void StrokeRect(Graphics g, Color color, Rectangle r, float width = 1)
    {
        using var pen = new Pen(color, width);
        g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    /// <summary>Draws letter-spaced text (Cinzel titles), centered or left aligned in <paramref name="bounds"/>.</summary>
    public static void DrawSpaced(Graphics g, string text, Font font, Brush brush, RectangleF bounds, float spacing, bool center = true)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        var widths = text.Select(ch => g.MeasureString(ch.ToString(), font, PointF.Empty, format).Width).ToArray();
        float total = widths.Sum() + spacing * Math.Max(0, text.Length - 1);
        float height = font.GetHeight(g);
        float x = center ? bounds.X + (bounds.Width - total) / 2 : bounds.X;
        float y = bounds.Y + (bounds.Height - height) / 2;
        for (int i = 0; i < text.Length; i++)
        {
            g.DrawString(text[i].ToString(), font, brush, x, y, format);
            x += widths[i] + spacing;
        }
    }

    /// <summary>A small rotated square (legend / list markers).</summary>
    public static void Diamond(Graphics g, Color fill, float cx, float cy, float rx, float ry, Color? outline = null)
    {
        PointF[] points = [new(cx, cy - ry), new(cx + rx, cy), new(cx, cy + ry), new(cx - rx, cy)];
        using var brush = new SolidBrush(fill);
        g.FillPolygon(brush, points);
        if (outline is { } o)
        {
            using var pen = new Pen(o, 1);
            g.DrawPolygon(pen, points);
        }
    }
}
