using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Map;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>Alchemy (phBot's Alchemy tab): Plus, Attribute (white) and Alchemic (blue) jobs in one queue.</summary>
internal sealed class AlchemyPage : UserControl
{
    private readonly Func<SroClient?> _game;
    private readonly Func<AlchemyRunner?> _runner;
    private readonly FlowLayoutPanel _itemsGrid = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(2) };
    private readonly List<SlotView> _slots = [];
    private readonly Panel _itemButtons = new() { Dock = DockStyle.Bottom, Height = 40, BackColor = Color.Transparent };
    private readonly IconCache _icons;
    private byte? _selectedSlot;
    private readonly ListBox _queue = SocialUi.List();
    private readonly Label _itemInfo = Sro.Label("", Muted, SroTheme.Small);
    private readonly Label _status = Sro.Label("Idle.", Muted, SroTheme.Small);
    private readonly Label _layout = Sro.Label("", Muted, SroTheme.Small);
    private readonly Panel _settingsHost = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
    private readonly SroButton[] _tabs;
    private readonly SroCombo _stone = new() { Dock = DockStyle.Top };
    private AlchemySettings _settings = new();
    private Action _changed = () => { };
    private AlchemyJob _job = AlchemyJob.Plus;
    private IReadOnlyList<(byte Slot, Equipable Item)> _shownItems = [];
    private IReadOnlyList<string> _stones = [];
    private DateTime _sayUntil;

    public AlchemyPage(Func<SroClient?> game, Func<AlchemyRunner?> runner, IconCache icons)
    {
        _game = game;
        _runner = runner;
        _icons = icons;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        _tabs = [new SroButton { Text = "Plus" }, new SroButton { Text = "Attribute" }, new SroButton { Text = "Alchemic" }];
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Items: slots like the Inventory page's (rare glow, item card on hover); click one to choose it.
        var items = SocialUi.Panel("Items");
        var itemsWell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        itemsWell.Controls.Add(_itemsGrid);
        _itemInfo.AutoSize = false;
        _itemInfo.Dock = DockStyle.Bottom;
        _itemInfo.Height = 56;
        items.Controls.Add(itemsWell);
        items.Controls.Add(_itemInfo);
        items.Controls.Add(_itemButtons);
        grid.Controls.Add(items, 0, 0);

        // Settings with Plus / Attribute / Alchemic tabs
        var settings = SocialUi.Panel("Settings");
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, BackColor = Color.Transparent, WrapContents = false };
        for (int i = 0; i < _tabs.Length; i++)
        {
            var job = (AlchemyJob)i;
            _tabs[i].Width = 92;
            _tabs[i].Height = 28;
            _tabs[i].Margin = new Padding(0, 0, 6, 0);
            _tabs[i].Click += (_, _) => ShowJob(job);
            tabs.Controls.Add(_tabs[i]);
        }
        settings.Controls.Add(_settingsHost);
        settings.Controls.Add(tabs);
        grid.Controls.Add(settings, 1, 0);

        // Queue
        var queue = SocialUi.Panel("Alchemy queue");
        var queueWell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        queueWell.Controls.Add(_queue);
        var menu = new ContextMenuStrip { Renderer = new SroMenuRenderer(), ShowImageMargin = false, Font = SroTheme.Body };
        menu.Items.Add("Delete", null, (_, _) => DeleteSelected());
        menu.Items[0].ForeColor = SroTheme.Text;
        _queue.ContextMenuStrip = menu;
        _queue.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && _queue.IndexFromPoint(e.Location) is var i and >= 0)
                _queue.SelectedIndex = i;
        };
        _status.AutoSize = false;
        _status.Height = 40;
        _layout.AutoSize = false;
        _layout.Dock = DockStyle.Bottom;
        _layout.Height = 56;
        queue.Controls.Add(queueWell);
        queue.Controls.Add(_layout);
        SocialUi.Stack(queue,
            SocialUi.Buttons(SocialUi.Button("Start", 80, Start), SocialUi.Button("Cancel", 80, () => _runner()?.Stop()),
                SocialUi.Button("Clear", 70, () =>
                {
                    if (_runner() is { IsRunning: false } r)
                        r.Queue.Clear();
                })),
            _status,
            SocialUi.Caption("QUEUE  (RIGHT-CLICK A LINE TO DELETE IT)"));
        grid.Controls.Add(queue, 2, 0);
        Controls.Add(grid);
    }

    public void Bind(AlchemySettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        ShowJob(AlchemyJob.Plus);
    }

    private void Say(string text, bool ok)
    {
        _status.Text = text;
        _status.ForeColor = ok ? Good : Bad;
        _sayUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private Equipable? SelectedItem() =>
        _selectedSlot is { } slot && _shownItems.FirstOrDefault(x => x.Slot == slot) is { Item: { } item } ? item : null;

    private byte SelectedSlot() => _selectedSlot ?? 0;

    private void Choose(SlotView slot)
    {
        _selectedSlot = slot.Item is null ? null : (byte)slot.Slot;
        foreach (var s in _slots)
            s.Selected = s.Item is not null && s.Slot == _selectedSlot;
        ShowItem();
        if (_job != AlchemyJob.Plus)
            FillStones();
    }

    private SlotView NewSlot()
    {
        var slot = new SlotView(_icons) { Size = new Size(44, 44), Margin = new Padding(3), AllowDrop = false };
        slot.Hovered += s =>
        {
            if (s.Item is { } item)
                ItemPreview.Show(s, ItemCard.For(item, false, _game()?.Data.GetItem(item.ServerName)), _icons);
        };
        slot.Unhovered += _ => ItemPreview.Hide();
        slot.Click += (_, _) => Choose(slot);
        slot.Activated += Choose;
        return slot;
    }

    private Control Num(string text, Func<AlchemySettings, int> get, Action<AlchemySettings, int> set, int max) =>
        SocialUi.Field(text, get(_settings).ToString(CultureInfo.CurrentCulture), 70, v => set(_settings, SocialUi.Int(v, 0, max)), _changed);

    /// <summary>One phBot-style option: a check box, then "at +N" (and an optional extra control on the right).</summary>
    private Panel CheckRow(string text, bool on, Action<bool> setOn, string valueLabel, int value, Action<int> setValue, int max, Control? extra = null)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        var toggle = new SroSwitch { Text = text, Checked = on, Dock = DockStyle.Fill };
        var input = new SroInput { Dock = DockStyle.Right, Width = 52, Text = value.ToString(CultureInfo.CurrentCulture), Enabled = on };
        var label = Sro.Label(valueLabel, SroTheme.Text, SroTheme.Small);
        label.AutoSize = false;
        label.Dock = DockStyle.Right;
        label.Width = valueLabel.Length > 0 ? 34 : 0;
        label.TextAlign = ContentAlignment.MiddleRight;
        toggle.CheckedChanged += (_, _) =>
        {
            setOn(toggle.Checked);
            input.Enabled = toggle.Checked;
            if (extra is not null)
                extra.Enabled = toggle.Checked;
            _changed();
        };
        input.Box.TextChanged += (_, _) =>
        {
            setValue(SocialUi.Int(input.Text, 0, max));
            _changed();
        };
        row.Controls.Add(toggle);
        row.Controls.Add(label);
        row.Controls.Add(input);
        if (extra is not null)
        {
            extra.Dock = DockStyle.Right;
            extra.Enabled = on;
            row.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6, BackColor = Color.Transparent });
            row.Controls.Add(extra);
        }
        return row;
    }

    /// <summary>Auto (the item's degree) or a fixed degree 1-12.</summary>
    private SroCombo DegreeCombo(int value, Action<int> set)
    {
        var combo = new SroCombo { Width = 86 };
        combo.SetItems(["Auto", .. Enumerable.Range(1, 12).Select(d => $"Deg {d}")]);
        combo.SelectedIndex = Math.Clamp(value, 0, 12);
        combo.SelectedIndexChanged += (_, _) =>
        {
            set(Math.Max(0, combo.SelectedIndex));
            _changed();
        };
        return combo;
    }

    /// <summary>Item degree used for stones (phBot's "Auto (item degree)").</summary>
    private Panel DegreeRow()
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        var label = Sro.Label("Item degree (stones)", SroTheme.Text, SroTheme.Small);
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        var combo = DegreeCombo(_settings.DegreeOverride, v => _settings.DegreeOverride = v);
        combo.Dock = DockStyle.Right;
        row.Controls.Add(label);
        row.Controls.Add(combo);
        return row;
    }

    private void ShowJob(AlchemyJob job)
    {
        _job = job;
        for (int i = 0; i < _tabs.Length; i++)
        {
            _tabs[i].Style = i == (int)job ? SroButtonStyle.Gold : SroButtonStyle.Steel;
            _tabs[i].Invalidate();
        }
        _settingsHost.Controls.Clear();
        var s = _settings;
        switch (job)
        {
            case AlchemyJob.Plus:
                SocialUi.Stack(_settingsHost,
                    Num("Stop at +", x => x.StopAt, (x, v) => x.StopAt = Math.Max(1, v), 30),
                    Num("Success delay (ms)", x => x.SuccessDelayMs, (x, v) => x.SuccessDelayMs = v, 60000),
                    Num("Failure delay (ms)", x => x.FailureDelayMs, (x, v) => x.FailureDelayMs = v, 60000),
                    SocialUi.Caption("USE WHEN THE ITEM IS AT THIS PLUS OR HIGHER"),
                    CheckRow("Lucky powder", s.LuckyPowderOn, v => s.UseLuckyPowder = v, "at +", s.LuckyPowderAt, v => s.LuckyPowderAt = v, 30,
                        DegreeCombo(s.LuckyPowderDegree, v => s.LuckyPowderDegree = v)),
                    CheckRow("Astral stone", s.AstralOn, v => s.UseAstral = v, "at +", s.AstralAt, v => s.AstralAt = v, 30),
                    CheckRow("Steady stone", s.SteadyOn, v => s.UseSteady = v, "at +", s.SteadyAt, v => s.SteadyAt = v, 30),
                    CheckRow("Immortal stone", s.ImmortalOn, v => s.UseImmortal = v, "at +", s.ImmortalAt, v => s.ImmortalAt = v, 30),
                    CheckRow("Lucky stone", s.LuckyStoneOn, v => s.UseLuckyStone = v, "at +", s.LuckyStoneAt, v => s.LuckyStoneAt = v, 30),
                    CheckRow("Stop after attempts", s.StopAfterOn, v => s.UseStopAfterAttempts = v, "", s.StopAfterAttempts,
                        v => s.StopAfterAttempts = Math.Max(1, v), 10000),
                    DegreeRow(),
                    SocialUi.Switch("Move on after a plus failure", s.MoveOnAfterFailure, v => s.MoveOnAfterFailure = v, _changed),
                    SocialUi.Switch("Stop all alchemy if an item is destroyed", s.StopAllIfDestroyed, v => s.StopAllIfDestroyed = v, _changed));
                SetItemButtons(SocialUi.Button("Add", 70, AddPlus), SocialUi.Button("Add all items", 116, AddAllPlus));
                break;
            default:
                bool white = job == AlchemyJob.Attribute;
                FillStones();
                SocialUi.Stack(_settingsHost,
                    SocialUi.Caption(white ? "ATTRIBUTE STONE (FOR THE SELECTED ITEM)" : "ALCHEMIC STONE (FOR THE SELECTED ITEM)"),
                    _stone,
                    white
                        ? Num("Stop at white %", x => x.AttributeStopPercent, (x, v) => x.AttributeStopPercent = Math.Clamp(v, 1, 100), 100)
                        : Num("Stop at blue value (not %)", x => x.AlchemicStopValue, (x, v) => x.AlchemicStopValue = Math.Max(1, v), 10000),
                    CheckRow("Stop after attempts", s.StopAfterOn, v => s.UseStopAfterAttempts = v, "", s.StopAfterAttempts,
                        v => s.StopAfterAttempts = Math.Max(1, v), 10000),
                    SocialUi.Switch("Use alchemy catalyst (stones cannot fail)", s.UseCatalyst, v => s.UseCatalyst = v, _changed),
                    SocialUi.Hint(white
                        ? "White % is read for weapon, armor, shield and accessory stats the bot knows; for other stones it stops after one success."
                        : "Blue value is the option's number (e.g. STR 5), not a percent.", 52));
                SetItemButtons(
                    SocialUi.Button("Add", 56, AddStone),
                    SocialUi.Button(white ? "Add all stones" : "Add all blues", 104, AddAllStones),
                    SocialUi.Button("Add all items", 104, AddStoneToAllItems));
                break;
        }
    }

    /// <summary>The job's Add buttons, under the items.</summary>
    private void SetItemButtons(params SroButton[] buttons)
    {
        _itemButtons.Controls.Clear();
        _itemButtons.Controls.Add(SocialUi.Buttons(buttons));
    }

    private void FillStones()
    {
        var client = _game();
        var item = SelectedItem();
        _stones = client is null || item is null || _runner() is not { } runner ? [] : runner.StonesFor(item, _job);
        _stone.SetItems(_stones.Count == 0 ? ["(no fitting stone in the inventory)"] : _stones.Select(s => client!.Data.GetItem(s)?.Name is { Length: > 0 } n ? $"{n}  ({s})" : s));
        _stone.SelectedIndex = 0;
    }

    private string? SelectedStone() => _stone.SelectedIndex >= 0 && _stone.SelectedIndex < _stones.Count ? _stones[_stone.SelectedIndex] : null;

    private bool Queue(AlchemyEntry entry)
    {
        if (_runner() is not { } runner)
        {
            Say("Start the bot and enter the game first.", false);
            return false;
        }
        if (runner.Queue.Any(q => q.Job == entry.Job && q.ItemId == entry.ItemId && q.Slot == entry.Slot && q.Stone == entry.Stone))
            return false;
        runner.Queue.Add(entry);
        return true;
    }

    private void AddPlus()
    {
        if (SelectedItem() is not { } item)
        {
            Say("Select an item first.", false);
            return;
        }
        Queue(new AlchemyEntry(AlchemyJob.Plus, SelectedSlot(), item.Id, item.Name, _settings.StopAt));
    }

    private void AddAllPlus()
    {
        if (_runner() is not { } runner)
            return;
        int added = runner.AllPlusItems().Count(Queue);
        Say($"{added} items added.", added > 0);
    }

    private int StopFor(AlchemyJob job) => job == AlchemyJob.Attribute ? _settings.AttributeStopPercent : _settings.AlchemicStopValue;

    private void AddStone()
    {
        if (SelectedItem() is not { } item || SelectedStone() is not { } stone)
        {
            Say("Select an item and a stone first.", false);
            return;
        }
        Queue(new AlchemyEntry(_job, SelectedSlot(), item.Id, item.Name, StopFor(_job), stone));
    }

    private void AddAllStones()
    {
        if (SelectedItem() is not { } item)
            return;
        byte slot = SelectedSlot();
        int added = _stones.Count(s => Queue(new AlchemyEntry(_job, slot, item.Id, item.Name, StopFor(_job), s)));
        Say($"{added} stones added for {item.Name}.", added > 0);
    }

    private void AddStoneToAllItems()
    {
        if (SelectedStone() is not { } stone || _runner() is not { } runner)
            return;
        int degree = AlchemyCatalog.MaterialDegree(stone);
        int added = _shownItems.Count(x => (degree == 0 || AlchemyCatalog.DegreeOf(x.Item.ServerName) == degree || _settings.DegreeOverride == degree)
                                           && runner.StonesFor(x.Item, _job).Contains(stone)
                                           && Queue(new AlchemyEntry(_job, x.Slot, x.Item.Id, x.Item.Name, StopFor(_job), stone)));
        Say($"{stone} added for {added} items.", added > 0);
    }

    private void DeleteSelected()
    {
        if (_runner() is not { } runner || _queue.SelectedIndex < 0 || _queue.SelectedIndex >= runner.Queue.Count)
            return;
        if (runner.IsRunning && _queue.SelectedIndex == 0)
        {
            Say("Cancel first to delete the running line.", false);
            return;
        }
        runner.Queue.RemoveAt(_queue.SelectedIndex);
    }

    private void Start()
    {
        if (_runner() is not { } runner)
        {
            Say("Start the bot and enter the game first.", false);
            return;
        }
        if (runner.Queue.Count == 0)
        {
            Say("Add items to the queue first.", false);
            return;
        }
        runner.Start(message => BeginInvoke(() => Say(message, message == "Queue finished.")));
    }

    private void ShowItem()
    {
        if (SelectedItem() is not { } item)
        {
            _itemInfo.Text = _shownItems.Count == 0 ? "" : "Click an item to choose it; point at it for its details.";
            _itemInfo.ForeColor = Muted;
            return;
        }
        int degree = _settings.DegreeOverride > 0 ? _settings.DegreeOverride : AlchemyCatalog.DegreeOf(item.ServerName);
        string blues = item.MagicOptions.Count == 0 ? "no blues" : string.Join(", ", item.MagicOptions.Select(o => $"{(o.Name.Length > 0 ? o.Name : o.ServerName)} {o.Value}"));
        _itemInfo.Text = $"{item.Name}  +{item.Plus}  ·  degree {degree}\n{blues}";
        _itemInfo.ForeColor = SroTheme.Text;
    }

    /// <summary>One slot per item, kept between refreshes (a slot only redraws when its item changed).</summary>
    private void ShowSlots(List<(byte Slot, Equipable Item)> list)
    {
        if (_slots.Count != list.Count)
        {
            _itemsGrid.SuspendLayout();
            while (_slots.Count < list.Count)
            {
                var slot = NewSlot();
                _slots.Add(slot);
                _itemsGrid.Controls.Add(slot);
            }
            while (_slots.Count > list.Count)
            {
                var slot = _slots[^1];
                _slots.RemoveAt(_slots.Count - 1);
                _itemsGrid.Controls.Remove(slot);
                slot.Dispose();
            }
            _itemsGrid.ResumeLayout();
        }
        for (int i = 0; i < list.Count; i++)
        {
            _slots[i].Slot = list[i].Slot;
            _slots[i].SetItem(CharacterSheet.ViewOf(list[i].Item, list[i].Slot));
            _slots[i].Selected = list[i].Slot == _selectedSlot;
        }
    }

    public void RefreshView()
    {
        var client = _game();
        var inventory = client?.World.Character?.Inventory;
        var list = new List<(byte, Equipable)>();
        if (inventory is not null)
            for (int i = Character.FirstInventorySlot; i < inventory.Capacity; i++)
                if (inventory[i] is Equipable e && AlchemyCatalog.CanAlchemy(e))
                    list.Add(((byte)i, e));
        _shownItems = list;
        if (_selectedSlot is { } chosen && !list.Any(x => x.Item1 == chosen))
            _selectedSlot = null;
        ShowSlots(list);
        var runner = _runner();
        SocialUi.Fill(_queue, runner is null ? [] : [.. runner.Queue.Select((q, i) => (runner.IsRunning && i == 0 ? "▶ " : "   ") + q)]);
        if (runner is not null && DateTime.UtcNow > _sayUntil)
        {
            _status.Text = runner.IsRunning ? runner.Status : runner.Queue.Count == 0 ? "Queue empty." : $"Idle · {runner.Queue.Count} in the queue.";
            _status.ForeColor = runner.IsRunning ? Good : Muted;
        }
        _layout.Text = string.Create(CultureInfo.InvariantCulture,
            $"Packet types: elixir {_settings.ElixirType} · stone {_settings.MagicStoneType} · attribute {_settings.AttributeStoneType}") +
            (_settings.LearnedFrom.Length > 0 ? "  (learned from the game client)" : "\nDo one elixir and one stone in the game client so the bot learns your server's types.");
        ShowItem();
    }
}
