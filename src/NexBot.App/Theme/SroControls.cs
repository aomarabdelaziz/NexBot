using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Theme;

/// <summary>
/// A metal-framed panel with a gold Cinzel title bar (POC-2's <c>frame</c> + <c>wintitle</c>). Children go below the
/// title; <see cref="Control.Padding"/> is set for it.
/// </summary>
internal sealed class SroPanel : Panel
{
    public const int TitleHeight = 30;
    private string _title = "";
    private readonly Font _titleFont = SroTheme.Title(10f);

    public SroPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Hex("#14120f");
        ForeColor = SroTheme.Text;
        Padding = new Padding(12, TitleHeight + 10, 12, 12);
    }

    [Category("Appearance"), DefaultValue("")]
    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? "";
            Padding = new Padding(Padding.Left, _title.Length > 0 ? TitleHeight + 10 : 12, Padding.Right, Padding.Bottom);
            Invalidate();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        if (r.Width <= 2 || r.Height <= 2)
            return;

        using (var bg = Vertical(r, Hex("#1a1814"), Hex("#0f0e0b")))
            g.FillRectangle(bg, r);
        g.FillRectangle(Noise, r);

        // Inner shadow (inset 0 0 40px #000b): a few dark rings fading inwards.
        for (int i = 0; i < 10; i++)
        {
            using var pen = new Pen(Color.FromArgb(34 - i * 3, 0, 0, 0), 2);
            g.DrawRectangle(pen, r.X + 4 + i * 2, r.Y + 4 + i * 2, r.Width - 9 - i * 4, r.Height - 9 - i * 4);
        }

        // Frame: #77736a, then rings 1 #000, 2 #2b2a27, 1 #000.
        StrokeRect(g, Frame, r);
        StrokeRect(g, Color.Black, Rectangle.Inflate(r, -1, -1));
        StrokeRect(g, FrameDark, Rectangle.Inflate(r, -2, -2));
        StrokeRect(g, FrameDark, Rectangle.Inflate(r, -3, -3));
        StrokeRect(g, Color.Black, Rectangle.Inflate(r, -4, -4));

        if (_title.Length == 0)
            return;
        var bar = new Rectangle(5, 5, r.Width - 10, TitleHeight - 5);
        using (var tb = Vertical(bar, Hex("#25231f"), Hex("#0c0b0a")))
            g.FillRectangle(tb, bar);
        using (var hi = new Pen(Color.FromArgb(20, 255, 255, 255)))
            g.DrawLine(hi, bar.Left, bar.Top, bar.Right - 1, bar.Top);
        using (var line = new Pen(Frame))
            g.DrawLine(line, bar.Left, bar.Bottom, bar.Right - 1, bar.Bottom);
        using (var black = new Pen(Color.Black))
            g.DrawLine(black, bar.Left, bar.Bottom + 1, bar.Right - 1, bar.Bottom + 1);

        // Gold title with small ornaments on both sides.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var gold = new SolidBrush(Gold);
        DrawSpaced(g, _title.ToUpperInvariant(), _titleFont, gold, bar, 2.5f);
        float cy = bar.Top + bar.Height / 2f;
        Diamond(g, GoldDim, bar.Left + 12, cy, 3, 3);
        Diamond(g, GoldDim, bar.Right - 12, cy, 3, 3);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _titleFont.Dispose();
        base.Dispose(disposing);
    }
}

internal enum SroButtonStyle { Steel, Gold, Primary, Danger }

/// <summary>The game buttons of the theme: steel (default), gold "sro", navy primary with a gold ring, danger.</summary>
internal sealed class SroButton : Button
{
    private bool _hover;
    private bool _pressed;
    private bool _selected;
    private SroButtonStyle _style;

    public SroButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        BackColor = Color.Transparent;
        Font = SroTheme.Body;
        Cursor = Cursors.Hand;
        Height = 30;
        Margin = new Padding(4);
    }

    [DefaultValue(SroButtonStyle.Steel)]
    public SroButtonStyle Style
    {
        get => _style;
        set { _style = value; Invalidate(); }
    }

    /// <summary>Active state of a nav button (navy with a gold ring).</summary>
    [DefaultValue(false)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs mevent) { _pressed = true; Invalidate(); base.OnMouseDown(mevent); }
    protected override void OnMouseUp(MouseEventArgs mevent) { _pressed = false; Invalidate(); base.OnMouseUp(mevent); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        var r = ClientRectangle;
        if (Parent is not null)
        {
            // Let the parent's background show around the frame.
            var state = g.Save();
            g.TranslateTransform(-Left, -Top);
            InvokePaintBackground(Parent, new PaintEventArgs(g, Bounds));
            g.Restore(state);
        }

        bool hover = _hover && Enabled;
        Color top, bottom, text;
        var style = _selected ? SroButtonStyle.Primary : _style;
        int inset = 0;
        switch (style)
        {
            case SroButtonStyle.Primary:
                // rings: 0 0 0 3 #5a4520, 0 0 0 2 #000, 0 0 0 1 gold
                StrokeRect(g, hover ? Hex("#7a5f28") : Hex("#5a4520"), r);
                StrokeRect(g, Color.Black, Rectangle.Inflate(r, -1, -1));
                StrokeRect(g, hover ? Hex("#f0d48a") : Hex("#c9a24c"), Rectangle.Inflate(r, -2, -2));
                inset = 3;
                (top, bottom) = hover ? (Hex("#2c3d55"), Hex("#142030")) : (Hex("#1f2b3c"), Hex("#0e1520"));
                text = Color.White;
                break;
            case SroButtonStyle.Gold:
                StrokeRect(g, Color.Black, r);
                StrokeRect(g, Hex("#c9a24c"), Rectangle.Inflate(r, -1, -1));
                inset = 2;
                (top, bottom) = hover ? (Hex("#806838"), Hex("#4a3818")) : (Hex("#6a5530"), Hex("#3a2c14"));
                text = hover ? Color.White : Hex("#f0e2b8");
                break;
            case SroButtonStyle.Danger:
                StrokeRect(g, Hex("#4d4a42"), r);
                StrokeRect(g, Hex("#120505"), Rectangle.Inflate(r, -1, -1));
                inset = 2;
                (top, bottom) = hover ? (Hex("#924040"), Hex("#521c1c")) : (Hex("#7a3030"), Hex("#3e1414"));
                text = Hex("#ffd6d6");
                break;
            default:
                StrokeRect(g, Hex("#4d4a42"), r);
                StrokeRect(g, Hex("#0a0c12"), Rectangle.Inflate(r, -1, -1));
                inset = 2;
                (top, bottom) = hover ? (Hex("#6b7896"), Hex("#36405e")) : (Hex("#56627e"), Hex("#2c3550"));
                text = hover ? Color.White : Hex("#e7ebf5");
                break;
        }

        var face = Rectangle.Inflate(r, -inset, -inset);
        if (face.Width > 0 && face.Height > 0)
        {
            if (_pressed && hover)
                (top, bottom) = (bottom, top);
            using (var brush = Vertical(face, top, bottom))
                g.FillRectangle(brush, face);
            using (var hi = new Pen(Color.FromArgb(_style == SroButtonStyle.Primary ? 31 : 72, 255, 255, 255)))
                g.DrawLine(hi, face.Left, face.Top, face.Right - 1, face.Top);
            using (var lo = new Pen(Color.FromArgb(102, 0, 0, 0)))
                g.DrawLine(lo, face.Left, face.Bottom - 1, face.Right - 1, face.Bottom - 1);
        }

        var font = style == SroButtonStyle.Primary ? SroTheme.BodyBold : Font;
        TextRenderer.DrawText(g, Text, font, face, Enabled ? text : Hex("#8a8578"),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (!Enabled)
        {
            using var veil = new SolidBrush(Color.FromArgb(120, 10, 9, 8));
            g.FillRectangle(veil, r);
        }
    }
}

/// <summary>A dark text box with the theme's border (gold when focused).</summary>
internal sealed class SroInput : UserControl
{
    private bool _focused;

    public SroInput()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = InputBack;
        Padding = new Padding(7, 6, 7, 4);
        Height = 28;
        Margin = new Padding(0, 0, 0, 8);
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = InputBack,
            ForeColor = SroTheme.Text,
            Font = SroTheme.Body,
            Dock = DockStyle.Fill,
        };
        Box.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        Box.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        Controls.Add(Box);
        Cursor = Cursors.IBeam;
        Click += (_, _) => Box.Focus();
    }

    public TextBox Box { get; }

    [AllowNull]
    public override string Text
    {
        get => Box.Text;
        set => Box.Text = value ?? "";
    }

    public bool Password
    {
        get => Box.UseSystemPasswordChar;
        set => Box.UseSystemPasswordChar = value;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var back = new SolidBrush(InputBack))
            g.FillRectangle(back, r);
        // inset 0 1 6px #000
        using (var shade = new Pen(Color.FromArgb(160, 0, 0, 0)))
            g.DrawLine(shade, 1, 1, r.Width - 2, 1);
        StrokeRect(g, _focused ? Gold : InputLine, r);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Box.ForeColor = Enabled ? SroTheme.Text : Hex("#77705f");
        base.OnEnabledChanged(e);
    }
}

/// <summary>
/// A dark combo box drawn by hand (the Win32 combo ignores dark colors): a fixed list, or editable text with a list of
/// suggestions. The drop-down is an owner-drawn list in a <see cref="ToolStripDropDown"/>.
/// </summary>
internal sealed class SroCombo : UserControl
{
    private const int ArrowWidth = 22;
    private const int RowHeight = 22;
    private readonly TextBox? _box;
    private readonly List<string> _items = [];
    private int _selectedIndex = -1;
    private string _text = "";
    private bool _hover;
    private ToolStripDropDown? _drop;

    public SroCombo(bool editable = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = InputBack;
        ForeColor = SroTheme.Text;
        Font = SroTheme.Body;
        Padding = new Padding(7, 6, ArrowWidth + 4, 4);
        Height = 28;
        Margin = new Padding(0, 0, 0, 8);
        TabStop = true;
        Cursor = Cursors.Hand;
        if (editable)
        {
            _box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = InputBack,
                ForeColor = SroTheme.Text,
                Font = SroTheme.Body,
                Dock = DockStyle.Fill,
                Cursor = Cursors.IBeam,
            };
            _box.TextChanged += (_, _) => _text = _box.Text;
            _box.GotFocus += (_, _) => Invalidate();
            _box.LostFocus += (_, _) => Invalidate();
            _box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Down && e.Alt || e.KeyCode == Keys.F4)
                {
                    OpenDropDown();
                    e.Handled = true;
                }
            };
            Controls.Add(_box);
        }
    }

    public event EventHandler? SelectedIndexChanged;

    public IReadOnlyList<string> Items => _items;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int index = value >= 0 && value < _items.Count ? value : -1;
            if (index >= 0)
                SetText(_items[index]);
            if (index == _selectedIndex)
                return;
            _selectedIndex = index;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [AllowNull]
    public override string Text
    {
        get => _text;
        set
        {
            SetText(value ?? "");
            int index = _items.IndexOf(_text);
            if (index != _selectedIndex)
            {
                _selectedIndex = index;
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Replaces the list. An editable combo keeps its text; a fixed list loses its selection.</summary>
    public void SetItems(IEnumerable<string> items)
    {
        _items.Clear();
        _items.AddRange(items);
        if (_box is not null)
        {
            _selectedIndex = _items.IndexOf(_text);
        }
        else
        {
            _selectedIndex = -1;
            _text = "";
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
        Invalidate();
    }

    private void SetText(string text)
    {
        _text = text;
        if (_box is not null && _box.Text != text)
            _box.Text = text;
        Invalidate();
    }

    #region Drop-down

    private void OpenDropDown()
    {
        if (!Enabled || _items.Count == 0)
            return;
        if (_drop is { Visible: true })
        {
            _drop.Close();
            return;
        }

        var list = new ListBox
        {
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = RowHeight,
            BorderStyle = BorderStyle.None,
            BackColor = InputBack,
            ForeColor = SroTheme.Text,
            Font = Font,
            IntegralHeight = false,
        };
        list.Items.AddRange([.. _items]);
        list.SelectedIndex = _items.IndexOf(_text);
        list.DrawItem += DrawListItem;
        list.MouseMove += (_, e) =>
        {
            int i = list.IndexFromPoint(e.Location);
            if (i >= 0 && i != list.SelectedIndex)
                list.SelectedIndex = i;
        };
        list.MouseClick += (_, e) => Pick(list.IndexFromPoint(e.Location));
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
                Pick(list.SelectedIndex);
            else if (e.KeyCode == Keys.Escape)
                _drop?.Close();
        };

        var size = new Size(Width - 2, Math.Min(_items.Count, 10) * RowHeight);
        var host = new ToolStripControlHost(list) { AutoSize = false, Size = size, Margin = Padding.Empty, Padding = Padding.Empty };
        var drop = new ToolStripDropDown { Padding = new Padding(1), BackColor = GoldDim, AutoClose = true, DropShadowEnabled = true };
        drop.Items.Add(host);
        drop.Closed += (_, _) =>
        {
            if (ReferenceEquals(drop, _drop))
                _drop = null;
            BeginInvoke(drop.Dispose);
            Invalidate();
        };
        _drop = drop;
        drop.Show(this, new Point(0, Height));
        list.Focus();
        Invalidate();
    }

    private void Pick(int index)
    {
        _drop?.Close();
        if (index < 0)
            return;
        SelectedIndex = index;
        if (_box is not null)
        {
            _box.Focus();
            _box.SelectAll();
        }
    }

    private void DrawListItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0)
            return;
        var g = e.Graphics;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Hex("#3a2c10") : InputBack))
            g.FillRectangle(back, e.Bounds);
        TextRenderer.DrawText(g, list.Items[e.Index].ToString(), Font, Rectangle.Inflate(e.Bounds, -7, 0), selected ? Value : SroTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    #endregion

    #region Input + painting

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_box is null)
                Focus();
            OpenDropDown();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_box is null && _items.Count > 0)
        {
            if (e.KeyCode == Keys.Down && e.Alt || e.KeyCode is Keys.F4 or Keys.Space or Keys.Enter)
                OpenDropDown();
            else if (e.KeyCode == Keys.Down)
                SelectedIndex = Math.Min(_items.Count - 1, _selectedIndex + 1);
            else if (e.KeyCode == Keys.Up)
                SelectedIndex = Math.Max(0, _selectedIndex - 1);
        }
        base.OnKeyDown(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (_box is not null)
            _box.ForeColor = Enabled ? SroTheme.Text : Hex("#77705f");
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var back = new SolidBrush(InputBack))
            g.FillRectangle(back, r);
        using (var shade = new Pen(Color.FromArgb(160, 0, 0, 0)))
            g.DrawLine(shade, 1, 1, r.Width - 2, 1);

        bool focused = Focused || _box?.Focused == true || _drop is { Visible: true };
        StrokeRect(g, focused ? Gold : _hover && Enabled ? GoldDim : InputLine, r);

        if (_box is null)
        {
            var textRect = new Rectangle(7, 0, r.Width - ArrowWidth - 10, r.Height);
            TextRenderer.DrawText(g, _text, Font, textRect, Enabled ? SroTheme.Text : Hex("#77705f"),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        // Arrow button.
        var arrow = new Rectangle(r.Right - ArrowWidth - 1, 1, ArrowWidth, r.Height - 2);
        using (var ab = Vertical(arrow, Hex("#1c1a16"), Hex("#0c0b09")))
            g.FillRectangle(ab, arrow);
        using (var sep = new Pen(InputLine))
            g.DrawLine(sep, arrow.Left, arrow.Top, arrow.Left, arrow.Bottom - 1);
        float cx = arrow.Left + arrow.Width / 2f, cy = arrow.Top + arrow.Height / 2f;
        using var tri = new SolidBrush(Enabled ? (focused || _hover ? Gold : GoldDim) : Hex("#5a5446"));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPolygon(tri, new PointF[] { new(cx - 4.5f, cy - 2), new(cx + 4.5f, cy - 2), new(cx, cy + 3) });
    }

    #endregion
}

/// <summary>The theme's toggle switch (gold when on) with a label.</summary>
internal sealed class SroSwitch : Control
{
    /// <summary>How far a sub-option (text given with leading spaces) is moved right, toggle and text together.</summary>
    public const int SubOptionIndent = 20;

    /// <summary>Where the text starts, from the toggle's left edge.</summary>
    public const int TextOffset = 48;

    private bool _checked;
    private int _indent;

    public SroSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ForeColor = Muted;
        Font = SroTheme.Small;
        Cursor = Cursors.Hand;
        Height = 24;
        Margin = new Padding(0, 2, 12, 6);
    }

    public event EventHandler? CheckedChanged;

    [DefaultValue(false)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set
        {
            // Leading spaces mark a sub-option: the whole switch is indented, so the toggles and texts line up.
            string text = value ?? "";
            _indent = text.StartsWith(' ') ? SubOptionIndent : 0;
            base.Text = text.TrimStart();
            Width = _indent + TextOffset + TextRenderer.MeasureText(base.Text, Font).Width + 4;
            Invalidate();
        }
    }

    protected override void OnClick(EventArgs e)
    {
        if (Enabled)
            Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var track = new Rectangle(_indent, (Height - 20) / 2, 40, 20);
        using (var back = _checked ? Vertical(track, Hex("#3a2c10"), Hex("#1e1607")) : Vertical(track, Hex("#0a0908"), Hex("#0a0908")))
            g.FillRectangle(back, track);
        StrokeRect(g, _checked ? GoldDim : InputLine, track);

        var thumb = new Rectangle(track.X + (_checked ? 22 : 2), track.Y + 3, 16, 14);
        using (var t = _checked ? Vertical(thumb, Hex("#f5dc90"), Hex("#a8822f")) : Vertical(thumb, Hex("#8a8f9c"), Hex("#4a4f5c")))
            g.FillRectangle(t, thumb);
        using (var hi = new Pen(Color.FromArgb(77, 255, 255, 255)))
            g.DrawLine(hi, thumb.Left, thumb.Top, thumb.Right - 1, thumb.Top);

        int textLeft = _indent + TextOffset;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft), Height), Enabled ? (_checked ? SroTheme.Text : ForeColor) : Hex("#6f6858"),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (!Enabled)
        {
            using var veil = new SolidBrush(Color.FromArgb(110, 10, 9, 8));
            g.FillRectangle(veil, track);
        }
    }
}

internal enum SroBarKind { Hp, Mp, Exp }

/// <summary>An HP / MP / EXP gauge in the game's style.</summary>
internal sealed class SroBar : Control
{
    private double _value;
    private double _max = 1;
    private string _caption = "";

    public SroBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 18;
        Font = SroTheme.Tiny;
        Margin = new Padding(0, 0, 0, 6);
    }

    public SroBarKind Kind { get; set; }

    public void SetValue(double value, double max, string caption)
    {
        if (value == _value && max == _max && caption == _caption)
            return;
        _value = value;
        _max = max;
        _caption = caption;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var back = new SolidBrush(Hex("#0a0908")))
            g.FillRectangle(back, r);

        double ratio = _max <= 0 ? 0 : Math.Clamp(_value / _max, 0, 1);
        var fill = new Rectangle(2, 2, (int)Math.Round((r.Width - 4) * ratio), r.Height - 4);
        if (fill.Width > 0)
        {
            var (top, mid, bottom) = Kind switch
            {
                SroBarKind.Hp => (Hex("#ff8a7a"), Hex("#c02828"), Hex("#5a0c0c")),
                SroBarKind.Mp => (Hex("#8ac4ff"), Hex("#2a62c8"), Hex("#0c225a")),
                _ => (Hex("#fff0b0"), Hex("#d8a838"), Hex("#6a4c10")),
            };
            using var brush = Vertical(fill, (top, 0f), (mid, 0.45f), (bottom, 1f));
            g.FillRectangle(brush, fill);
        }
        StrokeRect(g, Hex("#5a5242"), r);
        StrokeRect(g, Color.Black, Rectangle.Inflate(r, -1, -1));

        var rect = r;
        rect.Offset(1, 1);
        TextRenderer.DrawText(g, _caption, Font, rect, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, _caption, Font, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>An ochre parchment strip with a dark letter-spaced caption (section headers, legends).</summary>
internal sealed class SroParchment : Control
{
    public SroParchment()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 22;
        Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        Margin = new Padding(0, 6, 0, 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var brush = Vertical(r, (Hex("#e0ab58"), 0f), (Hex("#c08a3c"), 0.55f), (Hex("#a8742e"), 1f)))
            g.FillRectangle(brush, r);
        using (var blotch = new GraphicsPath())
        {
            blotch.AddEllipse(-r.Width * 0.1f, -r.Height, r.Width * 0.6f, r.Height * 3);
            using var pgb = new PathGradientBrush(blotch) { CenterColor = Color.FromArgb(70, 90, 50, 10), SurroundColors = [Color.Transparent] };
            g.FillPath(pgb, blotch);
        }
        g.FillRectangle(Noise, r);
        StrokeRect(g, Color.Black, r);
        using var ink = new SolidBrush(Hex("#1a0f02"));
        DrawSpaced(g, Text.ToUpperInvariant(), Font, ink, new RectangleF(10, 0, r.Width - 20, r.Height), 1.6f, center: false);
    }
}

/// <summary>Small factory helpers for themed labels.</summary>
internal static class Sro
{
    public static Label Label(string text, Color? color = null, Font? font = null) => new()
    {
        Text = text,
        AutoSize = true,
        BackColor = Color.Transparent,
        ForeColor = color ?? Muted,
        Font = font ?? SroTheme.Small,
        Margin = new Padding(0, 0, 0, 3),
    };

    public static Label Value(string text = "-") => Label(text, SroTheme.Value, SroTheme.Body);
}
