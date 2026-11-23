using System.Drawing.Drawing2D;
using NexBot.App.Theme;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>The top bar: gold "NEXBOT" title and a state pill, over a gold rule.</summary>
internal sealed class SroHeader : Control
{
    private readonly Font _title = Title(22f);
    private readonly Font _subtitle = new("Segoe UI", 7.5f, FontStyle.Bold);
    private string _state = "IDLE";
    private Color _stateColor = Muted;

    public SroHeader()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 68;
        Dock = DockStyle.Top;
    }

    public void SetState(string text, Color color)
    {
        _state = text.ToUpperInvariant();
        _stateColor = color;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var bg = Vertical(r, (Hex("#1d1a14"), 0f), (Hex("#0d0c0a"), 0.7f), (Hex("#070706"), 1f)))
            g.FillRectangle(bg, r);
        g.FillRectangle(Noise, r);

        // Gold rule: dark line, bright gold fading to the sides, dark line.
        int y = Height - 4;
        using (var dark = new Pen(Color.Black))
            g.DrawLine(dark, 0, y - 1, Width, y - 1);
        using (var rule = new LinearGradientBrush(new Rectangle(0, y, Math.Max(1, Width), 2), GoldDim, GoldDim, LinearGradientMode.Horizontal))
        {
            rule.InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(40, GoldDim), Gold, Gold, Color.FromArgb(40, GoldDim)],
                Positions = [0f, 0.25f, 0.75f, 1f],
            };
            g.FillRectangle(rule, 0, y, Width, 2);
        }
        using (var below = new SolidBrush(Hex("#050505")))
            g.FillRectangle(below, 0, y + 2, Width, 2);

        // Title with a gold gradient and a soft shadow.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var titleRect = new RectangleF(22, 6, 230, 40);
        using (var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
            DrawSpaced(g, "NEXBOT", _title, shadow, titleRect with { X = titleRect.X + 2, Y = titleRect.Y + 2 }, 5f, center: false);
        using (var gold = new LinearGradientBrush(new RectangleF(0, titleRect.Y + 6, 10, 30), Hex("#fff1c0"), Hex("#b8862e"), LinearGradientMode.Vertical))
            DrawSpaced(g, "NEXBOT", _title, gold, titleRect, 5f, center: false);
        using (var muted = new SolidBrush(Muted))
            DrawSpaced(g, "SILKROAD ONLINE  ·  BOT", _subtitle, muted, new RectangleF(24, 44, 230, 14), 1.4f, center: false);

        // State pill (right).
        var size = TextRenderer.MeasureText(_state, Tiny);
        var pill = new Rectangle(Width - size.Width - 52, (Height - 4 - 26) / 2, size.Width + 34, 26);
        using (var back = new SolidBrush(Hex("#0c0b09")))
            g.FillRectangle(back, pill);
        StrokeRect(g, Line, pill);
        using (var dot = new SolidBrush(_stateColor))
            g.FillEllipse(dot, pill.X + 10, pill.Y + 9, 8, 8);
        using (var glow = new SolidBrush(Color.FromArgb(60, _stateColor)))
            g.FillEllipse(glow, pill.X + 7, pill.Y + 6, 14, 14);
        TextRenderer.DrawText(g, _state, Tiny, new Rectangle(pill.X + 24, pill.Y, pill.Width - 26, pill.Height), _stateColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _title.Dispose();
            _subtitle.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The bottom strip with "Caption value" segments.</summary>
internal sealed class SroStatusBar : Control
{
    private (string Caption, string Value, Color Color)[] _segments = [];

    public SroStatusBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 26;
        Dock = DockStyle.Bottom;
        Font = SroTheme.Small;
    }

    public void SetSegments(params (string Caption, string Value, Color Color)[] segments)
    {
        _segments = segments;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var bg = Vertical(r, Hex("#14120e"), Hex("#080706")))
            g.FillRectangle(bg, r);
        using (var top = new Pen(Line))
            g.DrawLine(top, 0, 0, Width, 0);

        int x = 14;
        foreach (var (caption, value, color) in _segments)
        {
            var cs = TextRenderer.MeasureText(caption, Font);
            TextRenderer.DrawText(g, caption, Font, new Point(x, (Height - cs.Height) / 2), Muted);
            x += cs.Width;
            var vs = TextRenderer.MeasureText(value, Font);
            TextRenderer.DrawText(g, value, Font, new Point(x, (Height - vs.Height) / 2), color);
            x += vs.Width + 10;
            Diamond(g, GoldDim, x, Height / 2f, 2.5f, 2.5f);
            x += 14;
        }
    }
}
