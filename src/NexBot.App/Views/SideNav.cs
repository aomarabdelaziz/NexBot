using System.Drawing.Drawing2D;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>The glyph drawn next to a side menu entry.</summary>
internal enum NavGlyph { Server, Map, Character, Inventory, Script, Trade, Party, Guild, Academy, Players, Stall, Alchemy, Conditions, Sound, Log, Protection, Town, GameMaster, Pet, PickFilter, Attack, Training }

/// <summary>
/// The left side menu: framed column with a gold Cinzel caption, one entry per page (glyph + name); the active entry
/// is navy with a gold edge, like the game's selected tab.
/// </summary>
internal sealed class SideNav : Control
{
    /// <summary>Row height: 46, smaller when the window is too short for every page (then without the hint line).</summary>
    private int ItemHeight => Shown.Count == 0 ? 46 : Math.Clamp((Height - ListTop - 4) / Shown.Count, 30, 46);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }
    private const int ListTop = 44;
    private readonly List<(string Text, string Hint, NavGlyph Glyph, Control Page)> _items = [];
    private readonly HashSet<Control> _hidden = [];

    /// <summary>Indexes of the entries that are shown, in order.</summary>
    private List<int> Shown => Enumerable.Range(0, _items.Count).Where(i => !_hidden.Contains(_items[i].Page)).ToList();

    /// <summary>Hides or shows an entry (the GM page is shown only for GM characters). Hiding the open page opens the first one.</summary>
    public void SetShown(Control page, bool shown)
    {
        if (shown ? !_hidden.Remove(page) : !_hidden.Add(page))
            return;
        if (!shown && ReferenceEquals(SelectedPage, page) && Shown.Count > 0)
        {
            _selected = -1;
            SelectPage(_items[Shown[0]].Page);
        }
        Invalidate();
    }
    private readonly Font _caption = Title(9f);
    private readonly Font _itemFont = new("Segoe UI", 10f, FontStyle.Bold);
    private readonly Font _hintFont = new("Segoe UI", 7.5f);
    private int _selected = -1;
    private int _hover = -1;

    public SideNav()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Left;
        Width = 196;
        Cursor = Cursors.Hand;
    }

    /// <summary>The selected page changed (the page control).</summary>
    public event Action<Control>? PageSelected;

    public Control? SelectedPage => _selected >= 0 ? _items[_selected].Page : null;

    public void Add(string text, string hint, NavGlyph glyph, Control page)
    {
        _items.Add((text, hint, glyph, page));
        Invalidate();
    }

    public void SelectPage(Control page)
    {
        int index = _items.FindIndex(i => ReferenceEquals(i.Page, page));
        if (index < 0 || index == _selected)
            return;
        _selected = index;
        Invalidate();
        PageSelected?.Invoke(page);
    }

    private int IndexAt(Point p)
    {
        if (p.Y < ListTop)
            return -1;
        int index = (p.Y - ListTop) / ItemHeight;
        var shown = Shown;
        return index < shown.Count ? shown[index] : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hover = IndexAt(e.Location);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && IndexAt(e.Location) is var i && i >= 0)
            SelectPage(_items[i].Page);
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var bg = Vertical(r, Hex("#15130f"), Hex("#0a0908")))
            g.FillRectangle(bg, r);
        g.FillRectangle(Noise, r);
        // right edge: frame line + dark line
        using (var frame = new Pen(FrameDark))
            g.DrawLine(frame, r.Right - 2, 0, r.Right - 2, r.Bottom);
        using (var black = new Pen(Color.Black))
            g.DrawLine(black, r.Right - 1, 0, r.Right - 1, r.Bottom);

        // caption
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var gold = new SolidBrush(GoldDim))
            DrawSpaced(g, "MENU", _caption, gold, new RectangleF(0, 12, r.Width - 2, 20), 3f);
        using (var rule = new LinearGradientBrush(new Rectangle(14, 36, r.Width - 30, 1), Color.Transparent, GoldDim, LinearGradientMode.Horizontal))
        {
            rule.InterpolationColors = new ColorBlend { Colors = [Color.Transparent, GoldDim, Color.Transparent], Positions = [0f, 0.5f, 1f] };
            g.FillRectangle(rule, 14, 36, r.Width - 30, 1);
        }

        var shownItems = Shown;
        for (int pos = 0; pos < shownItems.Count; pos++)
        {
            int i = shownItems[pos];
            var (text, hint, glyph, _) = _items[i];
            var row = new Rectangle(8, ListTop + pos * ItemHeight + 3, r.Width - 18, ItemHeight - 6);
            bool selected = i == _selected, hover = i == _hover && !selected;
            if (selected)
            {
                StrokeRect(g, Hex("#5a4520"), row);
                StrokeRect(g, Color.Black, Rectangle.Inflate(row, -1, -1));
                var face = Rectangle.Inflate(row, -2, -2);
                using (var b = Vertical(face, Hex("#25344a"), Hex("#0e1520")))
                    g.FillRectangle(b, face);
                using (var edge = new SolidBrush(Gold))
                    g.FillRectangle(edge, face.X, face.Y, 3, face.Height);
            }
            else if (hover)
            {
                using var b = new SolidBrush(Color.FromArgb(18, 232, 201, 122));
                g.FillRectangle(b, row);
                StrokeRect(g, Hex("#3a352a"), row);
            }

            Color glyphColor = selected ? Gold : hover ? Value : GoldDim;
            DrawGlyph(g, glyph, new RectangleF(row.X + 14, row.Y + (row.Height - 20) / 2f, 20, 20), glyphColor);
            Color textColor = selected ? Color.White : hover ? Value : Theme.SroTheme.Text;
            if (ItemHeight >= 42)
            {
                TextRenderer.DrawText(g, text, _itemFont, new Point(row.X + 44, row.Y + 4), textColor);
                TextRenderer.DrawText(g, hint, _hintFont, new Point(row.X + 45, row.Y + 22), selected ? Hex("#c9b98c") : Muted);
            }
            else
            {
                TextRenderer.DrawText(g, text, _itemFont, new Rectangle(row.X + 44, row.Y, row.Width - 44, row.Height), textColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
        }
    }

    /// <summary>Small line icons, drawn so they need no image files.</summary>
    private static void DrawGlyph(Graphics g, NavGlyph glyph, RectangleF b, Color color)
    {
        using var pen = new Pen(color, 1.6f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(color);
        float x = b.X, y = b.Y, w = b.Width, h = b.Height;
        switch (glyph)
        {
            case NavGlyph.Server:
                for (int i = 0; i < 3; i++)
                {
                    var rect = new RectangleF(x + 1, y + 1 + i * 6.3f, w - 2, 5);
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                    g.FillEllipse(brush, rect.Right - 4.5f, rect.Y + 1.5f, 2.2f, 2.2f);
                }
                break;
            case NavGlyph.Map:
                PointF[] map = [new(x + 1, y + 4), new(x + 7, y + 1), new(x + 13, y + 4), new(x + 19, y + 1), new(x + 19, y + 16), new(x + 13, y + 19), new(x + 7, y + 16), new(x + 1, y + 19)];
                g.DrawPolygon(pen, map);
                g.DrawLine(pen, x + 7, y + 1, x + 7, y + 16);
                g.DrawLine(pen, x + 13, y + 4, x + 13, y + 19);
                break;
            case NavGlyph.Character:
                g.DrawEllipse(pen, x + 6, y + 1, 8, 8);
                using (var body = new GraphicsPath())
                {
                    body.AddArc(x + 2, y + 11, 16, 14, 180, 180);
                    g.DrawPath(pen, body);
                }
                break;
            case NavGlyph.Inventory:
                g.DrawRectangle(pen, x + 2, y + 5, w - 4, h - 6);
                g.DrawArc(pen, x + 6, y + 0.5f, 8, 9, 180, 180);
                g.DrawLine(pen, x + 2, y + 11, x + w - 2, y + 11);
                g.FillRectangle(brush, x + 8.5f, y + 9.5f, 3, 3.5f);
                break;
            case NavGlyph.Script:
                // a route: points joined by a winding line, ending at a flag
                g.DrawBezier(pen, x + 3, y + 17, x + 9, y + 17, x + 5, y + 9, x + 11, y + 9);
                g.DrawBezier(pen, x + 11, y + 9, x + 17, y + 9, x + 12, y + 4, x + 16, y + 4);
                g.FillEllipse(brush, x + 1, y + 15, 4.5f, 4.5f);
                g.FillEllipse(brush, x + 9, y + 7, 4, 4);
                g.DrawLine(pen, x + 16, y + 4, x + 16, y);
                g.FillPolygon(brush, new PointF[] { new(x + 16, y), new(x + 20, y + 1.5f), new(x + 16, y + 3) });
                break;
            case NavGlyph.Stall:
                // a market stall: awning and counter
                g.DrawPolygon(pen, new PointF[] { new(x + 1, y + 8), new(x + 4, y + 3), new(x + 18, y + 3), new(x + 21, y + 8) });
                g.DrawLine(pen, x + 1, y + 8, x + 21, y + 8);
                g.DrawLine(pen, x + 3, y + 8, x + 3, y + 19);
                g.DrawLine(pen, x + 19, y + 8, x + 19, y + 19);
                g.DrawRectangle(pen, x + 3, y + 13, 16, 6);
                break;
            case NavGlyph.Alchemy:
                // a flask
                g.DrawLine(pen, x + 8, y + 2, x + 14, y + 2);
                g.DrawLine(pen, x + 9, y + 2, x + 9, y + 8);
                g.DrawLine(pen, x + 13, y + 2, x + 13, y + 8);
                g.DrawPolygon(pen, new PointF[] { new(x + 9, y + 8), new(x + 13, y + 8), new(x + 19, y + 19), new(x + 3, y + 19) });
                g.DrawLine(pen, x + 6, y + 14, x + 16, y + 14);
                break;
            case NavGlyph.Conditions:
                // if → then: a diamond and an arrow
                g.DrawPolygon(pen, new PointF[] { new(x + 6, y + 3), new(x + 11, y + 8), new(x + 6, y + 13), new(x + 1, y + 8) });
                g.DrawLine(pen, x + 11, y + 8, x + 20, y + 8);
                g.DrawLine(pen, x + 17, y + 5, x + 20, y + 8);
                g.DrawLine(pen, x + 17, y + 11, x + 20, y + 8);
                g.DrawLine(pen, x + 6, y + 13, x + 6, y + 19);
                g.DrawLine(pen, x + 6, y + 19, x + 18, y + 19);
                break;
            case NavGlyph.Protection:
                // a shield with a cross
                g.DrawPolygon(pen, new PointF[] { new(x + 11, y + 1), new(x + 19, y + 4), new(x + 18, y + 12), new(x + 11, y + 20), new(x + 4, y + 12), new(x + 3, y + 4) });
                g.DrawLine(pen, x + 11, y + 6, x + 11, y + 15);
                g.DrawLine(pen, x + 7, y + 10, x + 15, y + 10);
                break;
            case NavGlyph.GameMaster:
                // a crown
                g.DrawPolygon(pen, new PointF[] { new(x + 2, y + 16), new(x + 2, y + 6), new(x + 7, y + 11), new(x + 11, y + 3), new(x + 15, y + 11), new(x + 20, y + 6), new(x + 20, y + 16) });
                g.DrawLine(pen, x + 2, y + 19, x + 20, y + 19);
                break;
            case NavGlyph.Town:
                // houses: two roofs
                g.DrawPolygon(pen, new PointF[] { new(x + 1, y + 10), new(x + 7, y + 4), new(x + 13, y + 10), new(x + 13, y + 19), new(x + 1, y + 19) });
                g.DrawPolygon(pen, new PointF[] { new(x + 13, y + 12), new(x + 17, y + 8), new(x + 21, y + 12), new(x + 21, y + 19), new(x + 13, y + 19) });
                g.DrawRectangle(pen, x + 5, y + 13, 4, 6);
                break;
            case NavGlyph.Sound:
                // a speaker and two waves
                g.DrawPolygon(pen, new PointF[] { new(x + 2, y + 8), new(x + 6, y + 8), new(x + 11, y + 4), new(x + 11, y + 18), new(x + 6, y + 14), new(x + 2, y + 14) });
                g.DrawArc(pen, x + 9, y + 7, 6, 8, -60, 120);
                g.DrawArc(pen, x + 9, y + 3, 11, 16, -60, 120);
                break;
            case NavGlyph.Party:
                // three heads side by side
                g.DrawEllipse(pen, x + 1, y + 6, 6, 6);
                g.DrawEllipse(pen, x + 8, y + 3, 6, 6);
                g.DrawEllipse(pen, x + 15, y + 6, 6, 6);
                g.DrawArc(pen, x - 1, y + 13, 10, 8, 180, 180);
                g.DrawArc(pen, x + 6, y + 10, 10, 10, 180, 180);
                g.DrawArc(pen, x + 13, y + 13, 10, 8, 180, 180);
                break;
            case NavGlyph.Guild:
                // a banner on a pole
                g.DrawLine(pen, x + 5, y + 1, x + 5, y + 20);
                g.DrawPolygon(pen, new PointF[] { new(x + 5, y + 2), new(x + 18, y + 2), new(x + 15, y + 7), new(x + 18, y + 12), new(x + 5, y + 12) });
                break;
            case NavGlyph.Academy:
                // a scholar's cap
                g.DrawPolygon(pen, new PointF[] { new(x + 1, y + 8), new(x + 11, y + 3), new(x + 21, y + 8), new(x + 11, y + 13) });
                g.DrawArc(pen, x + 5, y + 9, 12, 8, 0, 180);
                g.DrawLine(pen, x + 19, y + 9, x + 19, y + 16);
                break;
            case NavGlyph.Attack:
                // two crossed swords
                g.DrawLine(pen, x + 3, y + 3, x + 17, y + 17);
                g.DrawLine(pen, x + 19, y + 3, x + 5, y + 17);
                g.DrawLine(pen, x + 13, y + 15, x + 17, y + 11);
                g.DrawLine(pen, x + 9, y + 15, x + 5, y + 11);
                g.DrawLine(pen, x + 17, y + 17, x + 20, y + 20);
                g.DrawLine(pen, x + 5, y + 17, x + 2, y + 20);
                break;
            case NavGlyph.Training:
                // a flag on a pole over a circle (the area)
                g.DrawEllipse(pen, x + 1, y + 13, 20, 7);
                g.DrawLine(pen, x + 8, y + 2, x + 8, y + 17);
                g.DrawPolygon(pen, new PointF[] { new(x + 8, y + 2), new(x + 18, y + 5), new(x + 8, y + 9) });
                break;
            case NavGlyph.PickFilter:
                // a funnel with a drop under it
                g.DrawLine(pen, x + 2, y + 3, x + 20, y + 3);
                g.DrawLine(pen, x + 2, y + 3, x + 9, y + 11);
                g.DrawLine(pen, x + 20, y + 3, x + 13, y + 11);
                g.DrawLine(pen, x + 9, y + 11, x + 9, y + 17);
                g.DrawLine(pen, x + 13, y + 11, x + 13, y + 15);
                g.DrawLine(pen, x + 9, y + 17, x + 13, y + 15);
                g.DrawEllipse(pen, x + 15, y + 16, 5, 5);
                break;
            case NavGlyph.Pet:
                // a paw: pad and four toes
                g.DrawEllipse(pen, x + 6, y + 11, 10, 8);
                g.DrawEllipse(pen, x + 2, y + 6, 4, 5);
                g.DrawEllipse(pen, x + 7, y + 2, 4, 5);
                g.DrawEllipse(pen, x + 12, y + 2, 4, 5);
                g.DrawEllipse(pen, x + 17, y + 6, 4, 5);
                break;
            case NavGlyph.Players:
                // a person and a magnifier
                g.DrawEllipse(pen, x + 3, y + 2, 7, 7);
                g.DrawArc(pen, x, y + 10, 13, 12, 180, 180);
                g.DrawEllipse(pen, x + 12, y + 9, 7, 7);
                g.DrawLine(pen, x + 18, y + 15, x + 21, y + 19);
                break;
            case NavGlyph.Trade:
                // a trade crate on a cart: box with a lid line, two wheels
                g.DrawRectangle(pen, x + 3, y + 5, 14, 9);
                g.DrawLine(pen, x + 3, y + 8, x + 17, y + 8);
                g.DrawLine(pen, x + 10, y + 5, x + 10, y + 14);
                g.DrawEllipse(pen, x + 4, y + 15, 4.5f, 4.5f);
                g.DrawEllipse(pen, x + 12, y + 15, 4.5f, 4.5f);
                g.DrawLine(pen, x + 17, y + 11, x + 21, y + 9);
                break;
            default:
                g.DrawRectangle(pen, x + 2, y + 1, w - 4, h - 2);
                for (int i = 0; i < 3; i++)
                    g.DrawLine(pen, x + 6, y + 6 + i * 4, x + w - 6, y + 6 + i * 4);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _caption.Dispose();
            _itemFont.Dispose();
            _hintFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
