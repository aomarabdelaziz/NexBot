using System.Drawing.Drawing2D;
using System.Globalization;
using NexBot.App.Theme;
using NexBot.Data;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>What the item preview shows: the icon (Media.pk2 path), the name and a few detail lines.</summary>
/// <param name="NameColor">The name's colour (gold when null; Seal items use the rare colour).</param>
internal sealed record ItemPreviewInfo(string? Icon, string Name, string Detail, Color? NameColor = null);

/// <summary>
/// Item pictures for the pages' plain lists and grids: a small icon in front of each item and a preview card (big icon,
/// name, code, level, type) while the mouse is over it.
/// </summary>
internal static class ItemVisuals
{
    private const int RowHeight = 30, IconSize = 26;

    /// <summary>The preview of a database item; <paramref name="extra"/> is added as the last line (+7, x250 ...).</summary>
    public static ItemPreviewInfo? Describe(ItemInfo? info, string? extra = null)
    {
        if (info is null)
            return null;
        var lines = new List<string> { info.ServerName };
        var facts = new List<string>();
        if (info.Level > 0)
            facts.Add($"Lv. {info.Level}");
        facts.Add(Kind(info));
        if (info.Stack > 1)
            facts.Add(string.Create(CultureInfo.CurrentCulture, $"stack {info.Stack:N0}"));
        if (info.CashItem != 0)
            facts.Add("item mall");
        lines.Add(string.Join("  ·  ", facts));
        if (!string.IsNullOrEmpty(extra))
            lines.Add(extra);
        return new ItemPreviewInfo(string.IsNullOrEmpty(info.Icon) ? null : info.Icon, info.Name.Length > 0 ? info.Name : info.ServerName,
            string.Join("\n", lines));
    }

    public static string Kind(ItemInfo i) => (i.Tid2, i.Tid3) switch
    {
        (1, 6) => "weapon",
        (1, 4) => "shield",
        (1, 5) => "accessory",
        (1, 7) => "job suit",
        (1, 13 or 14) => "avatar",
        (1, _) => "armor",
        (2, _) => "pet / summon",
        (3, 1) => "recovery potion",
        (3, 2) => "pill",
        (3, 3) => "scroll",
        (3, 4) => "ammo",
        (3, 5) => "gold",
        (3, 8) => "trade goods",
        (3, 10) => "elixir",
        (3, 11) => "alchemy",
        (3, 13) => "mall / buff item",
        _ => "item",
    };

    // ---------------------------------------------------------------- list boxes

    /// <summary>Draws an icon in front of every line of <paramref name="list"/> and previews the item under the mouse.</summary>
    public static void IconList(ListBox list, IconCache icons, Func<int, ItemPreviewInfo?> item)
    {
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = RowHeight;
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= list.Items.Count)
                return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(selected ? Hex("#2a2418") : list.BackColor))
                e.Graphics.FillRectangle(back, e.Bounds);
            var info = item(e.Index);
            var iconBox = new Rectangle(e.Bounds.X + 3, e.Bounds.Y + (e.Bounds.Height - IconSize) / 2, IconSize, IconSize);
            DrawIcon(e.Graphics, icons, info?.Icon, iconBox);
            var text = new Rectangle(iconBox.Right + 6, e.Bounds.Y, e.Bounds.Width - iconBox.Right - 6, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, list.Items[e.Index]?.ToString(), list.Font, text, selected ? Value : list.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        icons.Loaded += list.Invalidate;
        list.Disposed += (_, _) => icons.Loaded -= list.Invalidate;

        int hover = -1;
        list.MouseMove += (_, e) =>
        {
            int i = list.IndexFromPoint(e.Location);
            if (i == hover)
                return;
            hover = i;
            if (i >= 0 && item(i) is { } info)
                ItemPreview.Show(list, info, icons);
            else
                ItemPreview.Hide();
        };
        list.MouseLeave += (_, _) =>
        {
            hover = -1;
            ItemPreview.Hide();
        };
    }

    // ---------------------------------------------------------------- grids

    /// <summary>
    /// Draws an icon at the left of the <paramref name="column"/> cells (the column keeps its place, so positional
    /// <c>Rows.Add</c> calls still fit) and previews the row's item under the mouse.
    /// </summary>
    public static void IconGrid(DataGridView grid, string column, IconCache icons, Func<DataGridViewRow, ItemPreviewInfo?> item)
    {
        grid.RowTemplate.Height = RowHeight;
        var col = grid.Columns[column] ?? throw new ArgumentException($"No column {column}.", nameof(column));
        col.DefaultCellStyle.Padding = new Padding(IconSize + 8, 0, 0, 0);
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != col.Index || e.Graphics is null)
                return;
            e.Paint(e.CellBounds, DataGridViewPaintParts.All);
            var box = new Rectangle(e.CellBounds.X + 4, e.CellBounds.Y + (e.CellBounds.Height - IconSize) / 2, IconSize, IconSize);
            DrawIcon(e.Graphics, icons, item(grid.Rows[e.RowIndex])?.Icon, box);
            e.Handled = true;
        };
        icons.Loaded += grid.Invalidate;
        grid.Disposed += (_, _) => icons.Loaded -= grid.Invalidate;

        grid.CellMouseEnter += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < grid.Rows.Count && item(grid.Rows[e.RowIndex]) is { } info)
                ItemPreview.Show(grid, info, icons);
            else
                ItemPreview.Hide();
        };
        grid.MouseLeave += (_, _) => ItemPreview.Hide();
    }

    // ---------------------------------------------------------------- drawing

    public static void DrawIcon(Graphics g, IconCache icons, string? path, Rectangle box)
    {
        if (icons.Get(path) is { } bitmap)
        {
            var mode = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bitmap, box);
            g.InterpolationMode = mode;
        }
        else
        {
            using var fill = new SolidBrush(Hex("#15130f"));
            g.FillRectangle(fill, box);
        }
        using var frame = new Pen(Hex("#4a3d22"));
        g.DrawRectangle(frame, box.X, box.Y, box.Width - 1, box.Height - 1);
    }
}

/// <summary>The floating item card: a big icon, the name in gold and detail lines. One card for the whole app.</summary>
internal sealed class ItemPreview : Form
{
    private static ItemPreview? s_card;
    private ItemPreviewInfo? _info;
    private IconCache? _icons;
    private readonly Font _title = new("Segoe UI", 10f, FontStyle.Bold);

    private ItemPreview()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Hex("#0d0c0a");
        DoubleBuffered = true;
        Size = new Size(340, 96);
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Put in front of a detail line to draw it as a heading.</summary>
    public const char HeadingMark = Heading;

    /// <summary>Put in front of a detail line to draw it in the game's magic option blue.</summary>
    public const char BlueMark = '';

    /// <summary>Put in front of a detail line to draw it in the game's orange (sort of item, degree, max options).</summary>
    public const char OrangeMark = '';

    private static readonly char[] s_marks = [Heading, BlueMark, OrangeMark];

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public static void Show(Control owner, ItemPreviewInfo info, IconCache icons)
    {
        if (owner.IsDisposed)
            return;
        s_card ??= new ItemPreview();
        var card = s_card;
        card._info = info;
        card._icons = icons;
        icons.Loaded -= card.Invalidate;
        icons.Loaded += card.Invalidate;
        card.Height = Math.Max(92, card.Measure(info) + 14);
        var at = Cursor.Position + new Size(18, 16);
        var screen = Screen.FromPoint(at).WorkingArea;
        if (at.X + card.Width > screen.Right)
            at.X = Cursor.Position.X - card.Width - 12;
        if (at.Y + card.Height > screen.Bottom)
            at.Y = Cursor.Position.Y - card.Height - 12;
        card.Location = at;
        card.Invalidate();
        if (!card.Visible)
            card.Show();
    }

    public static new void Hide()
    {
        if (s_card is { Visible: true } card)
            card.Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var frame = new Pen(Hex("#5a4520")))
            g.DrawRectangle(frame, 0, 0, r.Width - 1, r.Height - 1);
        using (var inner = new Pen(Color.Black))
            g.DrawRectangle(inner, 1, 1, r.Width - 3, r.Height - 3);
        if (_info is not { } info || _icons is null)
            return;
        var iconBox = new Rectangle(12, 14, 64, 64);
        ItemVisuals.DrawIcon(g, _icons, info.Icon, iconBox);
        int x = iconBox.Right + 12;
        TextRenderer.DrawText(g, info.Name, _title, new Rectangle(x, 12, r.Width - x - 8, 22), info.NameColor ?? Gold,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        int y = 36;
        bool first = true;
        foreach (var raw in info.Detail.Split('\n'))
        {
            // A line starting with Heading is a gold heading ("How To Use", "Restriction").
            char mark = raw.Length > 0 && Array.IndexOf(s_marks, raw[0]) >= 0 ? raw[0] : ' ';
            bool heading = mark == Heading;
            string line = mark != ' ' ? raw[1..] : raw;
            int height = LineHeight(line, r.Width - x - 8);
            var color = mark switch
            {
                Heading => Gold,
                BlueMark => Hex("#6fb6ff"),
                OrangeMark => Hex("#f0a848"),
                _ => first ? Muted : SroTheme.Text,
            };
            TextRenderer.DrawText(g, line, heading ? SroTheme.BodyBold : SroTheme.Small, new Rectangle(x, y, r.Width - x - 8, height), color, Wrap);
            y += height;
            first = false;
        }
    }

    private const TextFormatFlags Wrap = TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;

    /// <summary>Marks a detail line as a heading (drawn bold and gold).</summary>
    public const char Heading = '\u0001';

    /// <summary>Height of one detail line, wrapped to <paramref name="width"/> (a blank line is a small gap).</summary>
    private static int LineHeight(string line, int width) =>
        line.Length == 0 ? 8 : Math.Max(18, TextRenderer.MeasureText(line.TrimStart(s_marks), SroTheme.BodyBold, new Size(width, 2000), Wrap).Height + 2);

    /// <summary>Bottom of the text when <paramref name="info"/> is drawn.</summary>
    private int Measure(ItemPreviewInfo info)
    {
        int width = Width - (12 + 64 + 12) - 8;
        return 36 + info.Detail.Split('\n').Sum(l => LineHeight(l, width));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _title.Dispose();
        base.Dispose(disposing);
    }
}
