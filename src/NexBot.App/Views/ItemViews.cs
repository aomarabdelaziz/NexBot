using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;
using NexBot.App.Theme;
using NexBot.Map;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// An inventory / equipment / avatar slot like the game's (ported from POC-2's SlotControl): icon, quantity top-left,
/// plus bottom-right, gold frame for rare items. Drag it onto another slot to move the item; right-click to use it.
/// </summary>
internal sealed class SlotView : Control
{
    private static readonly Font s_label = new("Segoe UI", 6f, FontStyle.Bold);
    private static readonly Font s_qty = new("Segoe UI", 7.5f, FontStyle.Bold);
    private static readonly Font s_initials = new("Segoe UI", 6.5f);
    private ItemView? _item;
    private string? _signature;
    private bool _hover;
    private bool _dropTarget;
    private bool _locked;
    private bool _selected;
    private bool _waitingForIcon;
    private Point? _pressed;

    public SlotView(IconCache icons, int slot = -1, bool avatar = false, string? label = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Icons = icons;
        Slot = slot;
        Avatar = avatar;
        Label = label;
        Size = new Size(40, 40);
        Margin = new Padding(2);
        AllowDrop = true;
        BackColor = Hex("#0d0c0a");
        // Every slot redraws itself when the picture it waits for has loaded (not only the Inventory page's).
        Icons.Loaded += OnIconLoaded;
    }

    private void OnIconLoaded()
    {
        if (_waitingForIcon && !IsDisposed)
            Invalidate();
    }

    public IconCache Icons { get; }

    public int Slot { get; set; }

    public bool Avatar { get; }

    public string? Label { get; set; }

    public ItemView? Item => _item;

    /// <summary>Chosen in a list of slots (gold frame).</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            Invalidate();
        }
    }

    public bool Locked
    {
        get => _locked;
        set
        {
            if (_locked == value)
                return;
            _locked = value;
            AllowDrop = !value;
            Invalidate();
        }
    }

    /// <summary>The mouse entered (item or null).</summary>
    public event Action<SlotView>? Hovered;

    /// <summary>The mouse left the slot.</summary>
    public event Action<SlotView>? Unhovered;

    /// <summary>Right-click / double-click on an item: use, wear or take off.</summary>
    public event Action<SlotView>? Activated;

    /// <summary>Alt + left click on an item (bag to exchange, exchange to bag).</summary>
    public event Action<SlotView>? AltClicked;

    /// <summary>An item was dropped here from another slot (source, target).</summary>
    public event Action<SlotView, SlotView>? MoveRequested;

    public void SetItem(ItemView? item)
    {
        _item = item;
        SlotEffects.Animate(this, item is not null && (item.InUse || item.Rarity is not null));
        string signature = $"{item?.Signature}|{_locked}";
        if (signature == _signature)
            return;
        _signature = signature;
        Invalidate();
    }

    #region Mouse + drag and drop

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        Hovered?.Invoke(this);
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = null;
        Invalidate();
        Unhovered?.Invoke(this);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _item is not null && (ModifierKeys & Keys.Alt) != 0 && AltClicked is { } alt)
            alt(this);
        else if (e.Button == MouseButtons.Left && _item is not null)
            _pressed = e.Location;
        else if (e.Button == MouseButtons.Right && _item is not null)
            Activated?.Invoke(this);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_pressed is { } p && e.Button == MouseButtons.Left && _item is not null
            && (Math.Abs(e.X - p.X) > 4 || Math.Abs(e.Y - p.Y) > 4))
        {
            _pressed = null;
            ItemPreview.Hide();
            DoDragDrop(this, DragDropEffects.Move);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = null;
        base.OnMouseUp(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _item is not null)
            Activated?.Invoke(this);
        base.OnMouseDoubleClick(e);
    }

    private SlotView? Source(DragEventArgs e) =>
        e.Data?.GetData(typeof(SlotView)) is SlotView source && !ReferenceEquals(source, this) && !_locked
        && !(source.Avatar && Avatar) ? source : null;

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        bool ok = Source(drgevent) is not null;
        drgevent.Effect = ok ? DragDropEffects.Move : DragDropEffects.None;
        _dropTarget = ok;
        Invalidate();
        base.OnDragEnter(drgevent);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        _dropTarget = false;
        Invalidate();
        base.OnDragLeave(e);
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        _dropTarget = false;
        Invalidate();
        if (Source(drgevent) is { } source)
            MoveRequested?.Invoke(source, this);
        base.OnDragDrop(drgevent);
    }

    #endregion

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        var item = _item;
        bool rare = item?.Rarity is not null;

        using (var well = Vertical(r, Hex("#0b0a08"), Hex("#17150f")))
            g.FillRectangle(well, r);
        if (_locked)
        {
            using var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(40, 255, 255, 255), Color.FromArgb(200, 0, 0, 0));
            g.FillRectangle(hatch, r);
        }
        StrokeRect(g, Color.Black, r);
        var inner = Rectangle.Inflate(r, -1, -1);
        Color frame = _dropTarget || _selected ? Gold : _hover && !_locked ? GoldDim : rare ? Hex("#b8902e") : item is not null ? Hex("#5a5242") : Hex("#2f2b22");
        StrokeRect(g, frame, inner);
        if (_selected)
            StrokeRect(g, Gold, Rectangle.Inflate(r, -2, -2));
        if (rare)
        {
            using var glow = new Pen(Color.FromArgb(90, Sox), 2);
            g.DrawRectangle(glow, 3, 3, r.Width - 7, r.Height - 7);
        }

        if (item is null)
        {
            if (Label is { Length: > 0 } label && !_locked)
                TextRenderer.DrawText(g, label.ToUpperInvariant(), s_label, Rectangle.Inflate(r, -1, 0), Hex("#5d5545"),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        var iconRect = Rectangle.Inflate(r, -3, -3);
        var picture = Icons.Get(item.Icon);
        _waitingForIcon = picture is null;
        if (picture is { } icon)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(icon, iconRect);
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }
        else
        {
            using (var back = new SolidBrush(Hex("#221e16")))
                g.FillRectangle(back, iconRect);
            TextRenderer.DrawText(g, item.Name, s_initials, iconRect, SroTheme.Value,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        if (item.InUse)
            SlotEffects.DrawInUse(g, iconRect);
        if (rare)
            SlotEffects.DrawRare(g, Rectangle.Inflate(r, -1, -1));
        if (item.QuantityMax > 1)
            Outlined(g, item.Quantity.ToString(CultureInfo.InvariantCulture), s_qty, Color.White, new Point(2, 1), TextFormatFlags.Left | TextFormatFlags.Top, r);
        if (item.Plus > 0)
            Outlined(g, "+" + item.Plus.ToString(CultureInfo.InvariantCulture), s_qty, Sox, new Point(-2, -1), TextFormatFlags.Right | TextFormatFlags.Bottom, r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SlotEffects.Animate(this, false);
            Icons.Loaded -= OnIconLoaded;
        }
        base.Dispose(disposing);
    }

    private static void Outlined(Graphics g, string text, Font font, Color color, Point offset, TextFormatFlags flags, Rectangle r)
    {
        var rect = new Rectangle(r.X + offset.X, r.Y + offset.Y, r.Width, r.Height);
        foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            var shadow = rect;
            shadow.Offset(dx, dy);
            TextRenderer.DrawText(g, text, font, shadow, Color.Black, flags | TextFormatFlags.NoPadding);
        }
        TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.NoPadding);
    }
}

/// <summary>The item card next to the inventory: name (gold when rare), kind and level, quantity, durability, options.</summary>
internal sealed class ItemDetails : Control
{
    private readonly Font _nameFont = new("Segoe UI", 10.5f, FontStyle.Bold);
    private ItemView? _item;
    private bool _avatar;
    private string _hint = "Hover an item to see it here.";

    public ItemDetails(IconCache icons)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Icons = icons;
        Icons.Loaded += Invalidate;
    }

    public IconCache Icons { get; }

    public void ShowItem(ItemView? item, bool avatar)
    {
        if (ReferenceEquals(item, _item) || item is not null && _item is not null && item.Signature == _item.Signature && item.Slot == _item.Slot)
            return;
        _item = item;
        _avatar = avatar;
        Invalidate();
    }

    public void SetHint(string hint)
    {
        _hint = hint;
        Invalidate();
    }

    internal static string Words(string pascal) => Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z])", " ");

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_item is not { } item)
        {
            TextRenderer.DrawText(g, _hint, SroTheme.Small, ClientRectangle, Muted, TextFormatFlags.WordBreak);
            return;
        }

        // Icon
        var iconRect = new Rectangle(0, 0, 48, 48);
        using (var well = Vertical(iconRect, Hex("#0b0a08"), Hex("#17150f")))
            g.FillRectangle(well, iconRect);
        StrokeRect(g, item.Rarity is null ? Hex("#5a5242") : Hex("#b8902e"), iconRect);
        if (Icons.Get(item.Icon) is { } icon)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, Rectangle.Inflate(iconRect, -4, -4));
        }

        int x = 58, width = Width - x;
        int y = 0;
        void Line(string text, Font font, Color color, int gap = 2)
        {
            var size = TextRenderer.MeasureText(g, text, font, new Size(width, 1000), TextFormatFlags.WordBreak);
            TextRenderer.DrawText(g, text, font, new Rectangle(x, y, width, size.Height), color, TextFormatFlags.WordBreak);
            y += size.Height + gap;
        }

        Line(item.FullName, _nameFont, item.Rarity is null ? SroTheme.Value : Sox);
        Line(item.Kind + (item.Level > 0 ? $"  ·  Lv. {item.Level}" : ""), SroTheme.Small, Muted);
        y = Math.Max(y, 54);
        x = 0;
        width = Width;
        if (item.Rarity is { } rarity)
            Line(Words(rarity), SroTheme.Small, Sox);
        if (item.QuantityMax > 1)
            Line(string.Create(CultureInfo.CurrentCulture, $"Quantity  {item.Quantity:N0} / {item.QuantityMax:N0}"), SroTheme.Small, SroTheme.Text);
        if (item.Durability is { } durability)
            Line(string.Create(CultureInfo.CurrentCulture, $"Durability  {durability:N0}"), SroTheme.Small, SroTheme.Text);
        if (item.MagicOptions.Count > 0)
        {
            y += 4;
            foreach (string option in item.MagicOptions)
                Line(option, SroTheme.Small, Hex("#8fb4ff"), 1);
        }
        y += 6;
        string where = item.SlotName is not null ? (_avatar ? "Avatar · " : "Worn · ") + item.SlotName
            : string.Create(CultureInfo.InvariantCulture, $"Bag slot {item.Slot - CharacterSheet.FirstBagSlot + 1}");
        Line(where, SroTheme.Small, Muted);
        Line(item.ServerName, SroTheme.Small, Hex("#6f6858"));
        y += 6;
        string action = item.SlotName is not null ? "Right-click to take it off." : item.Usable ? "Right-click to use it." : item.Kind is "Item" or "Pet / mount" ? "" : "Right-click to wear it.";
        if (action.Length > 0)
            Line(action, SroTheme.Small, GoldDim);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Icons.Loaded -= Invalidate;
            _nameFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
