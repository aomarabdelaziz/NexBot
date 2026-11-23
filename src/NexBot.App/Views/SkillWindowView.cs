using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using NexBot.App.Theme;
using NexBot.Configuration;
using NexBot.Map;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// The game's skill window: Weapon / Force tabs, a sub-tab per mastery, the mastery header (icon, name, level and the
/// raise button) and the skill lines. Each slot shows the skill icon, its level and "MAX" or an up button that learns
/// the next level. The footer shows the skill points and the mastery level total.
/// </summary>
internal sealed class SkillWindowView : Control
{
    private const int TabHeight = 26;
    private const int SubTabHeight = 24;
    private const int HeaderHeight = 58;
    private const int FooterHeight = 50;
    private const int LineHeight = 64;
    private const int Slot = 36;
    private const int SlotPitch = 46;
    private const int LineHeader = 40;

    private enum HitKind { Tab, Mastery, Raise, Learn, Slot, AutoMastery, AllSkills }

    private sealed record Hit(Rectangle Bounds, HitKind Kind, int Index, SkillSlot? Slot = null);

    private readonly Func<SroClient?> _game;
    private readonly IconCache _icons;
    private readonly Font _titleFont = Title(12f);
    private readonly Font _levelFont = Title(10f);
    private readonly List<Hit> _hits = [];
    private readonly ImageAttributes _grey = Greyscale(0.55f);
    private readonly ImageAttributes _dark = Greyscale(0.28f);
    private SkillWindow? _window;
    private int _tab;
    private int _mastery;
    private bool _picked;
    private int _scroll;
    private int _maxScroll;
    private Point _mouse = new(-1, -1);
    private bool _busy;
    private string _message = "";
    private Color _messageColor = Muted;
    private AutoLevelSettings? _auto;

    public SkillWindowView(Func<SroClient?> game, IconCache icons)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        _game = game;
        _icons = icons;
        _icons.Loaded += Invalidate;
    }

    /// <summary>The auto-level choices shown (AUTO tags, "A" badges) and changed here; null hides them.</summary>
    public AutoLevelSettings? Auto
    {
        get => _auto;
        set
        {
            _auto = value;
            Invalidate();
        }
    }

    /// <summary>Raised when a mastery or skill was added to / removed from the auto-level choices.</summary>
    public event EventHandler? AutoChanged;

    private bool IsAuto(SkillSlot slot) =>
        _auto is { } auto && (auto.SkillGroups.Contains(slot.Group) || (Page is { } page && auto.AllSkillMasteries.Contains(page.Id)));

    private bool IsAllSkills(MasteryPage page) => _auto?.AllSkillMasteries.Contains(page.Id) == true;

    private void ToggleAllSkills(MasteryPage page)
    {
        if (_auto is null)
            return;
        bool on = !_auto.AllSkillMasteries.Remove(page.Id);
        if (on)
            _auto.AllSkillMasteries.Add(page.Id);
        Say(on ? $"Every skill of {page.Name} will be learned automatically (locked ones wait)." : $"{page.Name}: all skills off; right-clicked skills stay auto.", Gold);
        AutoChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsAuto(MasteryPage page) => _auto?.MasteryIds.Contains(page.Id) == true;

    private void ToggleAuto(SkillSlot slot)
    {
        if (_auto is null)
            return;
        if (Page is { } current && _auto.AllSkillMasteries.Contains(current.Id))
        {
            Say($"ALL SKILLS is on for {current.Name}: every skill here is learned automatically.", Gold);
            return;
        }
        bool on = !_auto.SkillGroups.Remove(slot.Group);
        if (on)
            _auto.SkillGroups.Add(slot.Group);
        Say(on ? $"{slot.Name} will be learned automatically." : $"{slot.Name}: automatic learning off.", Gold);
        AutoChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleAuto(MasteryPage page)
    {
        if (_auto is null)
            return;
        bool on = !_auto.MasteryIds.Remove(page.Id);
        if (on)
            _auto.MasteryIds.Add(page.Id);
        Say(on ? $"{page.Name} will be raised automatically." : $"{page.Name}: automatic raise off.", Gold);
        AutoChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetWindow(SkillWindow? window)
    {
        _window = window;
        if (window is null)
            _picked = false;
        else if (!_picked && window.Tabs.Count > 0)
        {
            // Open on the highest mastery, as a player would look first.
            var best = window.Tabs.SelectMany((t, ti) => t.Masteries.Select((m, mi) => (m.Level, ti, mi)))
                .OrderByDescending(x => x.Level).First();
            (_tab, _mastery, _picked) = (best.ti, best.mi, true);
        }
        Invalidate();
    }

    private MasteryPage? Page =>
        _window is { Tabs.Count: > 0 } w && _tab < w.Tabs.Count && _mastery < w.Tabs[_tab].Masteries.Count
            ? w.Tabs[_tab].Masteries[_mastery]
            : null;

    // ---- input ------------------------------------------------------------------------------------

    private Hit? HitAt(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Bounds.Contains(p))
                return _hits[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.Location;
        var hit = HitAt(e.Location);
        Cursor = hit is { Kind: HitKind.Tab or HitKind.Mastery or HitKind.Raise or HitKind.Learn or HitKind.AutoMastery or HitKind.AllSkills } ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _mouse = new Point(-1, -1);
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        Focus();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * LineHeight / 2, 0, _maxScroll);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (HitAt(e.Location) is not { } hit)
            return;
        if (e.Button == MouseButtons.Right)
        {
            if (hit.Slot is { } picked)
                ToggleAuto(picked);
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;
        switch (hit.Kind)
        {
            case HitKind.Tab when hit.Index != _tab:
                (_tab, _mastery, _scroll) = (hit.Index, 0, 0);
                Invalidate();
                break;
            case HitKind.Mastery when hit.Index != _mastery:
                (_mastery, _scroll) = (hit.Index, 0);
                Invalidate();
                break;
            case HitKind.Raise when Page is { } page:
                _ = RunAsync(page.RaiseMissing, c => c.RaiseMasteryAsync(page.Id));
                break;
            case HitKind.AutoMastery when Page is { } autoPage:
                ToggleAuto(autoPage);
                break;
            case HitKind.AllSkills when Page is { } allPage:
                ToggleAllSkills(allPage);
                break;
            case HitKind.Learn when hit.Slot is { } slot:
                _ = RunAsync(slot.Missing, c => c.LearnSkillAsync(slot.NextId));
                break;
        }
    }

    private async Task RunAsync(string? missing, Func<SroClient, Task<SkillResult>> action)
    {
        if (_busy)
            return;
        if (missing is not null)
        {
            Say(missing, Warn);
            return;
        }
        if (_game() is not { } client)
        {
            Say("The bot is not connected.", Bad);
            return;
        }

        _busy = true;
        Say("Waiting for the server…", Muted);
        try
        {
            var result = await action(client).ConfigureAwait(true);
            Say(result.Message, result.Ok ? Good : Bad);
            if (result.Ok)
                SetWindow(SkillWindow.Take(client));
        }
        finally
        {
            _busy = false;
            Invalidate();
        }
    }

    private void Say(string message, Color color)
    {
        (_message, _messageColor) = (message, color);
        Invalidate();
    }

    // ---- painting ---------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        _hits.Clear();

        if (_window is null || _window.NeedsRebuild || _window.Tabs.Count == 0)
        {
            string text = _window is null ? "Log in to see the skills."
                : _window.NeedsRebuild ? "This game database has no skill window layout.\nOpen Server and click \"Build database\", then restart the bot."
                : "No masteries for this character.";
            TextRenderer.DrawText(g, text, SroTheme.Body, ClientRectangle, Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        var w = _window;
        _tab = Math.Min(_tab, w.Tabs.Count - 1);
        _mastery = Math.Min(_mastery, Math.Max(0, w.Tabs[_tab].Masteries.Count - 1));

        int y = 0;
        y = PaintTabs(g, w, y);
        y = PaintMasteryTabs(g, w.Tabs[_tab], y);
        if (Page is not { } page)
            return;
        y = PaintHeader(g, page, y);
        PaintLines(g, page, new Rectangle(0, y + 4, Width, Height - FooterHeight - y - 8));
        PaintFooter(g, w, new Rectangle(0, Height - FooterHeight, Width, FooterHeight));
        PaintTooltip(g, page);
    }

    private int PaintTabs(Graphics g, SkillWindow w, int y)
    {
        int width = Math.Min(130, Width / Math.Max(1, w.Tabs.Count));
        for (int i = 0; i < w.Tabs.Count; i++)
        {
            var r = new Rectangle(i * (width + 2), y, width, TabHeight);
            bool selected = i == _tab, hover = r.Contains(_mouse);
            PaintTab(g, r, w.Tabs[i].Name, selected, hover, _titleFont.FontFamily, 9.5f);
            _hits.Add(new Hit(r, HitKind.Tab, i));
        }
        using (var pen = new Pen(Frame))
            g.DrawLine(pen, 0, y + TabHeight, Width, y + TabHeight);
        return y + TabHeight + 4;
    }

    private int PaintMasteryTabs(Graphics g, SkillTab tab, int y)
    {
        int width = Math.Min(140, Width / Math.Max(1, tab.Masteries.Count));
        for (int i = 0; i < tab.Masteries.Count; i++)
        {
            var r = new Rectangle(i * (width + 2), y, width, SubTabHeight);
            string name = tab.Masteries[i].Name.Replace(" Mastery", "", StringComparison.OrdinalIgnoreCase);
            PaintTab(g, r, name, i == _mastery, r.Contains(_mouse), null, 9f);
            _hits.Add(new Hit(r, HitKind.Mastery, i));
        }
        return y + SubTabHeight + 4;
    }

    private static void PaintTab(Graphics g, Rectangle r, string text, bool selected, bool hover, FontFamily? family, float size)
    {
        using var path = Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height + 4), 4);
        using (var fill = selected ? Vertical(r, Hex("#6b5a35"), Hex("#2a2214"))
                   : hover ? Vertical(r, Hex("#3e3a32"), Hex("#1d1b17"))
                   : Vertical(r, Hex("#2a2824"), Hex("#121110")))
            g.FillPath(fill, path);
        using (var pen = new Pen(selected ? GoldDim : FrameDark))
            g.DrawPath(pen, path);
        if (selected)
            using (var shine = new Pen(Color.FromArgb(90, Gold)))
                g.DrawLine(shine, r.X + 4, r.Y + 1, r.Right - 5, r.Y + 1);

        using var font = family is null ? new Font("Segoe UI", size, FontStyle.Bold) : new Font(family, size, FontStyle.Bold);
        using var brush = new SolidBrush(selected ? Gold : hover ? SroTheme.Text : Muted);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, font, brush, r, format);
    }

    private int PaintHeader(Graphics g, MasteryPage page, int y)
    {
        var box = new Rectangle(0, y, Width - 1, HeaderHeight - 6);
        using (var fill = Vertical(box, Hex("#1c1a16"), Hex("#0c0b09")))
            g.FillRectangle(fill, box);
        StrokeRect(g, Line, box);

        var iconRect = new Rectangle(8, y + 7, 38, 38);
        PaintSocket(g, iconRect, GoldDim);
        if (_icons.Get(page.Icon) is { } icon)
            g.DrawImage(icon, Rectangle.Inflate(iconRect, -2, -2));

        using (var gold = new SolidBrush(Gold))
            g.DrawString(page.Name.Contains("Mastery", StringComparison.OrdinalIgnoreCase) ? page.Name : page.Name + " Mastery",
                _titleFont, gold, new PointF(54, y + 7));
        string cost = page.RaiseMissing ?? string.Create(CultureInfo.CurrentCulture, $"Next level costs {page.RaiseSp:N0} SP");
        TextRenderer.DrawText(g, cost, SroTheme.Small, new Rectangle(55, y + 30, Width - (_auto is null ? 230 : 372), 18),
            page.RaiseMissing is null ? Muted : Hex("#8a8170"), TextFormatFlags.EndEllipsis | TextFormatFlags.Left);

        // "Lv 101" plate and the raise button, right aligned like the game.
        var raise = new Rectangle(Width - 36, y + 14, 24, 24);
        var plate = new Rectangle(raise.X - 92, y + 13, 84, 26);
        using (var fill = Vertical(plate, Hex("#0a0908"), Hex("#16140f")))
            g.FillRectangle(fill, plate);
        StrokeRect(g, Hex("#5a4f38"), plate);
        using (var muted = new SolidBrush(Muted))
        using (var value = new SolidBrush(Value))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center })
        {
            g.DrawString("Lv", SroTheme.Small, muted, new RectangleF(plate.X + 8, plate.Y, 26, plate.Height), format);
            format.Alignment = StringAlignment.Far;
            g.DrawString(page.Level.ToString(CultureInfo.InvariantCulture), _levelFont, value, new RectangleF(plate.X, plate.Y, plate.Width - 8, plate.Height), format);
        }
        PaintUpButton(g, raise, page.RaiseMissing is null && !_busy, raise.Contains(_mouse));
        _hits.Add(new Hit(raise, HitKind.Raise, 0));
        if (_auto is not null)
        {
            var tag = new Rectangle(plate.X - 58, plate.Y + 3, 50, 20);
            PaintAutoTag(g, tag, IsAuto(page), tag.Contains(_mouse), "AUTO");
            _hits.Add(new Hit(tag, HitKind.AutoMastery, 0));
            var all = new Rectangle(tag.X - 82, tag.Y, 76, 20);
            PaintAutoTag(g, all, IsAllSkills(page), all.Contains(_mouse), "ALL SKILLS");
            _hits.Add(new Hit(all, HitKind.AllSkills, 0));
        }
        return y + HeaderHeight;
    }

    private void PaintLines(Graphics g, MasteryPage page, Rectangle area)
    {
        int slots = page.Lines.Count == 0 ? SkillWindow.SlotsPerLine : page.Lines.Max(l => l.Slots.Count);
        int gridWidth = LineHeader + 10 + slots * SlotPitch;
        int left = Math.Max(4, (area.Width - gridWidth) / 2);
        _maxScroll = Math.Max(0, page.Lines.Count * LineHeight - area.Height);
        _scroll = Math.Min(_scroll, _maxScroll);

        var state = g.Save();
        g.SetClip(area);
        for (int i = 0; i < page.Lines.Count; i++)
        {
            var line = page.Lines[i];
            int top = area.Y + i * LineHeight - _scroll;
            if (top + LineHeight < area.Y || top > area.Bottom)
                continue;

            // Line plate: a dark bar behind the row, with the line's first skill as its header icon.
            var plate = new Rectangle(left - 4, top + 2, gridWidth + 8, Slot + 6);
            using (var fill = new LinearGradientBrush(plate, Color.FromArgb(70, 60, 50, 32), Color.FromArgb(10, 0, 0, 0), LinearGradientMode.Horizontal))
                g.FillRectangle(fill, plate);
            var head = new Rectangle(left, top + 5, LineHeader - 4, LineHeader - 4);
            PaintSocket(g, head, Hex("#5a4f38"));
            var first = line.Slots.FirstOrDefault(s => s is not null);
            if (first is not null && _icons.Get(first.Icon) is { } headIcon)
                g.DrawImage(headIcon, Rectangle.Inflate(head, -3, -3), 0, 0, headIcon.Width, headIcon.Height, GraphicsUnit.Pixel, _grey);
            Diamond(g, GoldDim, left + LineHeader + 3, top + 5 + Slot / 2f, 3, 3);

            for (int s = 0; s < line.Slots.Count; s++)
            {
                var rect = new Rectangle(left + LineHeader + 10 + s * SlotPitch, top + 5, Slot, Slot);
                PaintSlot(g, rect, line.Slots[s]);
            }
        }
        g.Restore(state);

        if (_maxScroll > 0)
        {
            // thin scroll gauge on the right edge
            var track = new Rectangle(Width - 4, area.Y, 3, area.Height);
            using (var back = new SolidBrush(Hex("#15130f")))
                g.FillRectangle(back, track);
            int thumb = Math.Max(20, area.Height * area.Height / (area.Height + _maxScroll));
            int at = area.Y + (area.Height - thumb) * _scroll / _maxScroll;
            using var gold = new SolidBrush(GoldDim);
            g.FillRectangle(gold, track.X, at, track.Width, thumb);
        }
    }

    private void PaintSlot(Graphics g, Rectangle rect, SkillSlot? slot)
    {
        if (slot is null)
        {
            PaintSocket(g, rect, Hex("#24221d"));
            return;
        }

        bool hover = rect.Contains(_mouse);
        var border = slot.State switch
        {
            SkillSlotState.Max => Gold,
            SkillSlotState.Upgradable or SkillSlotState.Learnable => Good,
            SkillSlotState.Learned => GoldDim,
            _ => Hex("#3a352a"),
        };
        PaintSocket(g, rect, hover ? Gold : border);
        if (_icons.Get(slot.Icon) is { } icon)
        {
            var inner = Rectangle.Inflate(rect, -2, -2);
            if (slot.Level > 0)
                g.DrawImage(icon, inner);
            else
                g.DrawImage(icon, inner, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel,
                    slot.State == SkillSlotState.Learnable ? _grey : _dark);
        }
        _hits.Add(new Hit(rect, HitKind.Slot, 0, slot));
        if (IsAuto(slot))
        {
            var badge = new Rectangle(rect.X - 3, rect.Y - 3, 13, 13);
            using (var fill = Vertical(badge, Hex("#f5dc90"), Hex("#9a7a3c")))
                g.FillEllipse(fill, badge);
            using (var pen = new Pen(Hex("#3a2a0a")))
                g.DrawEllipse(pen, badge);
            TextRenderer.DrawText(g, "A", SroTheme.Tiny, badge, Hex("#2a1c04"), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Level number at the icon's bottom-right corner, as in the game.
        if (slot.Level > 0)
        {
            string level = slot.Level.ToString(CultureInfo.InvariantCulture);
            var size = TextRenderer.MeasureText(level, SroTheme.Tiny);
            var tag = new Rectangle(rect.Right - size.Width + 2, rect.Bottom - 13, size.Width - 3, 12);
            using (var back = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                g.FillRectangle(back, tag);
            TextRenderer.DrawText(g, level, SroTheme.Tiny, tag, Value, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Below: "MAX", or the up button that learns the next level.
        var below = new Rectangle(rect.X, rect.Bottom + 3, rect.Width, 14);
        if (slot.State == SkillSlotState.Max)
        {
            TextRenderer.DrawText(g, "MAX", SroTheme.Tiny, below, Gold, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }
        var button = new Rectangle(rect.X + (rect.Width - 16) / 2, below.Y, 16, 14);
        bool enabled = slot.Missing is null && !_busy;
        PaintUpButton(g, button, enabled, button.Contains(_mouse));
        _hits.Add(new Hit(Rectangle.Inflate(button, 3, 2), HitKind.Learn, 0, slot));
    }

    private static void PaintAutoTag(Graphics g, Rectangle r, bool on, bool hover, string text)
    {
        using var path = Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 3);
        using (var fill = on ? Vertical(r, Hex("#e8c97a"), Hex("#7a5a22")) : Vertical(r, hover ? Hex("#34312a") : Hex("#211f1b"), Hex("#121110")))
            g.FillPath(fill, path);
        using (var pen = new Pen(on ? Hex("#3a2a0a") : hover ? GoldDim : Hex("#4a4436")))
            g.DrawPath(pen, path);
        TextRenderer.DrawText(g, text, SroTheme.Tiny, r, on ? Hex("#2a1c04") : hover ? Gold : Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static void PaintSocket(Graphics g, Rectangle r, Color border)
    {
        using (var well = Vertical(r, Hex("#070605"), Hex("#16140f")))
            g.FillRectangle(well, r);
        StrokeRect(g, border, r);
        using var inner = new Pen(Color.FromArgb(120, 0, 0, 0));
        g.DrawRectangle(inner, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
    }

    private static void PaintUpButton(Graphics g, Rectangle r, bool enabled, bool hover)
    {
        using var path = Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 3);
        using (var fill = !enabled ? Vertical(r, Hex("#2b2925"), Hex("#161512"))
                   : hover ? Vertical(r, Hex("#fff0b0"), Hex("#b08a3a"))
                   : Vertical(r, Hex("#e8c97a"), Hex("#7a5a22")))
            g.FillPath(fill, path);
        using (var pen = new Pen(enabled ? Hex("#3a2a0a") : Hex("#3a372f")))
            g.DrawPath(pen, path);

        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = Math.Min(r.Width, r.Height) * 0.28f;
        PointF[] arrow = [new(cx, cy - s), new(cx + s * 1.25f, cy + s * 0.8f), new(cx - s * 1.25f, cy + s * 0.8f)];
        using var brush = new SolidBrush(enabled ? Hex("#2a1c04") : Hex("#5a564c"));
        g.FillPolygon(brush, arrow);
    }

    private void PaintFooter(Graphics g, SkillWindow w, Rectangle r)
    {
        using (var pen = new Pen(Line))
            g.DrawLine(pen, r.X, r.Y, r.Right, r.Y);
        var inv = CultureInfo.CurrentCulture;
        PaintField(g, new Rectangle(r.X + 4, r.Y + 6, Math.Min(220, r.Width / 2 - 8), 20), "Skill Point", w.Sp.ToString("N0", inv), Value);
        bool full = w.MasteryTotal >= w.MasteryLimit;
        PaintField(g, new Rectangle(r.Right - Math.Min(240, r.Width / 2 - 8) - 4, r.Y + 6, Math.Min(240, r.Width / 2 - 8), 20),
            "Mastery level total", string.Create(inv, $"{w.MasteryTotal}/{w.MasteryLimit}"), full ? Warn : Value);

        string message = _message.Length > 0 ? _message
            : _auto is null ? "Up buttons learn the next skill level or raise the mastery."
            : "Right-click a skill or click AUTO to level it automatically.";
        TextRenderer.DrawText(g, message, SroTheme.Small, new Rectangle(r.X + 4, r.Y + 29, r.Width - 8, 18),
            _message.Length > 0 ? _messageColor : Hex("#7d7564"), TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }

    private static void PaintField(Graphics g, Rectangle r, string caption, string value, Color color)
    {
        using (var fill = Vertical(r, Hex("#0a0908"), Hex("#15130f")))
            g.FillRectangle(fill, r);
        StrokeRect(g, Hex("#4a4436"), r);
        TextRenderer.DrawText(g, caption, SroTheme.Small, new Rectangle(r.X + 6, r.Y, r.Width, r.Height), Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, value, SroTheme.BodyBold, new Rectangle(r.X, r.Y, r.Width - 6, r.Height), color, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    /// <summary>A game-style tooltip next to the hovered skill.</summary>
    private void PaintTooltip(Graphics g, MasteryPage page)
    {
        if (HitAt(_mouse) is not { Slot: { } slot } hit)
            return;

        var inv = CultureInfo.CurrentCulture;
        var rows = new List<(string Text, Font Font, Color Color)>
        {
            (slot.Name, SroTheme.BodyBold, Gold),
            (slot.Level > 0 ? $"Level {slot.Level} / {slot.MaxLevel}" : $"Not learned  ·  {slot.MaxLevel} levels", SroTheme.Small, SroTheme.Text),
        };
        if (slot.NextId != 0)
        {
            rows.Add((string.Create(inv, $"Next level: {slot.NextSp:N0} SP  ·  {page.Name.Replace(" Mastery", "", StringComparison.OrdinalIgnoreCase)} Lv. {slot.NextMasteryLevel}"), SroTheme.Small, Muted));
            rows.Add(slot.Missing is null ? ("Click the up button to learn it.", SroTheme.Small, Good) : (slot.Missing, SroTheme.Small, Warn));
        }
        else
            rows.Add(("Highest level learned.", SroTheme.Small, Gold));
        if (_auto is not null)
            rows.Add((Page is { } p && _auto.AllSkillMasteries.Contains(p.Id) ? "Auto: on  (ALL SKILLS of this mastery)"
                : IsAuto(slot) ? "Auto: on  (right-click to turn off)" : "Right-click: learn automatically", SroTheme.Small, IsAuto(slot) ? Gold : Hex("#7d7564")));

        int width = 0, height = 8;
        foreach (var row in rows)
        {
            var size = TextRenderer.MeasureText(row.Text, row.Font);
            width = Math.Max(width, size.Width);
            height += size.Height + 1;
        }
        width = Math.Min(width + 16, Width - 8);
        var box = new Rectangle(hit.Bounds.Right + 8, hit.Bounds.Y - 4, width, height);
        if (box.Right > Width - 4)
            box.X = hit.Bounds.X - width - 8;
        box.X = Math.Max(4, box.X);
        box.Y = Math.Clamp(box.Y, 4, Math.Max(4, Height - box.Height - 4));

        using (var back = new SolidBrush(Color.FromArgb(240, 12, 11, 9)))
            g.FillRectangle(back, box);
        StrokeRect(g, GoldDim, box);
        int y = box.Y + 4;
        foreach (var (text, font, color) in rows)
        {
            int h = TextRenderer.MeasureText(text, font).Height;
            TextRenderer.DrawText(g, text, font, new Rectangle(box.X + 8, y, box.Width - 12, h), color, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            y += h + 1;
        }
    }

    private static ImageAttributes Greyscale(float brightness)
    {
        float r = 0.30f * brightness, gr = 0.59f * brightness, b = 0.11f * brightness;
        var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix(
        [
            [r, r, r, 0, 0],
            [gr, gr, gr, 0, 0],
            [b, b, b, 0, 0],
            [0, 0, 0, 1, 0],
            [0, 0, 0, 0, 1],
        ]));
        return attributes;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icons.Loaded -= Invalidate;
            _titleFont.Dispose();
            _levelFont.Dispose();
            _grey.Dispose();
            _dark.Dispose();
        }
        base.Dispose(disposing);
    }
}
