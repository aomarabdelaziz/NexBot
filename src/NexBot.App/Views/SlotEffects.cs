using System.Drawing.Drawing2D;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// The animated slot effects of the game's inventory: the glow that fades in and out on a pet that is summoned (in
/// use), and the light that circles a rare (Seal of Star / Moon / Sun) item. One shared timer repaints the slots
/// that show an effect, and only while they are visible.
/// </summary>
internal static class SlotEffects
{
    private const int FrameMs = 40;
    private static readonly HashSet<Control> s_animated = [];
    private static System.Windows.Forms.Timer? s_timer;

    /// <summary>Milliseconds on the shared animation clock.</summary>
    public static long Now => Environment.TickCount64;

    /// <summary>Repaint <paramref name="control"/> every frame while <paramref name="on"/>.</summary>
    public static void Animate(Control control, bool on)
    {
        if (on ? !s_animated.Add(control) : !s_animated.Remove(control))
            return;
        if (s_animated.Count > 0)
        {
            s_timer ??= CreateTimer();
            s_timer.Start();
        }
        else
            s_timer?.Stop();
    }

    private static System.Windows.Forms.Timer CreateTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = FrameMs };
        timer.Tick += (_, _) =>
        {
            foreach (var control in s_animated)
                if (!control.IsDisposed && control.Visible && control.FindForm() is { WindowState: not FormWindowState.Minimized, Visible: true })
                    control.Invalidate();
        };
        return timer;
    }

    /// <summary>A warm glow along the inside of <paramref name="r"/> that fades in and out (about every 1.6 s).</summary>
    public static void DrawInUse(Graphics g, Rectangle r)
    {
        double phase = Now % 1600 / 1600.0;
        float pulse = (float)(0.5 - 0.5 * Math.Cos(phase * 2 * Math.PI)); // 0 → 1 → 0
        if (pulse < 0.02f)
            return;
        g.SmoothingMode = SmoothingMode.None;
        var glow = Hex("#ffcf6a");
        for (int i = 0; i < 5; i++)
        {
            int alpha = (int)((200 - i * 42) * pulse);
            if (alpha <= 0)
                continue;
            using var pen = new Pen(Color.FromArgb(alpha, glow));
            g.DrawRectangle(pen, r.X + i, r.Y + i, r.Width - 1 - 2 * i, r.Height - 1 - 2 * i);
        }
        // A soft wash over the icon at the brightest moment.
        using var wash = new SolidBrush(Color.FromArgb((int)(40 * pulse), glow));
        g.FillRectangle(wash, r);
    }

    /// <summary>Two bright streaks circling the border of <paramref name="r"/> (one turn every 2.4 s), with a sparkle at the head.</summary>
    public static void DrawRare(Graphics g, Rectangle r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = RectangleF.Inflate(r, -1.5f, -1.5f);
        double head = Now % 2400 / 2400.0;
        foreach (double start in new[] { head, (head + 0.5) % 1 })
        {
            const int steps = 18;
            const double length = 0.22;
            var previous = PointOn(box, start - length);
            for (int i = 1; i <= steps; i++)
            {
                double at = start - length + length * i / steps;
                var point = PointOn(box, at);
                float t = i / (float)steps; // 0 tail → 1 head
                using var pen = new Pen(Color.FromArgb((int)(230 * t * t), Hex("#fff0b0")), 0.8f + 2.2f * t) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, previous, point);
                previous = point;
            }
            Sparkle(g, previous, 4.5f);
        }
    }

    /// <summary>A point on the outline of <paramref name="r"/>, going clockwise from the top-left corner (0-1 = one turn).</summary>
    private static PointF PointOn(RectangleF r, double at)
    {
        at -= Math.Floor(at);
        double perimeter = 2 * (r.Width + r.Height), d = at * perimeter;
        if (d < r.Width)
            return new PointF(r.Left + (float)d, r.Top);
        d -= r.Width;
        if (d < r.Height)
            return new PointF(r.Right, r.Top + (float)d);
        d -= r.Height;
        if (d < r.Width)
            return new PointF(r.Right - (float)d, r.Bottom);
        d -= r.Width;
        return new PointF(r.Left, r.Bottom - (float)d);
    }

    private static void Sparkle(Graphics g, PointF c, float size)
    {
        using (var halo = new GraphicsPath())
        {
            halo.AddEllipse(c.X - size, c.Y - size, size * 2, size * 2);
            using var brush = new PathGradientBrush(halo) { CenterColor = Color.FromArgb(220, Color.White), SurroundColors = [Color.FromArgb(0, Hex("#ffd870"))] };
            g.FillPath(brush, halo);
        }
        float w = size * 0.28f;
        PointF[] star =
        [
            new(c.X, c.Y - size), new(c.X + w, c.Y - w), new(c.X + size, c.Y), new(c.X + w, c.Y + w),
            new(c.X, c.Y + size), new(c.X - w, c.Y + w), new(c.X - size, c.Y), new(c.X - w, c.Y - w),
        ];
        using var fill = new SolidBrush(Color.FromArgb(240, Color.White));
        g.FillPolygon(fill, star);
    }
}
