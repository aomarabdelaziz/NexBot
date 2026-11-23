using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// Pick Filter (phBot's Pick Filter tab): every item of the game by group, degree and rarity (or by name), with what to
/// do with it (pick up, store, sell, drop), the pick options and the gold to keep in the bag and in the storage.
/// </summary>
internal sealed class PickFilterPage : UserControl
{
    private const string Configured = "Items set in the filter";
    private static readonly (string Column, PickAction Action)[] s_actions =
        [("Pick", PickAction.Pick), ("Store", PickAction.Store), ("Sell", PickAction.Sell), ("Drop", PickAction.Drop)];

    private readonly Func<IGameData?> _data;
    private readonly Func<PickManager?> _manager;
    private readonly IconCache _icons;
    private readonly DataGridView _grid = new();
    private readonly SroCombo _category = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 6, 0) };
    private readonly SroCombo _degree = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 6, 0) };
    private readonly SroCombo _rarity = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly SroInput _search = new() { Dock = DockStyle.Fill };
    private readonly Label _count = Sro.Label("", Muted, SroTheme.Small);
    private readonly Label _status = Sro.Label("", Muted, SroTheme.Small);
    private readonly Panel _options = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
    private PickFilterSettings _settings = new();
    private Action _changed = () => { };
    private bool _filling;

    public PickFilterPage(Func<IGameData?> data, Func<PickManager?> manager, IconCache icons)
    {
        _data = data;
        _manager = manager;
        _icons = icons;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(BuildList(), 0, 0);
        var options = SocialUi.Panel("Options");
        _status.AutoSize = false;
        _status.Dock = DockStyle.Bottom;
        _status.Height = 58;
        options.Controls.Add(_options);
        options.Controls.Add(_status);
        grid.Controls.Add(options, 1, 0);
        Controls.Add(grid);
    }

    public void Bind(PickFilterSettings s, Action changed)
    {
        _settings = s;
        _changed = changed;
        _options.Controls.Clear();
        SocialUi.Stack(_options,
            SocialUi.Switch("Pick up items", s.Enabled, v => s.Enabled = v, changed),
            SocialUi.Hint("The grab pet picks up when it is out; the character picks up only when no script, trade route or town loop is moving it.", 46),
            SocialUi.Switch("Pick items first", s.PickItemsFirst, v => s.PickItemsFirst = v, changed),
            SocialUi.Switch("Use pick pet", s.UsePickPet, v => s.UsePickPet = v, changed),
            SocialUi.Switch("   Pick with character if pet is unsummoned or full", s.PickWithCharacterIfNoPet, v => s.PickWithCharacterIfNoPet = v, changed),
            SocialUi.Switch("Pick other player's items", s.PickOtherPlayersItems, v => s.PickOtherPlayersItems = v, changed),
            SocialUi.Switch("Pick party items", s.PickPartyItems, v => s.PickPartyItems = v, changed),
            SocialUi.Switch("Don't pick items", s.DontPickItems, v => s.DontPickItems = v, changed),
            SocialUi.Switch("Only pick rare or blue items", s.OnlyPickRareOrBlue, v => s.OnlyPickRareOrBlue = v, changed),
            SocialUi.Switch("Pick Arrows/Bolts", s.PickArrows, v => s.PickArrows = v, changed),
            SocialUi.Field("   Up to (in the bag)", s.ArrowAmount.ToString(CultureInfo.CurrentCulture), 80, v => s.ArrowAmount = SocialUi.Int(v, 0, 100000), changed),
            SocialUi.Caption("SELL AND STORE"),
            SocialUi.Switch("Allow selling of all item types", s.AllowSellingAllTypes, v => s.AllowSellingAllTypes = v, changed),
            SocialUi.Field("Do not sell items with plus >= (0 = off)", s.DontSellPlusAtLeast.ToString(CultureInfo.CurrentCulture), 60, v => s.DontSellPlusAtLeast = SocialUi.Int(v, 0, 255), changed),
            SocialUi.Switch("Only store rare or blue items", s.OnlyStoreRareOrBlue, v => s.OnlyStoreRareOrBlue = v, changed),
            SocialUi.Switch("Only store items with specific blue attributes", s.OnlyStoreWithBlues, v => s.OnlyStoreWithBlues = v, changed),
            SocialUi.Caption("BLUE ATTRIBUTES (PARTS OF NAMES: STR, INT, DURABILITY ...)"),
            new NameListEditor(s.BlueAttributes, changed) { Dock = DockStyle.Top, Height = 100 },
            SocialUi.Hint("Rare items are sold only with \"Allow selling of all item types\". Equipment marked to store that does not pass the store options is sold when it is also marked to sell.", 46),
            SocialUi.Caption("STORE GOLD"),
            SocialUi.Field("Gold keep amount", s.KeepGold.ToString(CultureInfo.CurrentCulture), 110, v => s.KeepGold = ParseGold(v), changed),
            SocialUi.Switch("Take gold from storage", s.TakeGoldFromStorage, v => s.TakeGoldFromStorage = v, changed),
            SocialUi.Switch("Store gold in storage", s.StoreGold, v => s.StoreGold = v, changed),
            SocialUi.Field("   Maximum amount (0 = no limit)", s.StoreGoldMax.ToString(CultureInfo.CurrentCulture), 110, v => s.StoreGoldMax = ParseGold(v), changed),
            SocialUi.Hint("The town loop stores and sells by the filter at the Storage-keeper and the shops, with the lists of the Town page, and moves the gold at the Storage-keeper. Pick items first: while hunting, the drops are picked up before the next monster. The guild storage and dismantling are not in this version.", 76));
        ShowItems();
    }

    private static ulong ParseGold(string text) =>
        ulong.TryParse(text.Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.CurrentCulture, out ulong n) ? n : 0;

    // ------------------------------------------------------------------ item list

    private SroPanel BuildList()
    {
        var panel = SocialUi.Panel("Pick filter");
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = InputBack;
        _grid.BorderStyle = BorderStyle.None;
        _grid.GridColor = Line;
        _grid.RowHeadersVisible = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = FrameDark, ForeColor = Gold, Font = SroTheme.Small, SelectionBackColor = FrameDark };
        _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = InputBack, ForeColor = SroTheme.Text, SelectionBackColor = Hex("#2a2418"), SelectionForeColor = Value, Font = SroTheme.Body };
        _grid.ColumnHeadersHeight = 26;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Item", HeaderText = "Item", ReadOnly = true, FillWeight = 52 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Lv", HeaderText = "Lv.", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 46 });
        foreach (var (column, _) in s_actions)
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = column, HeaderText = column, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 58, ToolTipText = $"Click the header to {column.ToLowerInvariant()} every item shown (again: none)" });
        ItemVisuals.IconGrid(_grid, "Item", _icons, row => row.Tag is ItemInfo info ? ItemVisuals.Describe(info, Summary(_settings.Get(info.ServerName))) : null);
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += OnCellChanged;
        _grid.DataError += (_, e) => e.Cancel = true;
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex >= 2 && e.Button == MouseButtons.Left)
            {
                var action = s_actions[e.ColumnIndex - 2].Action;
                var rows = _grid.Rows.Cast<DataGridViewRow>().ToList();
                bool all = rows.Count > 0 && rows.All(r => r.Tag is ItemInfo i && _settings.Get(i.ServerName).HasFlag(action));
                Apply(rows, action, !all);
            }
        };
        _grid.ContextMenuStrip = BuildMenu();

        _category.SetItems([Configured, .. PickFilter.Categories.Select(c => c.Name)]);
        _category.SelectedIndex = 0;
        _degree.SetItems(["Any degree", .. Enumerable.Range(1, 11).Select(d => $"Degree {d}")]);
        _degree.SelectedIndex = 0;
        _rarity.SetItems(["Normal and rare", "Normal only", "Rare (SoX) only"]);
        _rarity.SelectedIndex = 0;
        foreach (var combo in new[] { _category, _degree, _rarity })
            combo.SelectedIndexChanged += (_, _) =>
            {
                if (!_filling)
                {
                    _search.Text = "";
                    ShowItems();
                }
            };
        var combos = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, Margin = Padding.Empty };
        combos.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        combos.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        combos.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        combos.Controls.Add(_category, 0, 0);
        combos.Controls.Add(_degree, 1, 0);
        combos.Controls.Add(_rarity, 2, 0);

        var searchRow = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.Transparent };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 262, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(6, 0, 0, 0) };
        foreach (var (text, width, click) in new (string, int, Action)[]
                 { ("Search", 66, ShowItems), ("Clear", 56, ClearList), ("Update", 66, ShowItems), ("Reset", 56, Reset) })
        {
            var b = SocialUi.Button(text, width, click);
            b.Height = 28;
            b.Margin = new Padding(0, 0, 4, 0);
            buttons.Controls.Add(b);
        }
        _search.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                ShowItems();
            }
        };
        searchRow.Controls.Add(_search);
        searchRow.Controls.Add(buttons);
        _count.AutoSize = false;
        _count.Dock = DockStyle.Bottom;
        _count.Height = 22;

        var gridWell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Line };
        gridWell.Controls.Add(_grid);
        panel.Controls.Add(gridWell);
        panel.Controls.Add(_count);
        panel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Color.Transparent });
        panel.Controls.Add(searchRow);
        panel.Controls.Add(combos);
        panel.Controls.Add(SocialUi.Hint("Choose a group, degree and rarity, or search a name and press Enter. Tick what to do with each item; select several rows and right-click to set them together, or click a column header for every item shown. Gold: set Pick on the three gold drops.", 50));
        return panel;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new SroMenuRenderer(), ShowImageMargin = false, Font = SroTheme.Body };
        List<DataGridViewRow> Selected() => [.. _grid.SelectedRows.Cast<DataGridViewRow>()];
        foreach (var (column, action) in s_actions)
            menu.Items.Add(column, null, (_, _) => Apply(Selected(), action, true));
        menu.Items.Add(new ToolStripSeparator());
        foreach (var (column, action) in s_actions)
            menu.Items.Add($"Don't {column.ToLowerInvariant()}", null, (_, _) => Apply(Selected(), action, false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Clear (do nothing with them)", null, (_, _) =>
        {
            foreach (var row in Selected())
                if (row.Tag is ItemInfo info)
                    _settings.Set(info.ServerName, PickAction.None);
            Saved();
        });
        menu.Opening += (_, e) =>
        {
            // A right-click on a row that is not selected selects it alone, as lists do.
            var at = _grid.PointToClient(Cursor.Position);
            var hit = _grid.HitTest(at.X, at.Y);
            if (hit.RowIndex >= 0 && !_grid.Rows[hit.RowIndex].Selected)
            {
                _grid.ClearSelection();
                _grid.Rows[hit.RowIndex].Selected = true;
            }
            e.Cancel = _grid.SelectedRows.Count == 0;
        };
        return menu;
    }

    private void Apply(IEnumerable<DataGridViewRow> rows, PickAction action, bool on)
    {
        foreach (var row in rows)
            if (row.Tag is ItemInfo info)
            {
                var now = _settings.Get(info.ServerName);
                _settings.Set(info.ServerName, on ? now | action : now & ~action);
            }
        Saved();
    }

    private void Saved()
    {
        _changed();
        Refill();
    }

    /// <summary>The checkboxes again from the settings (after a group change).</summary>
    private void Refill()
    {
        _filling = true;
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Tag is ItemInfo info)
            {
                var action = _settings.Get(info.ServerName);
                foreach (var (column, flag) in s_actions)
                    row.Cells[column].Value = action.HasFlag(flag);
            }
        _filling = false;
        _grid.Invalidate();
        ShowCount();
    }

    private static string? Summary(PickAction action) =>
        action == PickAction.None ? null : "Filter: " + string.Join(", ", s_actions.Where(a => action.HasFlag(a.Action)).Select(a => a.Column.ToLowerInvariant()));

    private void OnCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_filling || e.RowIndex < 0 || e.ColumnIndex < 2 || _grid.Rows[e.RowIndex].Tag is not ItemInfo info)
            return;
        var row = _grid.Rows[e.RowIndex];
        var action = PickAction.None;
        foreach (var (column, flag) in s_actions)
            if (row.Cells[column].Value is true)
                action |= flag;
        _settings.Set(info.ServerName, action);
        _changed();
        ShowCount();
    }

    /// <summary>The items of the chosen group (or the search), as phBot's Update does.</summary>
    private void ShowItems()
    {
        if (_data() is not { } data)
        {
            Fill([]);
            _count.Text = "Start the bot once (Server page) so the game database is loaded.";
            return;
        }
        int degree = Math.Max(0, _degree.SelectedIndex);
        bool? rare = _rarity.SelectedIndex switch { 1 => false, 2 => true, _ => null };
        string text = _search.Text.Trim();
        List<ItemInfo> items;
        if (text.Length >= 2)
            items = [.. data.SearchItems(text, 500).Where(i => (degree == 0 || AlchemyCatalog.DegreeOf(i.ServerName) == degree) && (rare is null || PickFilter.IsRare(i.ServerName) == rare))];
        else if (_category.SelectedIndex <= 0)
            items = [.. _settings.Items.Keys.Select(data.GetItem).OfType<ItemInfo>()
                .Where(i => (degree == 0 || AlchemyCatalog.DegreeOf(i.ServerName) == degree) && (rare is null || PickFilter.IsRare(i.ServerName) == rare))
                .OrderBy(i => i.Tid2).ThenBy(i => i.Tid3).ThenBy(i => i.Level).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
        else
            items = PickFilter.ItemsOf(data, PickFilter.Categories[_category.SelectedIndex - 1], degree, rare);
        Fill(items);
    }

    private void Fill(IReadOnlyList<ItemInfo> items)
    {
        _filling = true;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var info in items.Take(4000))
        {
            var action = _settings.Get(info.ServerName);
            int i = _grid.Rows.Add(PickFilter.Label(info), info.Level > 0 ? info.Level.ToString(CultureInfo.CurrentCulture) : "",
                action.HasFlag(PickAction.Pick), action.HasFlag(PickAction.Store), action.HasFlag(PickAction.Sell), action.HasFlag(PickAction.Drop));
            _grid.Rows[i].Tag = info;
        }
        _grid.ResumeLayout();
        _filling = false;
        ShowCount();
    }

    private void ShowCount()
    {
        int shown = _grid.Rows.Count;
        _count.Text = string.Create(CultureInfo.CurrentCulture, $"{shown:N0} item(s) shown  ·  {_settings.Items.Count:N0} item(s) set in the filter")
            + (shown == 0 && _category.SelectedIndex <= 0 && _search.Text.Trim().Length < 2 ? "  ·  choose a group to start" : "");
    }

    /// <summary>Clears the window (phBot: to free memory); the filter stays.</summary>
    private void ClearList()
    {
        _search.Text = "";
        Fill([]);
    }

    private void Reset()
    {
        if (MessageBox.Show(this, "Reset the whole pick filter (every item's pick, store, sell and drop)?", "NexBot",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _settings.Items.Clear();
        _changed();
        ShowItems();
    }

    public void RefreshView()
    {
        if (_manager() is { } manager)
        {
            _status.Text = manager.Status;
            return;
        }
        _status.Text = _settings.Enabled ? "Picks up when the bot is in game." : "Picking up is off.";
    }
}
