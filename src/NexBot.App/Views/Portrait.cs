using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// Draws the character's face portrait (the game's 128 x 128 picture from Media.pk2): in a round gold medallion
/// (Character page header) or in a square gold frame (equipment panel), and a crest when there is no portrait.
/// </summary>
internal static class PortraitArt
{
    /// <summary>The portrait in a circle with a dark inner ring and a gold rim.</summary>
    public static void DrawRound(Graphics g, RectangleF disc, Bitmap? face, float fade = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using (var clip = new GraphicsPath())
        {
            clip.AddEllipse(disc);
            var state = g.Save();
            g.SetClip(clip, CombineMode.Intersect);
            using (var back = new LinearGradientBrush(disc, Hex("#1d1a14"), Hex("#080706"), LinearGradientMode.Vertical))
                g.FillEllipse(back, disc);
            if (face is not null)
                DrawFace(g, RectangleF.Inflate(disc, disc.Width * 0.06f, disc.Height * 0.06f), face, fade);
            else
                Crest(g, disc, fade);
            g.Restore(state);
        }
        using (var dark = new Pen(Color.FromArgb(A(255, fade), 0, 0, 0), 3))
            g.DrawEllipse(dark, RectangleF.Inflate(disc, 0.5f, 0.5f));
        using (var rim = new Pen(Color.FromArgb(A(255, fade), Hex("#c9a45a")), 2f))
            g.DrawEllipse(rim, RectangleF.Inflate(disc, 2.5f, 2.5f));
        using (var outer = new Pen(Color.FromArgb(A(120, fade), Hex("#6b5428")), 1))
            g.DrawEllipse(outer, RectangleF.Inflate(disc, 5f, 5f));
    }

    /// <summary>The portrait in a square double gold frame with a diamond on each corner.</summary>
    public static void DrawFramed(Graphics g, RectangleF box, Bitmap? face, float fade = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // Soft shadow under the frame.
        using (var shadow = new SolidBrush(Color.FromArgb(A(110, fade), 0, 0, 0)))
            g.FillRectangle(shadow, RectangleF.Inflate(new RectangleF(box.X + 3, box.Y + 4, box.Width, box.Height), 4, 4));
        using (var back = new LinearGradientBrush(box, Hex("#1d1a14"), Hex("#080706"), LinearGradientMode.Vertical))
            g.FillRectangle(back, box);
        var state = g.Save();
        g.SetClip(box, CombineMode.Intersect);
        if (face is not null)
            DrawFace(g, box, face, fade);
        else
            Crest(g, box, fade);
        g.Restore(state);
        // Inner shading: darker toward the edges, like a recessed window.
        using (var edge = new GraphicsPath())
        {
            edge.AddRectangle(box);
            using var vignette = new PathGradientBrush(edge) { CenterColor = Color.Transparent, SurroundColors = [Color.FromArgb(A(90, fade), 0, 0, 0)], FocusScales = new PointF(0.75f, 0.75f) };
            g.FillRectangle(vignette, box);
        }
        using (var dark = new Pen(Color.FromArgb(A(255, fade), 0, 0, 0), 2))
            g.DrawRectangle(dark, box.X - 1, box.Y - 1, box.Width + 2, box.Height + 2);
        using (var rim = new Pen(Color.FromArgb(A(255, fade), Hex("#c9a45a")), 1.6f))
            g.DrawRectangle(rim, box.X - 3, box.Y - 3, box.Width + 6, box.Height + 6);
        using (var outer = new Pen(Color.FromArgb(A(150, fade), Hex("#6b5428")), 1))
            g.DrawRectangle(outer, box.X - 6, box.Y - 6, box.Width + 12, box.Height + 12);
        var corner = Color.FromArgb(A(255, fade), Hex("#e6c77c"));
        var outline = Color.FromArgb(A(255, fade), Hex("#3a2a0a"));
        foreach (var (x, y) in new[] { (box.Left - 3, box.Top - 3), (box.Right + 3, box.Top - 3), (box.Left - 3, box.Bottom + 3), (box.Right + 3, box.Bottom + 3) })
            Diamond(g, corner, x, y, 5, 5, outline);
    }

    private static void DrawFace(Graphics g, RectangleF to, Bitmap face, float fade)
    {
        if (fade >= 0.999f)
        {
            g.DrawImage(face, to);
            return;
        }
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = fade });
        g.DrawImage(face, Rectangle.Round(to), 0, 0, face.Width, face.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>The crest shown without a portrait: two rings, a four-point star and a diamond.</summary>
    public static void Crest(Graphics g, RectangleF r, float fade)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, size = Math.Min(r.Width, r.Height);
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(cx - size * 0.45f, cy - size * 0.45f, size * 0.9f, size * 0.9f);
            using var glow = new PathGradientBrush(glowPath) { CenterColor = Color.FromArgb(A(55, fade), Hex("#c9a45a")), SurroundColors = [Color.Transparent] };
            g.FillPath(glow, glowPath);
        }
        float r1 = size * 0.32f, r2 = size * 0.25f;
        using (var ring = new Pen(Color.FromArgb(A(120, fade), Gold), 1.2f))
        {
            g.DrawEllipse(ring, cx - r1, cy - r1, r1 * 2, r1 * 2);
            g.DrawEllipse(ring, cx - r2, cy - r2, r2 * 2, r2 * 2);
            for (int i = 0; i < 16; i++)
            {
                double a = i * Math.PI / 8;
                float inner = r1 - (i % 2 == 0 ? 5 : 2.5f);
                g.DrawLine(ring, cx + (float)Math.Cos(a) * inner, cy + (float)Math.Sin(a) * inner, cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1);
            }
        }
        float s = r2 * 0.92f, w = r2 * 0.22f;
        PointF[] star =
        [
            new(cx, cy - s), new(cx + w, cy - w), new(cx + s, cy), new(cx + w, cy + w),
            new(cx, cy + s), new(cx - w, cy + w), new(cx - s, cy), new(cx - w, cy - w),
        ];
        using (var fill = new LinearGradientBrush(new RectangleF(cx - s, cy - s, s * 2, s * 2), Color.FromArgb(A(220, fade), Hex("#f0d48a")), Color.FromArgb(A(220, fade), Hex("#7a5a22")), LinearGradientMode.Vertical))
            g.FillPolygon(fill, star);
        using (var edge = new Pen(Color.FromArgb(A(200, fade), Hex("#3a2a0a")), 1))
            g.DrawPolygon(edge, star);
        Diamond(g, Color.FromArgb(A(255, fade), Hex("#14110c")), cx, cy, w * 1.1f, w * 1.1f, Color.FromArgb(A(255, fade), Hex("#e6c77c")));
    }

    private static int A(int alpha, float fade) => Math.Clamp((int)(alpha * fade), 0, 255);
}

/// <summary>The round portrait in the Character page header (a crest while not in game).</summary>
internal sealed class PortraitBox : Control
{
    private readonly IconCache _icons;
    private string? _portrait;

    public PortraitBox(IconCache icons)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        _icons = icons;
        _icons.Loaded += Invalidate;
    }

    /// <summary>The portrait's path in Media.pk2 (null = the crest).</summary>
    public string? Portrait
    {
        get => _portrait;
        set
        {
            if (value == _portrait)
                return;
            _portrait = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float size = Math.Min(Width, Height) - 12;
        var disc = new RectangleF(6, (Height - size) / 2, size, size);
        PortraitArt.DrawRound(e.Graphics, disc, _icons.Get(_portrait), _portrait is null ? 0.8f : 1f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _icons.Loaded -= Invalidate;
        base.Dispose(disposing);
    }
}
