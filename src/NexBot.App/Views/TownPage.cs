using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>Town (phBot's Town tab): the buy list, the town scripts and the town options.</summary>
internal sealed class TownPage : UserControl
{
    private readonly Func<IGameData?> _data;
    private readonly Func<TownRunner?> _runner;
    private readonly DataGridView _grid = new();
    private readonly SroInput _search = new() { Dock = DockStyle.Fill };
    private readonly ListBox _scripts = SocialUi.List();
    private readonly Label _training = Sro.Label("", SroTheme.Value, SroTheme.Small);
    private readonly Label _status = Sro.Label("Idle.", Muted, SroTheme.Small);
    private readonly SroSwitch _loopTraining = new() { Text = "Loop the training script", Dock = DockStyle.Top, Height = 28 };
    private readonly Panel _options = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
    private TownSettings _settings = new();
    private Action _changed = () => { };
    private bool _filling;

    private readonly IconCache _icons;

    public TownPage(Func<IGameData?> data, Func<TownRunner?> runner, IconCache icons)
    {
        _icons = icons;
        _data = data;
        _runner = runner;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(BuildBuyList(), 0, 0);
        grid.Controls.Add(BuildScripts(), 1, 0);
        var options = SocialUi.Panel("Options");
        options.Controls.Add(_options);
        grid.Controls.Add(options, 2, 0);
        Controls.Add(grid);
        _loopTraining.CheckedChanged += (_, _) =>
        {
            _settings.LoopTrainingScript = _loopTraining.Checked;
            _changed();
        };
    }

    public void Bind(TownSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        FillScripts();
        ShowSaved();
        _loopTraining.Checked = settings.LoopTrainingScript;
        _options.Controls.Clear();
        var sell = new NameListEditor(settings.Sell, changed) { Dock = DockStyle.Top, Height = 100 };
        var drop = new NameListEditor(settings.Drop, changed) { Dock = DockStyle.Top, Height = 100 };
        var store = new NameListEditor(settings.Store, changed) { Dock = DockStyle.Top, Height = 100 };
        SocialUi.Stack(_options,
            SocialUi.Switch("Run the town loop after returning to town", settings.RunAfterReturn, v => settings.RunAfterReturn = v, changed),
            SocialUi.Hint("After a return scroll or a revive in town, by the bot or by you in the game.", 32),
            SocialUi.Switch("Repair at the blacksmith / armor shop", settings.Repair, v => settings.Repair = v, changed),
            SocialUi.Switch("Only repair the worn equipment", settings.RepairEquippedOnly, v => settings.RepairEquippedOnly = v, changed),
            SocialUi.Switch("Sell excess potions, arrows and bolts", settings.SellExcess, v => settings.SellExcess = v, changed),
            SocialUi.Switch("Stop if the inventory is full when buying", settings.StopIfInventoryFull, v => settings.StopIfInventoryFull = v, changed),
            SocialUi.Caption("SELL THESE ITEMS (PARTS OF NAMES)"),
            SocialUi.Hint("Sold at the first shop on the way. Items on the buy list are never sold here.", 32),
            sell,
            SocialUi.Caption("DROP THESE ITEMS (PARTS OF NAMES)"),
            SocialUi.Hint("Dropped on the ground when the town loop starts. Items on the buy list are never dropped.", 32),
            drop,
            SocialUi.Caption("PUT THESE ITEMS IN STORAGE (PARTS OF NAMES)"),
            SocialUi.Hint("Put into the storage at the Storage-keeper on the way. Items on the buy list are never stored.", 32),
            store);
    }

    // ------------------------------------------------------------------ buy list

    private SroPanel BuildBuyList()
    {
        var panel = SocialUi.Panel("Buy list");
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
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = "On", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Item", HeaderText = "Item", ReadOnly = true, FillWeight = 46 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Npc", HeaderText = "Sold by", ReadOnly = true, FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Qty", HeaderText = "Keep", FillWeight = 14, ToolTipText = "How many to have after buying" });
        ItemVisuals.IconGrid(_grid, "Item", _icons, row => row.Tag is string item
            ? ItemVisuals.Describe(_data()?.GetItem(item), row.Cells["Npc"].Value is string npc && npc.Length > 0 ? "Sold by " + npc : null)
            : null);
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += OnCellChanged;
        _grid.DataError += (_, e) => e.Cancel = true;

        var searchRow = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.Transparent };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 230, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(6, 0, 0, 0) };
        foreach (var (text, width, click) in new (string, int, Action)[]
                 { ("Search", 66, Search), ("All", 46, () => Show(Catalog())), ("Mine", 52, ShowSaved), ("Reset", 54, Reset) })
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
                Search();
            }
        };
        searchRow.Controls.Add(_search);
        searchRow.Controls.Add(buttons);

        var gridWell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Line };
        gridWell.Controls.Add(_grid);
        panel.Controls.Add(gridWell);
        panel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Color.Transparent });
        panel.Controls.Add(searchRow);
        panel.Controls.Add(SocialUi.Hint("Search an item any NPC sells, tick it and type how many to keep. The town loop buys the rest at the NPC that sells it. \"Mine\" shows your list.", 50));
        return panel;
    }

    private List<(string Item, string Name, string Npcs)> Catalog(string filter = "")
    {
        if (_data() is not { } data)
            return [];
        return data.GetShopCatalog()
            .GroupBy(x => x.Item, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Item: g.Key, Name: data.GetItem(g.Key)?.Name is { Length: > 0 } n ? n : g.Key, Npcs: NpcNames(data, g.Select(x => x.Npc))))
            .Where(x => filter.Length == 0 || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || x.Item.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string NpcNames(IGameData data, IEnumerable<string> npcs)
    {
        var names = npcs.Select(n => data.GetModel(n)?.Name is { Length: > 0 } name ? name : n).Distinct().ToList();
        return names.Count <= 2 ? string.Join(", ", names) : $"{names[0]}, {names[1]} +{names.Count - 2}";
    }

    private void Search()
    {
        string text = _search.Text.Trim();
        if (text.Length < 2)
        {
            ShowSaved();
            return;
        }
        Show(Catalog(text));
    }

    private void ShowSaved()
    {
        var data = _data();
        var all = data is null ? [] : Catalog().ToDictionary(x => x.Item, StringComparer.OrdinalIgnoreCase);
        Show([.. _settings.Buy.Where(b => b.Enabled || b.Quantity > 0).Select(b =>
            all.TryGetValue(b.Item, out var x) ? x : (b.Item, data?.GetItem(b.Item)?.Name ?? b.Item, "?"))]);
    }

    private void Show(List<(string Item, string Name, string Npcs)> rows)
    {
        _filling = true;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var (item, name, npcs) in rows.Take(3000))
        {
            var saved = _settings.Buy.FirstOrDefault(b => string.Equals(b.Item, item, StringComparison.OrdinalIgnoreCase));
            int i = _grid.Rows.Add(saved?.Enabled == true, name, npcs, (saved?.Quantity ?? 0).ToString(CultureInfo.CurrentCulture));
            _grid.Rows[i].Tag = item;
        }
        _grid.ResumeLayout();
        _filling = false;
    }

    private void OnCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_filling || e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not string item)
            return;
        var row = _grid.Rows[e.RowIndex];
        var entry = _settings.Get(item);
        entry.Enabled = row.Cells["On"].Value is true;
        entry.Quantity = SocialUi.Int(Convert.ToString(row.Cells["Qty"].Value, CultureInfo.CurrentCulture) ?? "0", 0, 100000);
        if (entry.Quantity > 0 && e.ColumnIndex == row.Cells["Qty"].ColumnIndex && !entry.Enabled)
        {
            entry.Enabled = true;
            _filling = true;
            row.Cells["On"].Value = true;
            _filling = false;
        }
        _settings.Buy.RemoveAll(b => !b.Enabled && b.Quantity == 0);
        _changed();
    }

    private void Reset()
    {
        if (MessageBox.Show(this, "Clear the whole buy list?", "NexBot", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _settings.Buy.Clear();
        _changed();
        ShowSaved();
    }

    // ------------------------------------------------------------------ scripts

    private SroPanel BuildScripts()
    {
        var panel = SocialUi.Panel("Town scripts");
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var listWell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        listWell.Controls.Add(_scripts);
        _training.AutoSize = false;
        _training.Height = 36;
        _training.AutoEllipsis = true;
        _status.AutoSize = false;
        _status.Height = 52;
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 290, BackColor = Color.Transparent };
        SocialUi.Stack(bottom,
            SocialUi.Buttons(SocialUi.Button("Add…", 64, AddScript), SocialUi.Button("Remove", 76, RemoveScript)),
            SocialUi.Caption("THEN WALK TO TRAINING"),
            _training,
            SocialUi.Buttons(SocialUi.Button("Choose…", 80, ChooseTraining), SocialUi.Button("Clear", 60, () =>
            {
                _settings.TrainingScript = "";
                _changed();
                FillScripts();
            })),
            _loopTraining,
            SocialUi.Caption("TOWN LOOP"),
            SocialUi.Buttons(SocialUi.Button("Run now", 84, RunNow), SocialUi.Button("Stop", 60, () => _runner()?.Stop())),
            _status);
        body.Controls.Add(listWell);
        body.Controls.Add(bottom);
        panel.Controls.Add(body);
        panel.Controls.Add(SocialUi.Hint("One walking script per town (Script page): from where the return scroll puts you, past the NPCs, to the town gate. Every shop NPC near the route is visited.", 64));
        return panel;
    }

    private static string ScriptFolder
    {
        get
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "Scripts");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private void FillScripts()
    {
        SocialUi.Fill(_scripts, [.. _settings.Scripts.Select(Path.GetFileNameWithoutExtension).Select(n => n ?? "")]);
        _training.Text = _settings.TrainingScript.Length > 0 ? Path.GetFileName(_settings.TrainingScript) : "(none: stop at the end of the town script)";
    }

    private void AddScript()
    {
        using var dialog = new OpenFileDialog { Filter = "Walk scripts (*.txt)|*.txt|All files|*.*", InitialDirectory = ScriptFolder, Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        foreach (string file in dialog.FileNames)
            if (!_settings.Scripts.Contains(file, StringComparer.OrdinalIgnoreCase))
                _settings.Scripts.Add(file);
        _changed();
        FillScripts();
    }

    private void RemoveScript()
    {
        if (_scripts.SelectedIndex < 0 || _scripts.SelectedIndex >= _settings.Scripts.Count)
            return;
        _settings.Scripts.RemoveAt(_scripts.SelectedIndex);
        _changed();
        FillScripts();
    }

    private void ChooseTraining()
    {
        using var dialog = new OpenFileDialog { Filter = "Walk scripts (*.txt)|*.txt|All files|*.*", InitialDirectory = ScriptFolder };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _settings.TrainingScript = dialog.FileName;
        _changed();
        FillScripts();
    }

    private void RunNow()
    {
        if (_runner() is not { } runner)
        {
            MessageBox.Show(this, "Start the bot and enter the game first.", "NexBot", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        runner.Start();
    }

    /// <summary>Shows the runner's status (called by the main form when it changes).</summary>
    public void ShowStatus(string status, bool running)
    {
        _status.Text = status;
        _status.ForeColor = running ? Good : Muted;
    }
}
