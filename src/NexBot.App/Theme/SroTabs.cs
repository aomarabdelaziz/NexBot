using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Theme;

/// <summary>
/// A tab control in the game's style (phBot's pages use tabs): a strip of tab headers over the selected page. Pages
/// are any controls; only the selected one is shown.
/// </summary>
internal sealed class SroTabs : UserControl
{
    private readonly List<(string Title, Control Page)> _tabs = [];
    private readonly TabStrip _strip;
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
    private int _selected = -1;

    public SroTabs()
    {
        BackColor = Bg;
        DoubleBuffered = true;
        _strip = new TabStrip(this) { Dock = DockStyle.Top, Height = 32 };
        Controls.Add(_body);
        Controls.Add(_strip);
    }

    public event EventHandler? SelectedChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _tabs.Count || value == _selected)
                return;
            _selected = value;
            for (int i = 0; i < _tabs.Count; i++)
                _tabs[i].Page.Visible = i == value;
            _strip.Invalidate();
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Control? SelectedPage => _selected >= 0 ? _tabs[_selected].Page : null;

    public void Add(string title, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _body.Controls.Add(page);
        _tabs.Add((title, page));
        if (_selected < 0)
            SelectedIndex = 0;
        _strip.Invalidate();
    }

    private sealed class TabStrip : Control
    {
        private readonly SroTabs _owner;
        private int _hover = -1;

        public TabStrip(SroTabs owner)
        {
            _owner = owner;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        private List<Rectangle> Bounds()
        {
            var list = new List<Rectangle>();
            int x = 0;
            foreach (var (title, _) in _owner._tabs)
            {
                int w = TextRenderer.MeasureText(title.ToUpperInvariant(), SroTheme.Small).Width + 28;
                list.Add(new Rectangle(x, 0, w, Height - 1));
                x += w + 2;
            }
            return list;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hover = Bounds().FindIndex(r => r.Contains(e.Location));
            if (hover != _hover)
            {
                _hover = hover;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int at = Bounds().FindIndex(r => r.Contains(e.Location));
            if (at >= 0)
                _owner.SelectedIndex = at;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(Bg))
                g.FillRectangle(back, ClientRectangle);
            using (var line = new Pen(GoldDim))
                g.DrawLine(line, 0, Height - 1, Width, Height - 1);
            var bounds = Bounds();
            for (int i = 0; i < bounds.Count; i++)
            {
                var r = bounds[i];
                bool selected = i == _owner._selected, hover = i == _hover;
                using (var fill = new SolidBrush(selected ? FrameDark : hover ? Hex("#1c1912") : Hex("#121110")))
                    g.FillRectangle(fill, r);
                StrokeRect(g, selected ? GoldDim : Line, new Rectangle(r.X, r.Y, r.Width - 1, r.Height));
                if (selected)
                {
                    using var gold = new Pen(Gold, 2);
                    g.DrawLine(gold, r.X + 1, r.Y + 1, r.Right - 2, r.Y + 1);
                    using var cover = new Pen(FrameDark);
                    g.DrawLine(cover, r.X + 1, r.Bottom, r.Right - 2, r.Bottom);
                }
                TextRenderer.DrawText(g, _owner._tabs[i].Title.ToUpperInvariant(), SroTheme.Small, r, selected ? Gold : hover ? SroTheme.Text : Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}
