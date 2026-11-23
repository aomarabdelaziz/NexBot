using System.Drawing.Drawing2D;
using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Map;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// The inventory window (ported from POC-2's character view): equipment around a figure (or the avatar slots), the
/// bag in pages of 32 slots, gold, and the exchange window. Drag to move, right-click to use / wear / take off; the
/// hovered item's card shows beside it.
/// </summary>
internal sealed class InventoryPage : UserControl
{
    private const int PageSize = 32; // 4 x 8, like the game
    private const int Cell = 44;

    internal static readonly (int Slot, bool Right, int Row)[] s_equipLayout =
    [
        (6, false, 0), (7, true, 0), (0, false, 1), (2, true, 1), (1, false, 2), (3, true, 2),
        (4, false, 3), (5, true, 3), (9, false, 4), (10, true, 4), (12, false, 5), (11, true, 5),
    ];

    internal static readonly (int Slot, bool Right, int Row)[] s_avatarLayout =
        [(0, false, 1), (1, false, 2), (2, false, 3), (3, true, 1), (4, true, 2)];

    private readonly Func<SroClient?> _game;
    private readonly IconCache _icons;
    private readonly SlotView[] _cells = new SlotView[PageSize];
    private readonly Dictionary<int, SlotView> _equip = [];
    private readonly Dictionary<int, SlotView> _avatar = [];
    private readonly List<SroButton> _pageTabs = [];
    private readonly FlowLayoutPanel _pageBar;
    private readonly EquipmentBoard _board;
    private readonly SroButton _avatarToggle;
    private readonly SroButton _sortButton;
    private CancellationTokenSource? _sortCts;
    private readonly ExchangeView _exchange;
    private readonly Label _gold = Sro.Label("0", SroTheme.Value, BodyBold);
    private readonly Label _used = Sro.Label("", Muted, SroTheme.Small);
    private readonly Label _message = Sro.Label("", Muted, SroTheme.Small);
    private CharacterSheet? _sheet;
    private int _page;
    private bool _showAvatar;
    private bool _busy;

    public InventoryPage(Func<SroClient?> game, IconCache icons)
    {
        _game = game;
        _icons = icons;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        _icons.Loaded += RedrawSlots;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Bg, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 248));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Equipment ------------------------------------------------------------------------
        var equipPanel = new SroPanel { Title = "Equipment", Dock = DockStyle.Fill, Margin = new Padding(5) };
        _board = new EquipmentBoard { Location = new Point(12, 40), Size = new Size(260, 420) };
        foreach (var (slot, right, row) in s_equipLayout)
            _equip[slot] = Place(new SlotView(icons, slot, false, CharacterSheet.EquipmentSlots[slot]), right, row);
        _equip[8] = Place(new SlotView(icons, 8, false, "Job"), true, -1);
        foreach (var (slot, right, row) in s_avatarLayout)
            _avatar[slot] = Place(new SlotView(icons, slot, true, CharacterSheet.AvatarSlots[slot]) { Visible = false }, right, row);
        _avatarToggle = new SroButton { Text = "Avatar", Width = 90, Height = 28, Location = new Point(8, _board.Height - 36) };
        _avatarToggle.Click += (_, _) =>
        {
            _showAvatar = !_showAvatar;
            Render();
        };
        _board.Controls.Add(_avatarToggle);
        equipPanel.Controls.Add(_board);
        grid.Controls.Add(equipPanel, 0, 0);

        // Bag ---------------------------------------------------------------------------------
        var bagPanel = new SroPanel { Title = "Inventory", Dock = DockStyle.Fill, Margin = new Padding(5) };
        var bag = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _pageBar = new FlowLayoutPanel { Location = new Point(0, 0), Size = new Size(4 * (Cell + 4), 30), BackColor = Color.Transparent, WrapContents = false, Margin = Padding.Empty };
        var well = new Panel { Location = new Point(0, 32), Size = new Size(4 * (Cell + 4) + 8, 8 * (Cell + 4) + 8), BackColor = Hex("#080706"), Padding = new Padding(4) };
        well.Paint += (_, e) => StrokeRect(e.Graphics, Line, well.ClientRectangle);
        for (int i = 0; i < PageSize; i++)
        {
            var cell = new SlotView(icons) { Location = new Point(4 + i % 4 * (Cell + 4) + 2, 4 + i / 4 * (Cell + 4) + 2), Size = new Size(Cell, Cell) };
            Wire(cell);
            cell.AltClicked += s => _ = _exchange.OfferAsync(s); // Alt + click: into the exchange
            _cells[i] = cell;
            well.Controls.Add(cell);
        }
        var goldWell = new Panel { Location = new Point(0, well.Bottom + 8), Size = new Size(well.Width, 30), BackColor = Hex("#080706") };
        goldWell.Paint += (_, e) =>
        {
            StrokeRect(e.Graphics, Line, goldWell.ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var coin = new SolidBrush(Sox);
            e.Graphics.FillEllipse(coin, 10, 9, 12, 12);
            using var rim = new Pen(Hex("#8a6a1c"));
            e.Graphics.DrawEllipse(rim, 10, 9, 12, 12);
        };
        _gold.AutoSize = false;
        _gold.TextAlign = ContentAlignment.MiddleRight;
        _gold.Bounds = new Rectangle(28, 1, goldWell.Width - 38, 28);
        goldWell.Controls.Add(_gold);
        _used.Location = new Point(0, goldWell.Bottom + 44);
        _used.AutoSize = false;
        _used.AutoEllipsis = true;
        _used.Size = new Size(Math.Max(60, well.Right - 84), 20);
        _sortButton = new SroButton { Text = "Auto sort", Width = 92, Height = 28, Location = new Point(well.Right - 92, goldWell.Bottom + 6), TabStop = false };
        _sortButton.Click += (_, _) => _ = SortAsync();
        var sortTip = new ToolTip();
        sortTip.SetToolTip(_sortButton, "Stack the same items together (up to the server's maximum stack), then put the bag in order. Click again to stop.");
        var refresh = new SroButton { Text = "Refresh", Width = 80, Height = 28, Location = new Point(_sortButton.Left - 86, goldWell.Bottom + 6), TabStop = false };
        refresh.Click += (_, _) =>
        {
            _icons.RetryMissing();
            SetSheet(SheetSource?.Invoke() ?? _sheet);
            RedrawSlots();
        };
        sortTip.SetToolTip(refresh, "Read the inventory and equipment again now, and retry item pictures that did not load.");
        var global = new SroButton { Text = "Global", Width = 76, Height = 28, Location = new Point(well.Right - 76, goldWell.Bottom + 40), TabStop = false };
        global.Click += (_, _) => _ = GlobalAsync(null);
        sortTip.SetToolTip(global, "Send a message to all players with a global chatting item from the bag (or right-click the item).");
        bag.Controls.AddRange([_pageBar, well, goldWell, _used, global, refresh, _sortButton]);
        bagPanel.Controls.Add(bag);
        grid.Controls.Add(bagPanel, 1, 0);

        // Exchange ---------------------------------------------------------------------------
        var exchangePanel = new SroPanel { Title = "Exchange", Dock = DockStyle.Fill, Margin = new Padding(5) };
        _exchange = new ExchangeView(game, icons, ShowMessage) { Dock = DockStyle.Fill };
        var help = Sro.Label("Drag an item onto another slot to move it (also onto equipment and avatar slots), or onto your exchange slots to offer it (or Alt + click it; Alt + click an exchange item to take it back). Right-click (or double-click) to use potions and scrolls, wear equipment or take it off. Shift + drag a stack onto an empty slot to split it. Right-click a global chatting item (or click Global) to send a message to all players. Point at an item for its details.");
        help.AutoSize = false;
        help.Dock = DockStyle.Bottom;
        help.Height = 68;
        _message.AutoSize = false;
        _message.Dock = DockStyle.Bottom;
        _message.Height = 22;
        exchangePanel.Controls.Add(_exchange);
        exchangePanel.Controls.Add(_message);
        exchangePanel.Controls.Add(help);
        grid.Controls.Add(exchangePanel, 2, 0);

        Controls.Add(grid);
        SetPageCount(1);
        Render();
    }

    /// <summary>The exchange window's auto accept options.</summary>
    public void BindExchange(NexBot.Configuration.ExchangeSettings settings, Action changed) => _exchange.Bind(settings, changed);

    private SlotView Place(SlotView slot, bool right, int row)
    {
        slot.Size = new Size(Cell, Cell);
        int x = right ? _board.Width - 8 - Cell : 8;
        int y = row < 0 ? _board.Height - 8 - Cell : 8 + row * (Cell + 10) + (row > 0 ? 16 : 0);
        slot.Location = new Point(x, y);
        Wire(slot);
        _board.Controls.Add(slot);
        return slot;
    }

    private void Wire(SlotView slot)
    {
        slot.Hovered += s =>
        {
            if (s.Item is { } item)
                ItemPreview.Show(s, ItemCard.For(item, s.Avatar, _game()?.Data.GetItem(item.ServerName)), _icons);
            else
                ItemPreview.Hide();
        };
        slot.Unhovered += _ => ItemPreview.Hide();
        slot.Activated += s => _ = UseAsync(s);
        // An exchange slot of ours dragged back to the bag takes the item out of the exchange.
        slot.MoveRequested += (from, to) => _ = _exchange.Owns(from) ? _exchange.TakeBackAsync(from) : MoveAsync(from, to);
    }

    /// <summary>Shows a new sheet (null when the character is not in game).</summary>
    /// <summary>Where Refresh reads the character from (set by the main window).</summary>
    public Func<CharacterSheet?>? SheetSource { get; set; }

    /// <summary>A portrait shown while not in game (screenshots for the guide only).</summary>
    internal string? DemoPortrait { get; set; }

    public void SetSheet(CharacterSheet? sheet)
    {
        _sheet = sheet;
        _board.SetCharacter(sheet, sheet is null ? DemoPortrait : sheet.Portrait, _icons);
        Render();
    }

    private void SetPageCount(int pages)
    {
        if (pages == _pageTabs.Count)
            return;
        _pageBar.Controls.Clear();
        foreach (var tab in _pageTabs)
            tab.Dispose();
        _pageTabs.Clear();
        for (int n = 0; n < pages; n++)
        {
            int page = n;
            var tab = new SroButton { Text = $"Page {n + 1}", Width = 62, Height = 26, Margin = new Padding(0, 0, 4, 0), TabStop = false };
            tab.Click += (_, _) =>
            {
                _page = page;
                Render();
            };
            _pageTabs.Add(tab);
            _pageBar.Controls.Add(tab);
        }
    }

    private void Render()
    {
        var sheet = _sheet;
        int capacity = sheet?.InventoryCapacity ?? 0;
        int pages = Math.Max(1, (int)Math.Ceiling(capacity / (double)PageSize));
        SetPageCount(pages);
        _page = Math.Clamp(_page, 0, pages - 1);
        for (int n = 0; n < _pageTabs.Count; n++)
            _pageTabs[n].Selected = n == _page;

        var bySlot = sheet?.Items.ToDictionary(i => i.Slot) ?? [];
        var offered = _exchange.InWindow;
        int first = _page * PageSize;
        for (int i = 0; i < PageSize; i++)
        {
            var cell = _cells[i];
            int index = first + i;
            cell.Slot = CharacterSheet.FirstBagSlot + index;
            cell.Locked = sheet is null || index >= capacity;
            cell.SetItem(cell.Locked ? null : bySlot.GetValueOrDefault(cell.Slot));
            cell.Selected = !cell.Locked && offered.Contains((byte)cell.Slot); // in the exchange window
        }

        var worn = sheet?.Equipment.ToDictionary(i => i.Slot) ?? [];
        var avatars = sheet?.Avatar.ToDictionary(i => i.Slot) ?? [];
        foreach (var (slot, view) in _equip)
        {
            view.Visible = !_showAvatar || slot == 8;
            view.SetItem(worn.GetValueOrDefault(slot));
        }
        foreach (var (slot, view) in _avatar)
        {
            view.Visible = _showAvatar;
            view.SetItem(avatars.GetValueOrDefault(slot));
        }
        _board.ShowAvatar = _showAvatar;
        _avatarToggle.Text = _showAvatar ? "Equipment" : $"Avatar ({avatars.Count})";
        _avatarToggle.Selected = _showAvatar;

        _gold.Text = sheet is null ? "-" : sheet.Gold.ToString("N0", CultureInfo.CurrentCulture);
        _used.Text = sheet is null ? "Not in game" : string.Create(CultureInfo.CurrentCulture, $"{sheet.Items.Count} / {capacity} slots used");
        _exchange.RefreshView();
    }

    private void RedrawSlots()
    {
        foreach (var cell in _cells)
            cell.Invalidate();
        foreach (var view in _equip.Values.Concat(_avatar.Values))
            view.Invalidate();
    }

    private void ShowMessage(string text, bool ok)
    {
        _message.Text = text;
        _message.ForeColor = ok ? Good : Bad;
    }

    private async Task UseAsync(SlotView slot)
    {
        if (_busy || slot.Item is not { } item || _game() is not { } game)
            return;
        if (!slot.Avatar && game.World.Character?.Inventory[slot.Slot] is { } held && SroClient.IsGlobalChatItem(held))
        {
            await GlobalAsync(slot.Slot);
            return;
        }
        _busy = true;
        try
        {
            var result = await game.UseItemAsync((byte)slot.Slot, slot.Avatar);
            ShowMessage(result.Ok ? $"{item.Name}: done" : result.Message, result.Ok);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            ShowMessage(ex.Message, false);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Auto sort: stack the same items, then order the bag. Clicking while sorting stops it.</summary>
    private async Task SortAsync()
    {
        if (_sortCts is { } running)
        {
            running.Cancel();
            return;
        }
        if (_busy || _game() is not { } game)
        {
            if (_game() is null)
                ShowMessage("Not in game.", false);
            return;
        }
        _busy = true;
        using var cts = new CancellationTokenSource();
        _sortCts = cts;
        _sortButton.Text = "Stop";
        _sortButton.Selected = true;
        try
        {
            var progress = new Progress<string>(text => ShowMessage(text, true));
            var result = await Task.Run(() => InventorySorter.RunAsync(game, progress, token: cts.Token));
            ShowMessage(result.Message, result.Ok);
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Auto sort stopped.", false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            ShowMessage(ex.Message, false);
        }
        finally
        {
            _sortCts = null;
            _sortButton.Text = "Auto sort";
            _sortButton.Selected = false;
            _busy = false;
        }
    }

    private async Task MoveAsync(SlotView from, SlotView to)
    {
        if (_busy || from.Item is not { } item || _game() is not { } game)
            return;
        // Shift while dropping a stack: split it (the game's way), asking how many.
        ushort? quantity = null;
        if ((ModifierKeys & Keys.Shift) != 0 && !from.Avatar && !to.Avatar && item.QuantityMax > 1 && item.Quantity > 1)
        {
            quantity = ItemDialogs.AskSplit(FindForm(), item);
            if (quantity is null)
                return;
        }
        _busy = true;
        try
        {
            var result = await game.MoveItemAsync(from.Avatar, (byte)from.Slot, to.Avatar, (byte)to.Slot, quantity);
            ShowMessage(!result.Ok ? result.Message
                : quantity is { } n ? string.Create(CultureInfo.CurrentCulture, $"Split {n:N0} x {item.Name}") : $"Moved {item.Name}", result.Ok);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            ShowMessage(ex.Message, false);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Global message: asks for the text, then uses the global chatting item in <paramref name="slot"/> (or the first
    /// one in the bag) with it.
    /// </summary>
    private async Task GlobalAsync(int? slot)
    {
        if (_busy)
            return;
        if (_game() is not { } game || game.World.Character is not { } c)
        {
            ShowMessage("Not in game.", false);
            return;
        }
        int at = slot ?? game.FindGlobalChatItemSlot();
        if (at < 0 || c.Inventory[at] is not { } item)
        {
            ShowMessage("No global chatting item in the bag.", false);
            return;
        }
        if (ItemDialogs.AskGlobal(FindForm(), item.Name, SroClient.MaxGlobalMessageLength) is not { } message)
            return;
        _busy = true;
        try
        {
            var result = await game.UseItemAsync((byte)at, false, message);
            ShowMessage(result.Ok ? "Global message sent." : result.Message, result.Ok);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            ShowMessage(ex.Message, false);
        }
        finally
        {
            _busy = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icons.Loaded -= RedrawSlots;
            _sortCts?.Cancel();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The dark board behind the equipment slots. Between the two slot columns it shows a character card like the
/// game's character window: the face portrait in a gold frame, name, level and race, and the main stats. While not in
/// game it shows a crest instead of the portrait.
/// </summary>
internal sealed class EquipmentBoard : Panel
{
    private bool _showAvatar;
    private CharacterSheet? _sheet;
    private string? _portrait;
    private IconCache? _icons;
    private (string Name, string Line, (string Label, string Value)[] Rows)? _other;

    public EquipmentBoard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Hex("#080706");
    }

    /// <summary>Shows the character (null = not in game) and its portrait (null = the crest).</summary>
    public void SetCharacter(CharacterSheet? sheet, string? portrait, IconCache icons)
    {
        if (!ReferenceEquals(_icons, icons))
        {
            if (_icons is not null)
                _icons.Loaded -= Invalidate;
            _icons = icons;
            _icons.Loaded += Invalidate;
        }
        _sheet = sheet;
        _other = null;
        _portrait = portrait;
        Invalidate();
    }

    /// <summary>Shows another player: name, level line and rows of what is known about them, instead of our stats.</summary>
    public void SetPlayer(string name, string line, (string Label, string Value)[] rows, string? portrait, IconCache icons)
    {
        SetCharacter(null, portrait, icons);
        _other = (name, line, rows);
        Invalidate();
    }

    /// <summary>The name and line shown with nobody to show.</summary>
    public string EmptyName { get; set; } = "Not in game";
    public string EmptyLine { get; set; } = "Log in to see your character";

    public bool ShowAvatar
    {
        get => _showAvatar;
        set
        {
            if (_showAvatar == value)
                return;
            _showAvatar = value;
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _icons is not null)
            _icons.Loaded -= Invalidate;
        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var bg = new PathGradientBrush(new[] { new Point(0, 0), new Point(r.Width, 0), new Point(r.Width, r.Height), new Point(0, r.Height) })
               { CenterColor = Hex("#1a1712"), SurroundColors = [Hex("#060505")] })
            g.FillRectangle(bg, r);
        StrokeRect(g, Line, r);
        DrawCard(g, r, _showAvatar ? 0.45f : 1f);
        if (_showAvatar)
        {
            using var font = Title(10f);
            using var gold = new SolidBrush(Gold);
            DrawSpaced(g, "AVATAR", font, gold, new RectangleF(0, 356, r.Width, 20), 3f);
        }
    }

    /// <summary>Portrait, name, level line, divider and stat rows in the column between the slots.</summary>
    private void DrawCard(Graphics g, Rectangle r, float fade)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        const int column = 60, size = 112, top = 24;
        float cx = r.Width / 2f, left = column, width = r.Width - 2 * column;
        var sheet = _sheet;
        int A(int alpha) => Math.Clamp((int)(alpha * fade), 0, 255);

        // Glow behind the portrait.
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(cx - 100, top - 40, 200, size + 80);
            using var glow = new PathGradientBrush(glowPath) { CenterColor = Color.FromArgb(A(45), Hex("#c9a45a")), SurroundColors = [Color.Transparent] };
            g.FillPath(glow, glowPath);
        }
        var box = new RectangleF(cx - size / 2f, top, size, size);
        var face = _icons?.Get(_portrait);
        PortraitArt.DrawFramed(g, box, face, face is null ? fade * 0.85f : fade);

        var other = _other;
        bool empty = sheet is null && face is null && other is null;
        string name = other?.Name ?? sheet?.Name ?? (face is null ? EmptyName : "Your character");
        string line = other?.Line ?? (sheet is null
            ? (face is null ? EmptyLine : "Lv. -  ·  -")
            : string.Create(CultureInfo.CurrentCulture, $"Lv. {sheet.Level}  ·  {sheet.Race}"));
        using (var nameFont = Title(empty ? 10f : 11.5f))
        using (var nameBrush = new SolidBrush(Color.FromArgb(A(255), empty ? Muted : Gold)))
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(name, nameFont, nameBrush, new RectangleF(left - 8, box.Bottom + 12, width + 16, 24), center);
        TextRenderer.DrawText(g, line, SroTheme.Small, new Rectangle((int)left - 10, (int)box.Bottom + 36, (int)width + 20, 18),
            Color.FromArgb(A(255), sheet?.Dead == true ? Bad : Muted), TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Divider with a diamond.
        float dy = box.Bottom + 62;
        using (var divider = new Pen(Color.FromArgb(A(130), GoldDim), 1))
        {
            g.DrawLine(divider, left, dy, cx - 9, dy);
            g.DrawLine(divider, cx + 9, dy, left + width, dy);
        }
        Diamond(g, Color.FromArgb(A(255), Gold), cx, dy, 3.5f, 3.5f);

        var inv = CultureInfo.CurrentCulture;
        string Value(Func<CharacterSheet, string> read) => sheet is null ? "-" : read(sheet);
        (string Label, string Value)[] rows = other is { } o ? o.Rows :
        [
            ("Phy. atk", Value(s => string.Create(inv, $"{s.PhysicalAttackMin:N0} ~ {s.PhysicalAttackMax:N0}"))),
            ("Mag. atk", Value(s => string.Create(inv, $"{s.MagicalAttackMin:N0} ~ {s.MagicalAttackMax:N0}"))),
            ("Phy. def", Value(s => s.PhysicalDefense.ToString("N0", inv))),
            ("Mag. def", Value(s => s.MagicalDefense.ToString("N0", inv))),
            ("Hit rate", Value(s => s.HitRate.ToString("N0", inv))),
            ("Parry", Value(s => s.ParryRate.ToString("N0", inv))),
            ("STR / INT", Value(s => string.Create(inv, $"{s.Strength:N0} / {s.Intellect:N0}"))),
        ];
        int y = (int)dy + 10;
        var labelColor = Color.FromArgb(A(255), Muted);
        var valueColor = Color.FromArgb(A(255), SroTheme.Value);
        foreach (var (label, value) in rows)
        {
            var cell = new Rectangle((int)left - 4, y, (int)width + 8, 18);
            TextRenderer.DrawText(g, label, SroTheme.Small, cell, labelColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, value, SroTheme.Small, cell, valueColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            y += 19;
        }
    }
}
