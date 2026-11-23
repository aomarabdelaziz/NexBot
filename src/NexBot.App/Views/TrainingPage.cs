using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// Training Area (phBot's Training Area page): the list of named areas (position, radius, pick radius, polygon, walk
/// script, which one is enabled), and the Conditions, Collision, Settings and Script tabs.
/// </summary>
internal sealed class TrainingPage : UserControl
{
    private readonly Func<SroClient?> _game;
    private readonly Func<TownRunner?> _town;
    private readonly DataGridView _grid = new();
    private readonly Label _status = Sro.Label("", Muted, SroTheme.Small);
    private SroTabs? _tabs;
    private BotSettings _settings = new();
    private Action _changed = () => { };
    private bool _filling;

    public TrainingPage(Func<SroClient?> game, Func<TownRunner?> town)
    {
        _game = game;
        _town = town;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        Padding = new Padding(5);
    }

    public void Bind(BotSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        settings.EnsureTrainingAreas();
        var tabs = new SroTabs { Dock = DockStyle.Fill };
        tabs.Add("Training Area", AreasTab());
        tabs.Add("Conditions", ConditionsTab());
        tabs.Add("Collision", CollisionTab());
        tabs.Add("Settings", SettingsTab());
        tabs.Add("Script", ScriptTab());
        if (_tabs is not null)
        {
            Controls.Remove(_tabs);
            _tabs.Dispose();
        }
        _tabs = tabs;
        Controls.Add(tabs);
        ShowAreas();
    }

    public void SelectTab(int index)
    {
        if (_tabs is not null)
            _tabs.SelectedIndex = index;
    }

    private TrainingSetupSettings Setup => _settings.TrainingSetup;

    private SroSwitch Switch(string text, bool value, Action<bool> set) => SocialUi.Switch(text, value, set, _changed);

    private Panel Number(string text, int value, int min, int max, Action<int> set) =>
        SocialUi.Field(text, value.ToString(CultureInfo.CurrentCulture), 64, v => set(SocialUi.Int(v, min, max)), _changed);

    private static SroPanel Box(string title, params Control[] items)
    {
        var panel = SocialUi.Panel(title);
        var inner = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
        SocialUi.Stack(inner, items);
        panel.Controls.Add(inner);
        return panel;
    }

    private static TableLayoutPanel Grid(params int[] percents)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = percents.Length, RowCount = 1, BackColor = Bg, Margin = Padding.Empty };
        foreach (int p in percents)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, p));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return grid;
    }

    // ------------------------------------------------------------------ the area list

    private Control AreasTab()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = InputBack;
        _grid.BorderStyle = BorderStyle.None;
        _grid.GridColor = Line;
        _grid.RowHeadersVisible = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = FrameDark, ForeColor = Gold, Font = SroTheme.Small, SelectionBackColor = FrameDark };
        _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = InputBack, ForeColor = SroTheme.Text, SelectionBackColor = Hex("#2a2418"), SelectionForeColor = Value, Font = SroTheme.Body };
        _grid.ColumnHeadersHeight = 26;
        _grid.RowTemplate.Height = 28;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "On", HeaderText = "", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 28 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Where", HeaderText = "Coordinates", ReadOnly = true, FillWeight = 24 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Radius", HeaderText = "Radius", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pick", HeaderText = "Pick radius", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 86 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Shape", HeaderText = "Area", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Script", HeaderText = "Script", ReadOnly = true, FillWeight = 30 });
        _grid.CellEndEdit += OnCellEdited;
        _grid.DataError += (_, e) => e.Cancel = true;
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex is 0 or 2 or 5 or 6)
                Enable();
        };
        _grid.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && _grid.HitTest(e.X, e.Y) is { RowIndex: >= 0 } hit)
                _grid.CurrentCell = _grid.Rows[hit.RowIndex].Cells[1];
        };
        _grid.ContextMenuStrip = Menu();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 72, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 0) };
        foreach (var b in new[]
        {
            SocialUi.Button("Add", 60, Add), SocialUi.Button("Get Position", 104, GetPosition), SocialUi.Button("Enable", 70, Enable),
            SocialUi.Button("Walk", 64, Walk), SocialUi.Button("Stop", 60, () => _town()?.Stop()),
            SocialUi.Button("Change Script", 116, ChangeScript), SocialUi.Button("Clear Script", 100, ClearScript),
            SocialUi.Button("Use Radius", 96, () => Shape(false)), SocialUi.Button("Use Polygon", 104, () => Shape(true)),
            SocialUi.Button("Delete", 70, Delete),
        })
        {
            b.Height = 28;
            b.Margin = new Padding(0, 0, 6, 6);
            buttons.Controls.Add(b);
        }
        _status.AutoSize = false;
        _status.Dock = DockStyle.Bottom;
        _status.Height = 48;
        var panel = SocialUi.Panel("Training areas");
        var inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(_grid);
        inner.Controls.Add(well);
        inner.Controls.Add(buttons);
        inner.Controls.Add(SocialUi.Hint("Add an area, stand where you want to train and click Get Position (or right-click the Map → Set training area). The enabled area (★) is where the bot hunts; double-click a row to enable it. Edit the name, radius and pick radius in the list. With no script, the bot walks to the area on the map's routes after town.", 48));
        inner.Controls.Add(_status);
        panel.Controls.Add(inner);
        return panel;
    }

    private ContextMenuStrip Menu()
    {
        var menu = new ContextMenuStrip { Renderer = new SroMenuRenderer(), ShowImageMargin = false, Font = SroTheme.Body };
        menu.Items.Add("Add", null, (_, _) => Add());
        menu.Items.Add("Enable", null, (_, _) => Enable());
        menu.Items.Add("Walk", null, (_, _) => Walk());
        menu.Items.Add("Stop walking", null, (_, _) => _town()?.Stop());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Set Coordinates (here)", null, (_, _) => GetPosition());
        menu.Items.Add("Set Coordinates (from the clipboard)", null, (_, _) => PasteCoordinates());
        menu.Items.Add("Set Polygon (from the clipboard)", null, (_, _) => PastePolygon());
        menu.Items.Add("Use Radius", null, (_, _) => Shape(false));
        menu.Items.Add("Use Polygon", null, (_, _) => Shape(true));
        menu.Items.Add("Copy Coordinates", null, (_, _) => Copy(a => string.Create(CultureInfo.InvariantCulture, $"{a.X:0.#},{a.Y:0.#}")));
        menu.Items.Add("Copy Polygon", null, (_, _) => Copy(a => string.Join(";", a.Polygon.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:0.#},{p.Y:0.#}")))));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Change Script", null, (_, _) => ChangeScript());
        menu.Items.Add("Clear Script", null, (_, _) => ClearScript());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete", null, (_, _) => Delete());
        return menu;
    }

    private TrainingAreaSettings? Selected() =>
        _grid.CurrentRow?.Index is int i and >= 0 && i < Setup.Areas.Count ? Setup.Areas[i] : null;

    private void ShowAreas()
    {
        _filling = true;
        try
        {
            int keep = _grid.CurrentRow?.Index ?? 0;
            _grid.Rows.Clear();
            foreach (var a in Setup.Areas)
            {
                bool on = string.Equals(a.Name, Setup.Enabled, StringComparison.OrdinalIgnoreCase);
                string where = a.IsSet ? string.Create(CultureInfo.CurrentCulture, $"X {a.X:0}, Y {a.Y:0}") : "(not set)";
                string shape = a.UsePolygon && a.HasPolygon ? $"Polygon ({a.Polygon.Count})" : "Radius";
                string script = a.Script.Length > 0 ? Path.GetFileName(a.Script) : "(map routes)";
                int row = _grid.Rows.Add(on ? "★" : "", a.Name, where, a.Radius, a.PickRadius, shape, script);
                if (on)
                    _grid.Rows[row].DefaultCellStyle.ForeColor = Gold;
            }
            if (_grid.Rows.Count > 0)
                _grid.CurrentCell = _grid.Rows[Math.Clamp(keep, 0, _grid.Rows.Count - 1)].Cells[1];
        }
        finally
        {
            _filling = false;
        }
        _conditionsRefresh?.Invoke();
    }

    /// <summary>The enabled area changed: copy it again into the area the bot uses.</summary>
    private void Changed(TrainingAreaSettings area)
    {
        if (string.Equals(area.Name, Setup.Enabled, StringComparison.OrdinalIgnoreCase))
            _settings.EnableTrainingArea(area.Name);
        _changed();
        ShowAreas();
    }

    private void OnCellEdited(object? sender, DataGridViewCellEventArgs e)
    {
        if (_filling || e.RowIndex < 0 || e.RowIndex >= Setup.Areas.Count)
            return;
        var a = Setup.Areas[e.RowIndex];
        string text = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString()?.Trim() ?? "";
        switch (_grid.Columns[e.ColumnIndex].Name)
        {
            case "Name" when text.Length > 0 && Setup.Areas.All(o => o == a || !string.Equals(o.Name, text, StringComparison.OrdinalIgnoreCase)):
                bool wasOn = string.Equals(a.Name, Setup.Enabled, StringComparison.OrdinalIgnoreCase);
                foreach (var c in Setup.Conditions.Where(c => string.Equals(c.Area, a.Name, StringComparison.OrdinalIgnoreCase)))
                    c.Area = text;
                a.Name = text;
                if (wasOn)
                    Setup.Enabled = text;
                break;
            case "Radius":
                a.Radius = SocialUi.Int(text, 5, 2000);
                break;
            case "Pick":
                a.PickRadius = SocialUi.Int(text, 5, 300);
                break;
        }
        BeginInvoke(() => Changed(a));
    }

    private void Add()
    {
        int n = Setup.Areas.Count + 1;
        string name;
        do
            name = $"Area {n++}";
        while (Setup.Find(name) is not null);
        var area = new TrainingAreaSettings { Name = name };
        if (_game()?.World.Character is { } c)
        {
            var at = c.EstimatePosition();
            (area.X, area.Y, area.Region) = (Math.Round(at.PosX, 1), Math.Round(at.PosY, 1), at.InDungeon ? at.Region : (ushort)0);
        }
        Setup.Areas.Add(area);
        if (Setup.Areas.Count == 1)
            _settings.EnableTrainingArea(name);
        _changed();
        ShowAreas();
        _grid.CurrentCell = _grid.Rows[^1].Cells[1];
        _status.Text = area.IsSet ? $"{name} added where you stand." : $"{name} added: log in and click Get Position.";
    }

    private void GetPosition()
    {
        if (Selected() is not { } a)
            return;
        if (_game()?.World.Character is not { } c)
        {
            _status.Text = "Log in first: Get Position takes where the character stands.";
            return;
        }
        var at = c.EstimatePosition();
        (a.X, a.Y, a.Region) = (Math.Round(at.PosX, 1), Math.Round(at.PosY, 1), at.InDungeon ? at.Region : (ushort)0);
        _status.Text = string.Create(CultureInfo.CurrentCulture, $"{a.Name}: X {a.X:0}, Y {a.Y:0}.");
        Changed(a);
    }

    private void Enable()
    {
        if (Selected() is not { } a)
            return;
        _settings.EnableTrainingArea(a.Name);
        _changed();
        ShowAreas();
        _status.Text = $"{a.Name} is the training area now.";
    }

    private void Walk()
    {
        if (Selected() is not { } a)
            return;
        if (_town() is not { } town)
        {
            _status.Text = "Start the bot first (Server page).";
            return;
        }
        _status.Text = town.WalkToArea(a)
            ? $"Walking to {a.Name} on the map's routes (Stop to stop)."
            : "Cannot walk there: the map data is not loaded (client path), the area is not set, or you are in a dungeon.";
    }

    private void ChangeScript()
    {
        if (Selected() is not { } a)
            return;
        using var dialog = new OpenFileDialog { Filter = "Walk scripts (*.txt)|*.txt|All files (*.*)|*.*", Title = $"Walk script to {a.Name}" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        a.Script = dialog.FileName;
        Changed(a);
    }

    private void ClearScript()
    {
        if (Selected() is not { } a)
            return;
        a.Script = "";
        Changed(a);
    }

    private void Shape(bool polygon)
    {
        if (Selected() is not { } a)
            return;
        if (polygon && !a.HasPolygon)
        {
            _status.Text = "Draw the polygon on the Map page first, or use Set Polygon (from the clipboard).";
            return;
        }
        a.UsePolygon = polygon;
        Changed(a);
    }

    private void Copy(Func<TrainingAreaSettings, string> text)
    {
        if (Selected() is { } a && text(a) is { Length: > 0 } t)
        {
            Clipboard.SetText(t);
            _status.Text = "Copied.";
        }
    }

    private static List<MapPoint> Points(string text)
    {
        var points = new List<MapPoint>();
        foreach (string part in text.Split([';', '\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = part.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (xy.Length >= 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                points.Add(new MapPoint(x, y));
        }
        return points;
    }

    private void PasteCoordinates()
    {
        if (Selected() is not { } a || Points(Clipboard.GetText()) is not { Count: > 0 } points)
        {
            _status.Text = "The clipboard has no coordinates (X,Y).";
            return;
        }
        (a.X, a.Y) = (points[0].X, points[0].Y);
        Changed(a);
    }

    private void PastePolygon()
    {
        if (Selected() is not { } a || Points(Clipboard.GetText()) is not { Count: >= 3 } points)
        {
            _status.Text = "The clipboard has no polygon (at least 3 points X,Y separated by ;).";
            return;
        }
        a.Polygon = points;
        if (!a.IsSet)
            (a.X, a.Y) = (points.Average(p => p.X), points.Average(p => p.Y));
        a.UsePolygon = true;
        Changed(a);
    }

    private void Delete()
    {
        if (Selected() is not { } a)
            return;
        Setup.Areas.Remove(a);
        if (string.Equals(a.Name, Setup.Enabled, StringComparison.OrdinalIgnoreCase))
        {
            Setup.Enabled = "";
            if (Setup.Areas.Count > 0)
                _settings.EnableTrainingArea(Setup.Areas[0].Name);
        }
        _changed();
        ShowAreas();
    }

    /// <summary>The map or Train here changed the area the bot uses: show it in the list.</summary>
    public void RefreshAreas()
    {
        _settings.SaveTrainingToList();
        ShowAreas();
    }

    // ------------------------------------------------------------------ Conditions

    private Action? _conditionsRefresh;

    private Control ConditionsTab()
    {
        var list = SocialUi.List();
        var level = new SroInput { Text = "1" };
        var area = new SroCombo();
        void Refresh()
        {
            SocialUi.Fill(list, Setup.Conditions.Count == 0 ? ["(no condition)"]
                : [.. Setup.Conditions.OrderBy(c => c.Level).Select(c => $"Level {c.Level}  →  {c.Area}")]);
            var names = Setup.Areas.Select(a => a.Name).ToList();
            if (area.Items.Count != names.Count || !area.Items.Cast<object>().Select(o => o.ToString()).SequenceEqual(names))
            {
                area.SetItems(names);
                if (names.Count > 0)
                    area.SelectedIndex = 0;
            }
        }
        _conditionsRefresh = Refresh;
        void Add()
        {
            if (area.SelectedIndex < 0 || area.SelectedIndex >= Setup.Areas.Count)
                return;
            int lv = SocialUi.Int(level.Text.Trim(), 1, 140);
            Setup.Conditions.RemoveAll(c => c.Level == lv);
            Setup.Conditions.Add(new TrainingCondition { Level = lv, Area = Setup.Areas[area.SelectedIndex].Name });
            _changed();
            Refresh();
        }
        void Remove()
        {
            var ordered = Setup.Conditions.OrderBy(c => c.Level).ToList();
            if (list.SelectedIndex < 0 || list.SelectedIndex >= ordered.Count)
                return;
            Setup.Conditions.Remove(ordered[list.SelectedIndex]);
            _changed();
            Refresh();
        }
        Refresh();
        var well = new Panel { Dock = DockStyle.Top, Height = 260, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(list);
        var levelRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent, Padding = new Padding(0, 2, 0, 2) };
        var levelLabel = Sro.Label("At level", SroTheme.Text, SroTheme.Small);
        levelLabel.AutoSize = false;
        levelLabel.Width = 70;
        levelLabel.Dock = DockStyle.Left;
        levelLabel.TextAlign = ContentAlignment.MiddleLeft;
        level.Dock = DockStyle.Left;
        level.Width = 60;
        var toLabel = Sro.Label("  train in", SroTheme.Text, SroTheme.Small);
        toLabel.AutoSize = false;
        toLabel.Width = 70;
        toLabel.Dock = DockStyle.Left;
        toLabel.TextAlign = ContentAlignment.MiddleLeft;
        area.Dock = DockStyle.Fill;
        levelRow.Controls.Add(area);
        levelRow.Controls.Add(toLabel);
        levelRow.Controls.Add(level);
        levelRow.Controls.Add(levelLabel);
        var grid = Grid(55, 45);
        grid.Controls.Add(Box("Conditions", well, levelRow, SocialUi.Buttons(SocialUi.Button("Add", 60, Add), SocialUi.Button("Remove", 70, Remove))), 0, 0);
        grid.Controls.Add(Box("How it works",
            SocialUi.Hint("When the character reaches a level, the bot switches to that condition's training area (the highest condition at or under the level). The next town loop walks there with the area's script, or on the map's routes.", 92)), 1, 0);
        return grid;
    }

    // ------------------------------------------------------------------ Collision, Settings, Script

    private Control CollisionTab()
    {
        var s = Setup;
        var grid = Grid(55, 45);
        grid.Controls.Add(Box("Collision",
            Switch("Enable collision detection in the training area", s.CollisionDetection, v => s.CollisionDetection = v),
            Switch("Navigate around obstacles", s.NavigateAroundObstacles, v => s.NavigateAroundObstacles = v),
            Switch("Navigate to item drops", s.NavigateToDrops, v => s.NavigateToDrops = v),
            AttackPage.Missing("Disable Samarkand", "routes don't teleport"),
            AttackPage.Missing("Disable Alexandria", "routes don't teleport"),
            AttackPage.Missing("Disable Guide/Advice NPCs", "routes don't teleport"),
            AttackPage.Missing("Ignore teleport level", "routes don't teleport")), 0, 0);
        grid.Controls.Add(Box("About",
            SocialUi.Hint("Collision detection leaves monsters and drops that are behind walls. It uses the map data of the client (Settings: client path), the same as the town loop's own routes. Walking between towns by teleport is not part of the routes.", 92)), 1, 0);
        return grid;
    }

    private Control SettingsTab()
    {
        var s = Setup;
        var grid = Grid(55, 45);
        grid.Controls.Add(Box("Settings",
            Switch("Don't walk around", s.DontWalkAround, v => s.DontWalkAround = v),
            Switch("Use a repair hammer", s.UseRepairHammer, v => s.UseRepairHammer = v),
            Number("   When durability is under", s.RepairHammerBelow, 1, 200, v => s.RepairHammerBelow = v),
            Switch("Use a reverse return scroll when you die", s.ReverseReturnOnDeath, v => s.ReverseReturnOnDeath = v),
            Switch("Use a reverse return scroll after returning to town", s.ReverseReturnAfterReturn, v => s.ReverseReturnAfterReturn = v),
            Switch("Use monster summon scrolls in the training area", s.UseSummonScrolls, v => s.UseSummonScrolls = v),
            Switch("   Wait for all strong monsters to be killed first", s.WaitForStrongBeforeSummon, v => s.WaitForStrongBeforeSummon = v),
            Switch("Use speed drugs", s.UseSpeedDrugs, v => s.UseSpeedDrugs = v),
            Switch("   Only use speed drugs in the script", s.SpeedDrugsOnlyInScript, v => s.SpeedDrugsOnlyInScript = v)), 0, 0);
        grid.Controls.Add(Box("Not in vSRO 1.188",
            AttackPage.Missing("Use Treasure Boxes", "event NPCs"),
            AttackPage.Missing("Use Easter Egg Event NPCs", "event NPCs"),
            AttackPage.Missing("Equip better items", "not built"),
            AttackPage.Missing("Summon flowers in the training area", "no flowers"),
            AttackPage.Missing("Use berserker regeneration potions", "not in vSRO"),
            AttackPage.Missing("Use Energy of Life potions", "not in vSRO"),
            AttackPage.Missing("Use Energy of Life berserk regeneration", "not in vSRO"),
            AttackPage.Missing("Pandora's Box", "not in vSRO"),
            SocialUi.Hint("The reverse return scroll takes you back to where you died, or to where you used the return scroll, instead of walking from town.", 46)), 1, 0);
        return grid;
    }

    private Control ScriptTab()
    {
        var s = Setup;
        var grid = Grid(55, 45);
        grid.Controls.Add(Box("Script",
            Switch("Skip town script entirely", s.SkipTownScript, v => s.SkipTownScript = v),
            Switch("Return when can't continue script", s.ReturnWhenCannotContinue, v => s.ReturnWhenCannotContinue = v),
            Number("Script walk delay (ms at every point, 0 = off)", s.ScriptWalkDelay, 0, 10000, v => s.ScriptWalkDelay = v),
            Number("Go back a coordinate if stuck after X seconds (0 = off)", s.GoBackIfStuck, 0, 600, v => s.GoBackIfStuck = v),
            Number("Return if stuck in script after X seconds (0 = off)", s.ReturnIfStuck, 0, 3600, v => s.ReturnIfStuck = v),
            AttackPage.Missing("Ride fellow pet to training area", "no fellow pets in vSRO 1.188"),
            AttackPage.Missing("Remount in caves", "not built"),
            AttackPage.Missing("Avoid Statue of Justice", "not in vSRO 1.188"),
            AttackPage.Missing("Use town NPC teleport for last recall / death", "use the reverse return scroll")), 0, 0);
        grid.Controls.Add(Box("How the bot runs",
            SocialUi.Hint("Start bot (Attack page): in town, the town loop runs first (storage, shops, the town script), then the area's walk script (or the map's routes) to the training area, then hunting. A return to town (Protection) runs the town loop again and walks back.", 100)), 1, 0);
        return grid;
    }
}
