using System.Globalization;
using NexBot.App.Theme;
using NexBot.Map;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// The character's buffs as a row of icons, like the bar next to the portrait in the game: skill buffs and scroll /
/// item effects. The time left is written on each icon; hovering shows the game's buff card (name and level, duration,
/// effects, time left).
/// </summary>
internal sealed class BuffBar : Control
{
    private const int IconSize = 30, Spacing = 4;
    private readonly IconCache _icons;
    private IReadOnlyList<BuffView> _buffs = [];
    private string _signature = "";
    private int _hover = -1;

    public BuffBar(IconCache icons)
    {
        _icons = icons;
        _icons.Loaded += Invalidate;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = IconSize;
    }

    public void SetBuffs(IReadOnlyList<BuffView> buffs)
    {
        _buffs = buffs;
        string signature = string.Join(";", buffs.Select(b => $"{b.Id}:{TimeLeft(b)}"));
        if (signature != _signature)
        {
            _signature = signature;
            FitHeight();
            Invalidate();
        }
        // Keep the open card's time left running.
        if (_hover >= _buffs.Count)
        {
            _hover = -1;
            ItemPreview.Hide();
        }
        else if (_hover >= 0)
            ShowCard(_hover);
    }

    /// <summary>The card the game shows over a buff icon.</summary>
    internal static ItemPreviewInfo Card(BuffView b)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        if (b.Duration > TimeSpan.Zero)
            lines.Add(string.Create(inv, $"Duration {b.Duration.TotalSeconds:0} seconds{(b.Duration.TotalMinutes >= 1 ? $"  ({Span(b.Duration)})" : "")}"));
        if (b.Effects is { Count: > 0 } effects)
        {
            lines.Add("");
            lines.AddRange(effects);
        }
        lines.Add("");
        lines.Add(b.Remaining is { } r ? "Remaining time " + Span(r) : "Remaining time unknown (on since login)");
        return new ItemPreviewInfo(b.Icon, b.Level > 0 ? $"{b.Name} Lv {b.Level}" : b.Name, string.Join("\n", lines));
    }

    /// <summary>"27 minutes 17 seconds", "1 hour 5 minutes".</summary>
    private static string Span(TimeSpan t)
    {
        static string Unit(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
        if (t.TotalHours >= 1)
            return Unit((int)t.TotalHours, "hour") + (t.Minutes > 0 ? " " + Unit(t.Minutes, "minute") : "");
        if (t.TotalMinutes >= 1)
            return Unit(t.Minutes, "minute") + (t.Seconds > 0 ? " " + Unit(t.Seconds, "second") : "");
        return Unit(t.Seconds, "second");
    }

    private void ShowCard(int i) => ItemPreview.Show(this, Card(_buffs[i]), _icons);

    private int PerRow => Math.Max(1, (Width + Spacing) / (IconSize + Spacing));

    private void FitHeight()
    {
        int rows = Math.Max(1, (_buffs.Count + PerRow - 1) / PerRow);
        int height = rows * IconSize + (rows - 1) * Spacing;
        if (Height != height)
            Height = height;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitHeight();
    }

    private Rectangle Box(int i) => new(i % PerRow * (IconSize + Spacing), i / PerRow * (IconSize + Spacing), IconSize, IconSize);

    /// <summary>"12m", "45s", "1h" or "" when the time left is unknown.</summary>
    private static string TimeLeft(BuffView b) => b.Remaining switch
    {
        null => "",
        { TotalHours: >= 1 } t => $"{(int)t.TotalHours}h",
        { TotalMinutes: >= 1 } t => $"{(int)t.TotalMinutes}m",
        { } t => $"{(int)t.TotalSeconds}s",
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_buffs.Count == 0)
        {
            TextRenderer.DrawText(g, "No buffs.", SroTheme.Small, ClientRectangle, Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            return;
        }
        for (int i = 0; i < _buffs.Count; i++)
        {
            var b = _buffs[i];
            var box = Box(i);
            ItemVisuals.DrawIcon(g, _icons, b.Icon, box);
            // Fading out like the game: dim the icon in its last 10 seconds.
            if (b.Remaining is { TotalSeconds: < 10 })
            {
                using var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
                g.FillRectangle(dim, box);
            }
            string left = TimeLeft(b);
            if (left.Length > 0)
            {
                var text = new Rectangle(box.X, box.Bottom - 13, box.Width, 13);
                TextRenderer.DrawText(g, left, SroTheme.Tiny, new Rectangle(text.X + 1, text.Y + 1, text.Width, text.Height), Color.Black,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, left, SroTheme.Tiny, text, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
            }
            if (i == _hover)
                StrokeRect(g, Gold, box);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = -1;
        for (int i = 0; i < _buffs.Count; i++)
            if (Box(i).Contains(e.Location))
                hit = i;
        if (hit == _hover)
            return;
        _hover = hit;
        Invalidate();
        if (hit < 0)
            ItemPreview.Hide();
        else
            ShowCard(hit);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        ItemPreview.Hide();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icons.Loaded -= Invalidate;
        }
        base.Dispose(disposing);
    }
}
