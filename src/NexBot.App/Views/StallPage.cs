using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Game.Entities;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// Stall (phBot's Stall tab): the stall's items and prices, prices for inventory items (Inventory) and for any game
/// item with a per-slot quantity (Filter), options and a sales list.
/// </summary>
internal sealed class StallPage : UserControl
{
    private readonly Func<SroClient?> _game;
    private readonly Func<StallManager?> _manager;
    private readonly ListBox _slots = SocialUi.List();
    private readonly ListBox _sales = SocialUi.List();
    private readonly Label _status = Sro.Label("Not in game.", Muted, SroTheme.Small);
    private readonly SroInput _slotPrice = new() { Width = 110 };
    private readonly DataGridView _grid = new();
    private readonly SroInput _search = new() { Dock = DockStyle.Fill };
    private readonly ListBox _results = SocialUi.List();
    private readonly Panel _filterBar = new() { Dock = DockStyle.Top, Height = 118, BackColor = Color.Transparent, Visible = false };
    private readonly SroButton _tabInventory = new() { Text = "Inventory", Width = 96, Style = SroButtonStyle.Gold };
    private readonly SroButton _tabFilter = new() { Text = "Filter", Width = 80 };
    private readonly Panel _options = new() { Dock = DockStyle.Top, Height = 250, BackColor = Color.Transparent };
    private readonly SroInput _title = new() { Dock = DockStyle.Right, Width = 190 };
    private readonly SroInput _note = new() { Dock = DockStyle.Right, Width = 190 };
    private StallSettings _settings = new();
    private Action _changed = () => { };
    private bool _filterMode;
    private bool _filling;
    private IReadOnlyList<StallSlot> _shownSlots = [];
    private List<NexBot.Data.ItemInfo> _found = [];
    private string _lastRows = "";
    private DateTime _sayUntil;

    private readonly IconCache _icons;

    public StallPage(Func<SroClient?> game, Func<StallManager?> manager, IconCache icons)
    {
        _icons = icons;
        _game = game;
        _manager = manager;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(BuildStall(), 0, 0);
        grid.Controls.Add(BuildPrices(), 1, 0);
        grid.Controls.Add(BuildOptions(), 2, 0);
        Controls.Add(grid);
    }

    // ---- Stall column ----------------------------------------------------------------------

    private SroPanel BuildStall()
    {
        var panel = SocialUi.Panel("Stall");
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(_slots);
        var menu = new ContextMenuStrip { Renderer = new SroMenuRenderer(), ShowImageMargin = false, Font = SroTheme.Body };
        menu.Items.Add("Change price to the value below", null, async (_, _) => await ChangePriceAsync());
        menu.Items.Add("Remove from stall", null, async (_, _) => await RemoveAsync());
        foreach (ToolStripItem item in menu.Items)
            item.ForeColor = SroTheme.Text;
        _slots.ContextMenuStrip = menu;
        _slots.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && _slots.IndexFromPoint(e.Location) is var i and >= 0)
                _slots.SelectedIndex = i;
        };
        _slots.SelectedIndexChanged += (_, _) =>
        {
            if (Selected() is { } s)
                _slotPrice.Text = s.Price.ToString(CultureInfo.CurrentCulture);
        };

        _status.AutoSize = false;
        _status.Height = 38;
        var priceRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        _slotPrice.Margin = new Padding(0, 0, 6, 0);
        priceRow.Controls.Add(_slotPrice);
        priceRow.Controls.Add(SocialUi.Button("Change price", 106, async () => await ChangePriceAsync()));
        priceRow.Controls.Add(SocialUi.Button("Remove", 76, async () => await RemoveAsync()));
        foreach (Control c in priceRow.Controls)
            c.Margin = new Padding(0, 0, 6, 0);

        panel.Controls.Add(well);
        panel.Controls.Add(priceRow);
        SocialUi.Stack(panel,
            _status,
            Row("Title", _title),
            Row("Note", _note),
            SocialUi.Buttons(
                SocialUi.Button("Open stall", 100, async () => await OpenAsync()),
                SocialUi.Button("Close stall", 100, async () => await CloseAsync())),
            SocialUi.Caption("ITEMS FOR SALE  (RIGHT-CLICK A SLOT TO CHANGE IT)"));
        return panel;
    }

    private static Panel Row(string text, Control input)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        var label = Sro.Label(text, SroTheme.Text, SroTheme.Small);
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        row.Controls.Add(label);
        row.Controls.Add(input);
        return row;
    }

    private StallSlot? Selected() =>
        _slots.SelectedIndex >= 0 && _slots.SelectedIndex < _shownSlots.Count ? _shownSlots[_slots.SelectedIndex] : null;

    private void Say(string text, bool ok)
    {
        _status.Text = text;
        _status.ForeColor = ok ? Good : Bad;
        _sayUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private async Task OpenAsync()
    {
        if (_manager() is not { } manager)
        {
            Say("Start the bot and enter the game first.", false);
            return;
        }
        Say("Opening the stall…", true);
        var result = await manager.OpenAsync();
        Say(result.Message, result.Ok);
    }

    private async Task CloseAsync()
    {
        if (_manager() is not { } manager)
            return;
        var result = await manager.CloseAsync();
        Say(result.Message, result.Ok);
    }

    private async Task ChangePriceAsync()
    {
        if (_game() is not { } client || Selected() is not { } slot)
        {
            Say("Select a stall slot first.", false);
            return;
        }
        if (!ulong.TryParse(_slotPrice.Text.Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.CurrentCulture, out ulong price) || price == 0)
        {
            Say("Type a price first.", false);
            return;
        }
        // Prices can only change while the stall is closed to buyers.
        bool wasOpen = client.Stall.IsOpen;
        if (wasOpen)
            await client.OpenStallAsync(false);
        var result = await client.UpdateStallItemAsync(slot.Slot, slot.Quantity, price);
        if (wasOpen)
            await client.OpenStallAsync(true);
        if (result.Ok)
        {
            _settings.Get(slot.ItemServerName).Price = price; // remembered for next time
            _changed();
            FillGrid(force: true);
        }
        Say(result.Ok ? $"{slot.ItemName}: {price:N0} gold." : result.Message, result.Ok);
    }

    private async Task RemoveAsync()
    {
        if (_game() is not { } client || Selected() is not { } slot)
            return;
        bool wasOpen = client.Stall.IsOpen;
        if (wasOpen)
            await client.OpenStallAsync(false);
        var result = await client.RemoveStallItemAsync(slot.Slot);
        if (wasOpen && client.Stall.Slots.Count > 0)
            await client.OpenStallAsync(true);
        Say(result.Ok ? $"{slot.ItemName} removed." : result.Message, result.Ok);
    }

    // ---- Prices column ---------------------------------------------------------------------

    private SroPanel BuildPrices()
    {
        var panel = SocialUi.Panel("Prices");
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = InputBack;
        _grid.BorderStyle = BorderStyle.None;
        _grid.GridColor = Line;
        _grid.RowHeadersVisible = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = FrameDark, ForeColor = Gold, Font = SroTheme.Small, SelectionBackColor = FrameDark };
        _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = InputBack, ForeColor = SroTheme.Text, SelectionBackColor = Hex("#2a2418"), SelectionForeColor = Value, Font = SroTheme.Body };
        _grid.ColumnHeadersHeight = 26;
        _grid.RowTemplate.Height = 26;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = "On", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Item", HeaderText = "Item", ReadOnly = true, FillWeight = 52 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Have", HeaderText = "Have", ReadOnly = true, FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Price", HeaderText = "Price / slot", FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Per", HeaderText = "Per slot", FillWeight = 16, ToolTipText = "0 = the whole stack" });
        ItemVisuals.IconGrid(_grid, "Item", _icons, row => row.Tag switch
        {
            string serverName => ItemVisuals.Describe(_game()?.Data.GetItem(serverName)),
            StallPrice price => ItemVisuals.Describe(_game()?.Data.GetItem(price.Item)),
            _ => null,
        });
        ItemVisuals.IconList(_results, _icons, i => i < _found.Count ? ItemVisuals.Describe(_found[i]) : null);
        ItemVisuals.IconList(_slots, _icons, i => i < _shownSlots.Count && _shownSlots[i] is { } s
            ? ItemVisuals.Describe(_game()?.Data.GetItem(s.ItemServerName), string.Create(CultureInfo.CurrentCulture, $"x{s.Quantity}  ·  {s.Price:N0} gold"))
            : null);
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += OnCellChanged;
        _grid.DataError += (_, e) => e.Cancel = true;
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && _filterMode && _grid.CurrentRow?.Tag is StallPrice price)
            {
                _settings.Prices.Remove(price);
                _changed();
                FillGrid(force: true);
            }
        };

        // Filter: search any game item and add it.
        var searchRow = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.Transparent };
        var add = new SroButton { Text = "Add", Width = 60, Dock = DockStyle.Right };
        add.Click += (_, _) => AddFound();
        searchRow.Controls.Add(_search);
        searchRow.Controls.Add(add);
        _search.Box.TextChanged += (_, _) => Search();
        _results.Height = 70;
        _results.Dock = DockStyle.Fill;
        _results.DoubleClick += (_, _) => AddFound();
        var resultsWell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        resultsWell.Controls.Add(_results);
        _filterBar.Controls.Add(resultsWell);
        _filterBar.Controls.Add(searchRow);
        _filterBar.Padding = new Padding(0, 0, 0, 6);

        _tabInventory.Click += (_, _) => ShowTab(filter: false);
        _tabFilter.Click += (_, _) => ShowTab(filter: true);
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, BackColor = Color.Transparent, WrapContents = false };
        _tabInventory.Margin = _tabFilter.Margin = new Padding(0, 0, 6, 0);
        _tabInventory.Height = _tabFilter.Height = 28;
        tabs.Controls.Add(_tabInventory);
        tabs.Controls.Add(_tabFilter);

        var gridWell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Line };
        gridWell.Controls.Add(_grid);
        panel.Controls.Add(gridWell);
        panel.Controls.Add(_filterBar);
        panel.Controls.Add(SocialUi.Hint("Inventory: your items, one row per kind. Filter: any game item, sold a few at a time (\"Per slot\" splits stacks). Prices are saved.", 36));
        panel.Controls.Add(tabs);
        return panel;
    }

    private void ShowTab(bool filter)
    {
        _filterMode = filter;
        _tabInventory.Style = filter ? SroButtonStyle.Steel : SroButtonStyle.Gold;
        _tabFilter.Style = filter ? SroButtonStyle.Gold : SroButtonStyle.Steel;
        _tabInventory.Invalidate();
        _tabFilter.Invalidate();
        _filterBar.Visible = filter;
        _grid.Columns["Per"]!.Visible = true;
        FillGrid(force: true);
    }

    private void Search()
    {
        string text = _search.Text.Trim();
        _found = text.Length < 2 || _game()?.Data is not { } data ? [] : [.. data.SearchItems(text, 30)];
        SocialUi.Fill(_results, [.. _found.Select(i => $"{i.Name}   ({i.ServerName})")]);
    }

    private void AddFound()
    {
        if (_results.SelectedIndex < 0 || _results.SelectedIndex >= _found.Count)
            return;
        var info = _found[_results.SelectedIndex];
        var price = _settings.Get(info.ServerName);
        price.FromFilter = true;
        _changed();
        FillGrid(force: true);
    }

    private void OnCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_filling || e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not string serverName)
            return;
        var row = _grid.Rows[e.RowIndex];
        var price = _settings.Get(serverName);
        switch (_grid.Columns[e.ColumnIndex].Name)
        {
            case "On":
                price.Enabled = row.Cells["On"].Value is true;
                break;
            case "Price":
                price.Price = ulong.TryParse(Convert.ToString(row.Cells["Price"].Value, CultureInfo.CurrentCulture)?.Replace(",", "", StringComparison.Ordinal),
                    NumberStyles.Integer, CultureInfo.CurrentCulture, out ulong p) ? p : 0;
                if (price.Price > 0 && row.Cells["On"].Value is not true)
                {
                    price.Enabled = true;
                    _filling = true;
                    row.Cells["On"].Value = true;
                    _filling = false;
                }
                break;
            case "Per":
                price.Quantity = int.TryParse(Convert.ToString(row.Cells["Per"].Value, CultureInfo.CurrentCulture), out int q) ? Math.Clamp(q, 0, 1000) : 0;
                break;
        }
        _changed();
    }

    /// <summary>Rows: the inventory's kinds of items (Inventory) or the filter list (Filter).</summary>
    private void FillGrid(bool force = false)
    {
        if (_grid.IsCurrentCellInEditMode)
            return;
        var client = _game();
        var inventory = client?.World.Character?.Inventory;
        var have = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        if (inventory is not null)
        {
            for (int i = Character.FirstInventorySlot; i < inventory.Capacity; i++)
            {
                if (inventory[i] is not { } item)
                    continue;
                have.TryGetValue(item.ServerName, out var h);
                have[item.ServerName] = (item.Name, h.Count + Math.Max(1, (int)item.Quantity));
            }
        }
        List<(string ServerName, string Name, int Have)> rows = _filterMode
            ? [.. _settings.Prices.Where(p => p.FromFilter).Select(p =>
                (p.Item, client?.Data.GetItem(p.Item)?.Name is { Length: > 0 } n ? n : p.Item, have.TryGetValue(p.Item, out var h) ? h.Count : 0))]
            : [.. have.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Count)).OrderBy(r => r.Name, StringComparer.CurrentCulture)];
        string key = (_filterMode ? "F" : "I") + string.Join("|", rows.Select(r => $"{r.ServerName}:{r.Have}"));
        if (!force && key == _lastRows)
            return;
        _lastRows = key;
        _filling = true;
        _grid.Rows.Clear();
        foreach (var r in rows)
        {
            var price = _settings.Prices.FirstOrDefault(p => string.Equals(p.Item, r.ServerName, StringComparison.OrdinalIgnoreCase));
            int index = _grid.Rows.Add(price?.Enabled == true && price.Price > 0, r.Name, r.Have.ToString(CultureInfo.CurrentCulture),
                price is { Price: > 0 } ? price.Price.ToString(CultureInfo.CurrentCulture) : "",
                (price?.Quantity ?? 0).ToString(CultureInfo.CurrentCulture));
            _grid.Rows[index].Tag = r.ServerName;
        }
        _filling = false;
    }

    // ---- Options column --------------------------------------------------------------------

    private SroPanel BuildOptions()
    {
        var panel = SocialUi.Panel("Options");
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(_sales);
        panel.Controls.Add(well);
        SocialUi.Stack(panel, _options, SocialUi.Caption("SOLD THIS SESSION"));
        return panel;
    }

    public void Bind(StallSettings s, Action changed)
    {
        _settings = s;
        _changed = changed;
        _title.Text = s.Title;
        _note.Text = s.Note;
        _title.Box.TextChanged += (_, _) =>
        {
            s.Title = _title.Text.Trim();
            changed();
        };
        _note.Box.TextChanged += (_, _) =>
        {
            s.Note = _note.Text.Trim();
            changed();
        };
        _options.Controls.Clear();
        SocialUi.Stack(_options,
            SocialUi.Switch("Auto stall", s.AutoStall, v => s.AutoStall = v, changed),
            SocialUi.Hint("Opens the stall by itself when enough priced items are in the inventory (stand in a town, off the horse).", 40),
            SocialUi.Field("Stall when at least (items)", s.MinItems.ToString(CultureInfo.CurrentCulture), 60, v => s.MinItems = SocialUi.Int(v, 0, MyStall.Capacity), changed),
            SocialUi.Hint("0 = whenever any item can be stalled.", 22),
            SocialUi.Switch("Fill again when sold out", s.RefillWhenSoldOut, v => s.RefillWhenSoldOut = v, changed),
            SocialUi.Hint("When everything in the stall sold, it is filled again with the priced items left in the inventory. When none are left (or this is off), the stall closes by itself.", 40),
            SocialUi.Hint("Consignment (DoConsignment) is not built yet.", 22));
        FillGrid(force: true);
    }

    /// <summary>A sale, for the session list.</summary>
    public void AddSale(StallSoldEventArgs e)
    {
        _sales.Items.Insert(0, string.Create(CultureInfo.CurrentCulture,
            $"{DateTime.Now:HH:mm}  {e.Slot.ItemName} x{e.Slot.Quantity}  ·  {e.Slot.Price:N0}  ·  {(e.Buyer.Length > 0 ? e.Buyer : "?")}"));
    }

    public void RefreshView()
    {
        var client = _game();
        var stall = client?.Stall;
        _shownSlots = stall?.Slots ?? [];
        SocialUi.Fill(_slots, [.. _shownSlots.Select(s => string.Create(CultureInfo.CurrentCulture,
            $"{s.Slot + 1,2}.  {s.ItemName} x{s.Quantity}  ·  {s.Price:N0} gold"))]);
        if (client is null)
            _status.Text = "Not in game.";
        else if (_manager()?.Status is { } status && DateTime.UtcNow > _sayUntil && stall is not null)
        {
            string state = !stall.Exists ? "No stall." : stall.IsOpen ? $"Open · {stall.Slots.Count} / {MyStall.Capacity} slots" : $"Editing · {stall.Slots.Count} / {MyStall.Capacity} slots";
            _status.Text = $"{state}\n{status}";
            _status.ForeColor = stall.IsOpen ? Good : Muted;
        }
        FillGrid();
    }
}
