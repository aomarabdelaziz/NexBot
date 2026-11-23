using System.Globalization;
using NexBot.App.Theme;
using NexBot.Map;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>Name, level, HP / MP / EXP gauges, gold, SP and position of the character.</summary>
internal sealed class CharacterCard : UserControl
{
    private readonly Label _name = Sro.Label("Not in game", SroTheme.Value, null);
    private readonly Label _level = Sro.Label("", Gold, SroTheme.BodyBold);
    private readonly SroBar _hp = new() { Kind = SroBarKind.Hp, Dock = DockStyle.Top };
    private readonly SroBar _mp = new() { Kind = SroBarKind.Mp, Dock = DockStyle.Top };
    private readonly SroBar _exp = new() { Kind = SroBarKind.Exp, Dock = DockStyle.Top };
    private readonly Label _gold = Sro.Value();
    private readonly Label _sp = Sro.Value();
    private readonly Label _where = Sro.Value();
    private readonly Label _state = Sro.Value();
    private readonly Font _nameFont = Title(12f);

    public CharacterCard()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        _name.Font = _nameFont;
        _name.UseCompatibleTextRendering = true; // GDI+: GDI cannot draw the embedded Cinzel font
        _name.AutoEllipsis = true;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 30, ColumnCount = 2, BackColor = Color.Transparent };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _name.Anchor = AnchorStyles.Left;
        _name.AutoSize = false;
        _name.Dock = DockStyle.Fill;
        _name.TextAlign = ContentAlignment.MiddleLeft;
        _level.Anchor = AnchorStyles.Right;
        top.Controls.Add(_name, 0, 0);
        top.Controls.Add(_level, 1, 0);

        var gap = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Color.Transparent };
        var info = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 0) };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string caption, Label value)
        {
            value.AutoEllipsis = true;
            info.Controls.Add(Sro.Label(caption));
            info.Controls.Add(value);
        }
        Row("Gold", _gold);
        Row("SP", _sp);
        Row("Position", _where);
        Row("State", _state);

        // Dock order: last added is on top.
        Controls.Add(info);
        Controls.Add(_exp);
        Controls.Add(_mp);
        Controls.Add(_hp);
        Controls.Add(gap);
        Controls.Add(top);
        Clear();
    }

    public void Clear()
    {
        _name.Text = "Not in game";
        _level.Text = "";
        _hp.SetValue(0, 1, "HP");
        _mp.SetValue(0, 1, "MP");
        _exp.SetValue(0, 100, "EXP");
        _gold.Text = _sp.Text = _where.Text = "-";
        _state.Text = "-";
        _state.ForeColor = SroTheme.Value;
    }

    public void SetSnapshot(MapSnapshot? snapshot, string state)
    {
        _state.Text = state;
        if (snapshot is null)
        {
            string s = state;
            Clear();
            _state.Text = s;
            return;
        }
        var c = snapshot.Status;
        var inv = CultureInfo.CurrentCulture;
        _name.Text = c.Name;
        _level.Text = $"Lv. {c.Level}";
        _hp.SetValue(c.Hp, c.HpMax, string.Create(inv, $"HP  {c.Hp:N0} / {c.HpMax:N0}"));
        _mp.SetValue(c.Mp, c.MpMax, string.Create(inv, $"MP  {c.Mp:N0} / {c.MpMax:N0}"));
        _exp.SetValue(c.ExpPercent, 100, string.Create(inv, $"EXP  {c.ExpPercent:0.00} %"));
        _gold.Text = c.Gold.ToString("N0", inv);
        _sp.Text = c.Sp.ToString("N0", inv);
        _where.Text = string.Create(CultureInfo.InvariantCulture, $"{snapshot.Me.RegionName ?? "R " + snapshot.Me.Region}  ({snapshot.Me.X:0}, {snapshot.Me.Y:0})");
        _state.Text = c.IsDead ? "Dead" : c.IsRiding ? state + " · riding" : state;
        _state.ForeColor = c.IsDead ? Bad : SroTheme.Value;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _nameFont.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The entities around the character, nearest first. Double-click walks to one.</summary>
internal sealed class NearbyList : Control
{
    private const int RowHeight = 24;
    private const int HeaderHeight = 26;
    private List<(MapEntity Entity, double Distance)> _rows = [];
    private int _scroll;
    private int _hover = -1;
    private string _summary = "";

    public NearbyList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = InputBack;
        Font = SroTheme.Small;
    }

    public event Action<MapEntity>? EntityActivated;

    public void SetSnapshot(MapSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _rows = [];
            _summary = "";
        }
        else
        {
            double mx = snapshot.Me.X, my = snapshot.Me.Y;
            _rows = snapshot.Entities
                .Select(e => (e, Math.Sqrt((e.X - mx) * (e.X - mx) + (e.Y - my) * (e.Y - my))))
                .OrderBy(r => r.Item2)
                .ToList();
            var counts = snapshot.Entities.GroupBy(e => e.Kind == MapEntityKind.Unique ? MapEntityKind.Mob : e.Kind)
                .ToDictionary(g => g.Key, g => g.Count());
            _summary = string.Join("  ·  ", new[] { MapEntityKind.Mob, MapEntityKind.Player, MapEntityKind.Npc, MapEntityKind.Drop }
                .Select(k => $"{counts.GetValueOrDefault(k)} {(k == MapEntityKind.Npc ? "NPC" : k.ToString().ToLowerInvariant() + "s")}"));
        }
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        Invalidate();
    }

    private int VisibleRows => Math.Max(1, (Height - HeaderHeight) / RowHeight);
    private int MaxScroll => Math.Max(0, _rows.Count - VisibleRows);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(InputBack);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var header = new Rectangle(0, 0, Width, HeaderHeight);
        using (var hb = Vertical(header, Hex("#1c1a16"), Hex("#0e0d0b")))
            g.FillRectangle(hb, header);
        using (var line = new Pen(Line))
            g.DrawLine(line, 0, HeaderHeight - 1, Width, HeaderHeight - 1);
        TextRenderer.DrawText(g, _rows.Count == 0 ? "Nothing around" : _summary, SroTheme.Tiny, Rectangle.Inflate(header, -8, 0), Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        for (int i = 0; i < VisibleRows + 1 && _scroll + i < _rows.Count; i++)
        {
            int index = _scroll + i;
            var (entity, distance) = _rows[index];
            var row = new Rectangle(0, HeaderHeight + i * RowHeight, Width, RowHeight);
            if (index == _hover)
            {
                using var hb = new SolidBrush(Hex("#2a210e"));
                g.FillRectangle(hb, row);
            }
            else if (index % 2 == 1)
            {
                using var zebra = new SolidBrush(Color.FromArgb(10, 255, 255, 255));
                g.FillRectangle(zebra, row);
            }

            Diamond(g, KindColor(entity.Kind), row.X + 12, row.Y + row.Height / 2f, 4, 5);
            string name = string.IsNullOrEmpty(entity.Name) ? MapCanvas.KindName(entity.Kind) : entity.Name;
            string dist = distance.ToString("0", CultureInfo.InvariantCulture);
            var distSize = TextRenderer.MeasureText(dist, Font);
            var nameRect = new Rectangle(row.X + 24, row.Y, row.Width - 30 - distSize.Width - 8, row.Height);
            TextRenderer.DrawText(g, name, Font, nameRect, entity.Kind == MapEntityKind.Unique ? Hex("#d9a8ff") : SroTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, dist, Font, new Rectangle(row.Right - distSize.Width - 10, row.Y, distSize.Width + 4, row.Height), Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        // Scroll indicator.
        if (_rows.Count > VisibleRows)
        {
            int track = Height - HeaderHeight - 4;
            int thumb = Math.Max(20, track * VisibleRows / _rows.Count);
            int y = HeaderHeight + 2 + (track - thumb) * _scroll / Math.Max(1, MaxScroll);
            using var tb = new SolidBrush(GoldDim);
            g.FillRectangle(tb, Width - 4, y, 3, thumb);
        }
        StrokeRect(g, InputLine, ClientRectangle);
    }

    private static Color KindColor(MapEntityKind kind) => kind switch
    {
        MapEntityKind.Player => Hex("#6fb8ff"),
        MapEntityKind.Mob => Hex("#e05050"),
        MapEntityKind.Unique => Hex("#c070ff"),
        MapEntityKind.Npc => Hex("#5fd27a"),
        MapEntityKind.Pet => Hex("#ffa040"),
        MapEntityKind.Drop => Hex("#e8e8e8"),
        _ => Hex("#40d0d0"),
    };

    private int RowAt(Point p) => p.Y < HeaderHeight ? -1 : _scroll + (p.Y - HeaderHeight) / RowHeight is var i && i < _rows.Count ? i : -1;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * 3, 0, MaxScroll);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hover = RowAt(e.Location);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
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

    protected override void OnMouseEnter(EventArgs e)
    {
        Focus(); // wheel
        base.OnMouseEnter(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (RowAt(e.Location) is var i && i >= 0)
            EntityActivated?.Invoke(_rows[i].Entity);
        base.OnMouseDoubleClick(e);
    }
}
