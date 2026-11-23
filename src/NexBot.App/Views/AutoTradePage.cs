using System.Drawing.Drawing2D;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// Auto trade, modelled on phBot's Trade tab (for traders only):
/// <list type="bullet">
/// <item>top: the trade transport and trader suit to use, and the transport's state and cargo;</item>
/// <item>bottom: the trade loops (scripts recorded on the Script page, run in order), the goods list (what to buy and
/// how many), and the options (repeat, return scroll, stay on / off / remount the transport, attack thieves).</item>
/// </list>
/// </summary>
internal sealed class AutoTradePage : UserControl
{
    private static readonly string[] s_buyModes = ["As recorded in the script", "Fill the transport", "Goods list quantities"];
    private static readonly string[] s_mountModes = ["Stay on transport", "Stay off transport", "Remount transport"];

    private readonly Func<SroClient?> _game;
    private readonly TradeOptionList _transports;
    private readonly TradeOptionList _suits;
    private readonly SroSwitch _wearSuit = new() { Text = "Wear the trader suit before riding the transport", Dock = DockStyle.Top, Height = 28 };
    private readonly Label _jobState = Sro.Value();
    private readonly Label _transportState = Sro.Value();
    private readonly Label _cargoState = Sro.Value();
    private readonly Label _goldState = Sro.Value();
    private readonly CargoList _cargo;
    private readonly Label _message = Sro.Label("", Muted, SroTheme.Small);

    // loops / goods / options
    private readonly ListBox _loops = new()
    {
        Dock = DockStyle.Fill, BackColor = InputBack, ForeColor = SroTheme.Text, BorderStyle = BorderStyle.None,
        Font = SroTheme.Body, IntegralHeight = false,
    };
    private readonly SroCombo _trader = new() { Dock = DockStyle.Top };
    private readonly SroCombo _buyMode = new() { Dock = DockStyle.Top };
    private readonly Panel _goodsRows = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
    private readonly SroSwitch _repeat = new() { Text = "Repeat trade loops", Dock = DockStyle.Top, Height = 28 };
    private readonly SroInput _loopCount = new() { Width = 70 };
    private readonly SroSwitch _return = new() { Text = "Use return scroll after the loops complete", Dock = DockStyle.Top, Height = 28 };
    private readonly SroCombo _mountMode = new() { Dock = DockStyle.Top };
    private readonly SroSwitch _attack = new() { Text = "Attack spawned thieves", Dock = DockStyle.Top, Height = 28 };
    private readonly SroInput _radius = new() { Width = 70 };
    private readonly SroButton _start = new() { Text = "Start trade", Width = 110, Height = 30, Margin = new Padding(0, 0, 6, 0) };
    private readonly SroButton _stop = new() { Text = "Stop", Width = 80, Height = 30, Margin = Padding.Empty };
    private readonly Label _runState = Sro.Label("Stopped.", Muted, SroTheme.Small);

    private IReadOnlyList<ModelInfoView> _traders = [];
    private TradeSettings _settings = new();
    private Action? _changed;
    private TradeRunner? _runner;
    private bool _busy;
    private bool _binding;

    private sealed record ModelInfoView(string ServerName, string Name);

    public AutoTradePage(Func<SroClient?> game, IconCache icons)
    {
        _game = game;
        BackColor = Bg;
        Dock = DockStyle.Fill;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Bg, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        // Transport ------------------------------------------------------------------------------
        var transportPanel = new SroPanel { Title = "Trade transport", Dock = DockStyle.Fill, Margin = new Padding(5) };
        _transports = new TradeOptionList(icons) { Dock = DockStyle.Top };
        _transports.Picked += option =>
        {
            _settings.Transport = Same(_settings.Transport, option) ? "" : option.Info.ServerName;
            Changed();
        };
        transportPanel.Controls.Add(Scroller(_transports));
        transportPanel.Controls.Add(Hint("Transports that carry trade goods. Click one to use it for RIDE,TRANSPORT; click again for any you have.", 36));
        grid.Controls.Add(transportPanel, 0, 0);

        // Trader suit ----------------------------------------------------------------------------
        var jobPanel = new SroPanel { Title = "Trader suit", Dock = DockStyle.Fill, Margin = new Padding(5) };
        _suits = new TradeOptionList(icons) { Dock = DockStyle.Top };
        _suits.Picked += option =>
        {
            _settings.JobSuit = Same(_settings.JobSuit, option) ? "" : option.Info.ServerName;
            Changed();
        };
        _wearSuit.CheckedChanged += (_, _) => Edit(() => _settings.WearJobSuit = _wearSuit.Checked);
        jobPanel.Controls.Add(Scroller(_suits));
        jobPanel.Controls.Add(_wearSuit);
        jobPanel.Controls.Add(Hint("Trader flags and bags this character can wear. Click one to prefer it; otherwise any trader suit in the bag.", 36));
        grid.Controls.Add(jobPanel, 1, 0);

        // Status and actions ---------------------------------------------------------------------
        var tradePanel = new SroPanel { Title = "Transport", Dock = DockStyle.Fill, Margin = new Padding(5) };
        var facts = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (name, value) in new[] { ("Trader suit", _jobState), ("Transport", _transportState), ("Cargo", _cargoState), ("Gold", _goldState) })
        {
            var caption = Sro.Label(name, Muted, SroTheme.Small);
            caption.Margin = new Padding(0, 4, 0, 4);
            value.AutoSize = false;
            value.AutoEllipsis = true;
            value.Dock = DockStyle.Fill;
            value.Margin = new Padding(0, 3, 0, 3);
            facts.Controls.Add(caption);
            facts.Controls.Add(value);
        }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        var wear = new SroButton { Text = "Wear suit", Width = 92, Height = 28, Margin = new Padding(0, 0, 6, 0) };
        var ride = new SroButton { Text = "Ride transport", Width = 116, Height = 28, Margin = new Padding(0, 0, 6, 0) };
        var off = new SroButton { Text = "Get off", Width = 80, Height = 28, Margin = Padding.Empty };
        wear.Click += (_, _) => _ = RunAsync(WearAsync);
        ride.Click += (_, _) => _ = RunAsync(RideAsync);
        off.Click += (_, _) => _ = RunAsync((client, token) => client.DismountAsync(token));
        actions.Controls.AddRange([wear, ride, off]);
        _message.AutoSize = false;
        _message.Dock = DockStyle.Top;
        _message.Height = 22;
        _cargo = new CargoList(icons) { Dock = DockStyle.Fill };
        var cargoCaption = Sro.Label("WHAT THE TRANSPORT CARRIES", GoldDim, SroTheme.Tiny);
        cargoCaption.Dock = DockStyle.Top;
        cargoCaption.Padding = new Padding(0, 8, 0, 4);
        tradePanel.Controls.Add(_cargo);
        tradePanel.Controls.Add(cargoCaption);
        tradePanel.Controls.Add(_message);
        tradePanel.Controls.Add(actions);
        tradePanel.Controls.Add(facts);
        grid.Controls.Add(tradePanel, 2, 0);

        // Trade loops ----------------------------------------------------------------------------
        var loopPanel = new SroPanel { Title = "Trade loops", Dock = DockStyle.Fill, Margin = new Padding(5) };
        var loopButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        var add = new SroButton { Text = "Add…", Width = 70, Height = 28, Margin = new Padding(0, 0, 6, 0) };
        var remove = new SroButton { Text = "Remove", Width = 76, Height = 28, Margin = new Padding(0, 0, 6, 0) };
        var up = new SroButton { Text = "Up", Width = 48, Height = 28, Margin = new Padding(0, 0, 6, 0) };
        var down = new SroButton { Text = "Down", Width = 60, Height = 28, Margin = Padding.Empty };
        add.Click += (_, _) => AddLoops();
        remove.Click += (_, _) => MoveLoop(0);
        up.Click += (_, _) => MoveLoop(-1);
        down.Click += (_, _) => MoveLoop(+1);
        loopButtons.Controls.AddRange([add, remove, up, down]);
        var loopWell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        loopWell.Controls.Add(_loops);
        loopPanel.Controls.Add(loopWell);
        loopPanel.Controls.Add(loopButtons);
        loopPanel.Controls.Add(Hint("Each loop is a script from the Script page, from one town to the next: ride the transport, buy at the specialty trader, walk, sell. Example: Jangan → Donwhang, then Donwhang → Hotan. They run in this order.", 52));
        grid.Controls.Add(loopPanel, 0, 1);

        // Trade goods ----------------------------------------------------------------------------
        var goodsPanel = new SroPanel { Title = "Trade goods", Dock = DockStyle.Fill, Margin = new Padding(5) };
        _buyMode.SetItems(s_buyModes);
        _buyMode.SelectedIndexChanged += (_, _) => Edit(() => _settings.BuyMode = (TradeBuyMode)Math.Max(0, _buyMode.SelectedIndex));
        _trader.SelectedIndexChanged += (_, _) => BuildGoods();
        goodsPanel.Controls.Add(_goodsRows);
        goodsPanel.Controls.Add(Caption("GOODS OF THIS TRADER · TICK AND SET HOW MANY TO BUY"));
        goodsPanel.Controls.Add(_trader);
        goodsPanel.Controls.Add(Caption("TRADER"));
        goodsPanel.Controls.Add(_buyMode);
        goodsPanel.Controls.Add(Caption("HOW MUCH A BUY LINE BUYS"));
        grid.Controls.Add(goodsPanel, 1, 1);

        // Options --------------------------------------------------------------------------------
        var optionsPanel = new SroPanel { Title = "Trade options", Dock = DockStyle.Fill, Margin = new Padding(5) };
        _repeat.CheckedChanged += (_, _) => Edit(() => _settings.RepeatLoops = _repeat.Checked);
        _return.CheckedChanged += (_, _) => Edit(() => _settings.ReturnAfterLoops = _return.Checked);
        _attack.CheckedChanged += (_, _) => Edit(() => _settings.AttackThieves = _attack.Checked);
        _mountMode.SetItems(s_mountModes);
        _mountMode.SelectedIndexChanged += (_, _) => Edit(() => _settings.MountMode = (TradeMountMode)Math.Max(0, _mountMode.SelectedIndex));
        _loopCount.Box.TextChanged += (_, _) => Edit(() => _settings.LoopCount = Number(_loopCount, 0, 100000));
        _radius.Box.TextChanged += (_, _) => Edit(() => _settings.AttackRadius = Math.Max(5, Number(_radius, 5, 200)));
        _start.Click += (_, _) => StartTrade();
        _stop.Click += (_, _) => _runner?.Stop();
        var runButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        runButtons.Controls.AddRange([_start, _stop]);
        _runState.AutoSize = false;
        _runState.Dock = DockStyle.Bottom;
        _runState.Height = 36;
        // Docked controls added last sit at the edge: bottom row first, then the top list from its end.
        optionsPanel.Controls.Add(_runState);
        optionsPanel.Controls.Add(runButtons);
        optionsPanel.Controls.Add(Hint("Thieves are only fought when not staying on the transport (a rider cannot attack). Stay on transport needs enough recovery kits.", 36));
        optionsPanel.Controls.Add(Row("Attack radius (m)", _radius));
        optionsPanel.Controls.Add(_attack);
        optionsPanel.Controls.Add(_mountMode);
        optionsPanel.Controls.Add(Caption("ON THE ROAD"));
        optionsPanel.Controls.Add(_return);
        optionsPanel.Controls.Add(Row("Loop count (0 = until stopped)", _loopCount));
        optionsPanel.Controls.Add(_repeat);
        grid.Controls.Add(optionsPanel, 2, 1);

        Controls.Add(grid);
        RefreshView();
    }

    public ILogger Logger { get; set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private static bool Same(string serverName, TradeOption option) =>
        string.Equals(serverName, option.Info.ServerName, StringComparison.OrdinalIgnoreCase);

    private static int Number(SroInput input, int min, int max) =>
        int.TryParse(input.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) ? Math.Clamp(n, min, max) : min;

    private static Panel Scroller(Control list)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
        scroll.Controls.Add(list);
        scroll.Resize += (_, _) => list.Width = scroll.ClientSize.Width;
        return scroll;
    }

    private static Label Hint(string text, int height)
    {
        var hint = Sro.Label(text, Muted, SroTheme.Small);
        hint.AutoSize = false;
        hint.Dock = DockStyle.Top;
        hint.Height = height;
        return hint;
    }

    private static Label Caption(string text)
    {
        var caption = Sro.Label(text, GoldDim, SroTheme.Tiny);
        caption.AutoSize = false;
        caption.Dock = DockStyle.Top;
        caption.Height = 20;
        caption.TextAlign = ContentAlignment.BottomLeft;
        return caption;
    }

    /// <summary>A caption with a small input on its right.</summary>
    private static Panel Row(string text, SroInput input)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        var label = Sro.Label(text, SroTheme.Text, SroTheme.Small);
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        input.Dock = DockStyle.Right;
        input.Margin = Padding.Empty;
        row.Controls.Add(label);
        row.Controls.Add(input);
        return row;
    }

    /// <summary>Uses (and edits) these settings; <paramref name="changed"/> runs after every change (to save them).</summary>
    public void Bind(TradeSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        _binding = true;
        _wearSuit.Checked = settings.WearJobSuit;
        _repeat.Checked = settings.RepeatLoops;
        _loopCount.Text = settings.LoopCount.ToString(CultureInfo.CurrentCulture);
        _return.Checked = settings.ReturnAfterLoops;
        _attack.Checked = settings.AttackThieves;
        _radius.Text = settings.AttackRadius.ToString(CultureInfo.CurrentCulture);
        _buyMode.SelectedIndex = (int)settings.BuyMode;
        _mountMode.SelectedIndex = (int)settings.MountMode;
        ShowLoops();
        _binding = false;
        RefreshView();
    }

    private void Edit(Action change)
    {
        if (_binding)
            return;
        change();
        _changed?.Invoke();
    }

    private void Changed()
    {
        _changed?.Invoke();
        RefreshView();
    }

    // ---- loops ----------------------------------------------------------------------------------

    private void ShowLoops()
    {
        int selected = _loops.SelectedIndex;
        _loops.Items.Clear();
        for (int i = 0; i < _settings.Loops.Count; i++)
        {
            string path = _settings.Loops[i];
            _loops.Items.Add($"{i + 1}.  {Path.GetFileNameWithoutExtension(path)}{(File.Exists(path) ? "" : "   (missing)")}");
        }
        if (_loops.Items.Count > 0)
            _loops.SelectedIndex = Math.Clamp(selected, 0, _loops.Items.Count - 1);
    }

    private void AddLoops()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Scripts");
        Directory.CreateDirectory(folder);
        using var dialog = new OpenFileDialog
        {
            Title = "Add trade loop scripts", InitialDirectory = folder, Multiselect = true,
            Filter = "Walk scripts (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        _settings.Loops.AddRange(dialog.FileNames);
        ShowLoops();
        _loops.SelectedIndex = _loops.Items.Count - 1;
        _changed?.Invoke();
    }

    /// <summary>0 removes the selected loop, -1 / +1 moves it up / down.</summary>
    private void MoveLoop(int by)
    {
        int i = _loops.SelectedIndex;
        if (i < 0 || i >= _settings.Loops.Count)
            return;
        if (by == 0)
            _settings.Loops.RemoveAt(i);
        else
        {
            int j = i + by;
            if (j < 0 || j >= _settings.Loops.Count)
                return;
            (_settings.Loops[i], _settings.Loops[j]) = (_settings.Loops[j], _settings.Loops[i]);
            _loops.SelectedIndex = j;
        }
        ShowLoops();
        _changed?.Invoke();
    }

    // ---- goods ----------------------------------------------------------------------------------

    private void ShowTraders(SroClient client)
    {
        if (_traders.Count > 0)
            return;
        _traders = [.. client.Data.GetSpecialtyTraders().Select(m => new ModelInfoView(m.ServerName, m.Name))];
        _trader.SetItems(_traders.Select(t => $"{t.Name}  ·  {t.ServerName}"));
        if (_traders.Count > 0)
            _trader.SelectedIndex = 0;
    }

    /// <summary>One row per good of the chosen trader: tick to buy it, and how many.</summary>
    private void BuildGoods()
    {
        _goodsRows.SuspendLayout();
        foreach (Control old in _goodsRows.Controls.Cast<Control>().ToList())
            old.Dispose();
        _goodsRows.Controls.Clear();
        if (_game() is { } client && _trader.SelectedIndex >= 0 && _trader.SelectedIndex < _traders.Count)
        {
            var rows = new List<Control>();
            foreach (var shop in client.Data.GetShopItems(_traders[_trader.SelectedIndex].ServerName))
            {
                var info = client.Data.GetItem(shop.ItemServerName);
                var good = _settings.Goods.FirstOrDefault(g => string.Equals(g.Item, shop.ItemServerName, StringComparison.OrdinalIgnoreCase));
                var row = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.Transparent };
                var tick = new SroSwitch { Text = info?.Name ?? shop.ItemServerName, Dock = DockStyle.Fill, Checked = good?.Enabled ?? false };
                var amount = new SroInput { Dock = DockStyle.Right, Width = 70, Text = (good?.Quantity ?? info?.Stack ?? 0).ToString(CultureInfo.CurrentCulture) };
                string item = shop.ItemServerName;
                tick.CheckedChanged += (_, _) =>
                {
                    var g = _settings.Good(item);
                    g.Enabled = tick.Checked;
                    if (g.Quantity <= 0)
                        g.Quantity = Number(amount, 0, 100000);
                    _changed?.Invoke();
                };
                amount.Box.TextChanged += (_, _) =>
                {
                    _settings.Good(item).Quantity = Number(amount, 0, 100000);
                    _changed?.Invoke();
                };
                row.Controls.Add(tick);
                row.Controls.Add(amount);
                rows.Add(row);
            }
            // Dock top stacks the last added first: add in reverse to keep the shop order.
            for (int i = rows.Count - 1; i >= 0; i--)
                _goodsRows.Controls.Add(rows[i]);
        }
        _goodsRows.ResumeLayout(true);
    }

    // ---- run ------------------------------------------------------------------------------------

    private void StartTrade()
    {
        if (_game() is not { } client)
        {
            ShowRun("Start the bot and enter the game first.", Warn);
            return;
        }
        _runner?.Dispose();
        _runner = new TradeRunner(client, _settings, Logger);
        var runner = _runner;
        runner.StatusChanged += (_, _) =>
        {
            if (IsHandleCreated)
                BeginInvoke(() => ShowRun(runner.Status, runner.IsRunning ? Good : Muted));
        };
        if (runner.Start() is { } error)
            ShowRun(error, Warn);
    }

    /// <summary>Stops auto trade (the bot is stopping).</summary>
    public void StopAll() => _runner?.Stop();

    /// <summary>True while a trade run is going.</summary>
    public bool IsRunning => _runner?.IsRunning == true;

    private void ShowRun(string text, Color color)
    {
        _runState.Text = text;
        _runState.ForeColor = color;
        bool running = _runner?.IsRunning == true;
        _start.Enabled = !running;
        _stop.Enabled = running;
    }

    /// <summary>Reads the character, the bag and the transport again.</summary>
    public void RefreshView()
    {
        var client = _game();
        var character = client?.World.Character;
        if (client is null)
        {
            _transports.SetItems([], "", "Start the bot and enter the game to see the transports.");
            _suits.SetItems([], "", "Start the bot and enter the game to see the trader suits.");
        }
        else
        {
            _transports.SetItems(TradeCatalog.Transports(client.Data, character), _settings.Transport, "No trade transports in the game data.");
            _suits.SetItems(TradeCatalog.TraderSuits(client.Data, character), _settings.JobSuit, "No trader suits in the game data.");
            if (_traders.Count == 0)
            {
                ShowTraders(client);
                BuildGoods();
            }
        }
        bool running = _runner?.IsRunning == true;
        _start.Enabled = !running;
        _stop.Enabled = running;

        if (character is null)
        {
            foreach (var label in new[] { _jobState, _transportState, _cargoState, _goldState })
                label.Text = "-";
            _cargo.SetItems([]);
            return;
        }
        _jobState.Text = character.Inventory[8] is Equipable { IsJob: true } suit ? suit.Name : "None worn";
        _jobState.ForeColor = character.Inventory[8] is null ? Muted : SroTheme.Value;
        var transport = client!.Transport;
        _transportState.Text = transport is null ? "Not summoned"
            : string.Create(CultureInfo.CurrentCulture, $"{transport.Name}{(character.RidingUniqueId == transport.UniqueId ? " · riding" : "")} · HP {transport.Hp:N0} / {transport.HpMax:N0}");
        _transportState.ForeColor = transport is null ? Muted : SroTheme.Value;
        var goods = transport?.Inventory;
        int used = goods?.Count ?? 0;
        _cargoState.Text = transport is null ? "-" : string.Create(CultureInfo.CurrentCulture, $"{used} / {goods!.Capacity} slots");
        _goldState.Text = character.Gold.ToString("N0", CultureInfo.CurrentCulture);
        _cargo.SetItems(goods is null ? [] : [.. goods.Where(i => i is not null).Select(i => i!)]);
    }

    private async Task RunAsync(Func<SroClient, CancellationToken, Task<ItemMoveResult>> action)
    {
        if (_busy)
            return;
        if (_game() is not { } client || client.World.Character is null)
        {
            Show("Not in game.", false);
            return;
        }
        _busy = true;
        try
        {
            var result = await action(client, CancellationToken.None);
            Show(result.Message, result.Ok);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            Show(ex.Message, false);
        }
        finally
        {
            _busy = false;
            RefreshView();
        }
    }

    private async Task<ItemMoveResult> WearAsync(SroClient client, CancellationToken token)
    {
        int slot = TradeCatalog.FindTraderSuit(client.World.Character!, _settings.JobSuit);
        return slot < 0 ? new(false, "No trader suit this character can wear is in the bag.") : await client.WearJobSuitAsync(slot, token);
    }

    private async Task<ItemMoveResult> RideAsync(SroClient client, CancellationToken token)
    {
        var character = client.World.Character!;
        if (_settings.WearJobSuit && character.Inventory[8] is null)
        {
            var worn = await WearAsync(client, token);
            if (!worn.Ok)
                Show(worn.Message, false);
        }
        if (client.Mount is not null || character.IsRiding)
            return await client.RideMountAsync(-1, token);
        int slot = TradeCatalog.FindTransportScroll(character, _settings.Transport);
        return slot < 0 ? new(false, "No trade transport scroll in the bag.") : await client.RideMountAsync(slot, token);
    }

    private void Show(string text, bool ok)
    {
        _message.Text = text;
        _message.ForeColor = ok ? Good : Bad;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _runner?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Transports or trader suits to pick from: icon, name, level, how many are in the bag; the preferred one framed in gold.</summary>
internal sealed class TradeOptionList : Control
{
    private const int Row = 46, IconSize = 36;
    private readonly IconCache _icons;
    private IReadOnlyList<TradeOption> _items = [];
    private string _selected = "";
    private IReadOnlyList<string>? _ranked;
    private string _empty = "";
    private string _signature = "";
    private int _hover = -1;

    /// <summary>The word before the rank in a ranked list ("PET" shows PET 1, PET 2 ...).</summary>
    public string RankWord { get; set; } = "";

    /// <summary>Extra text after the bag count (pets: how many are dead).</summary>
    public Func<TradeOption, string?>? Extra { get; set; }

    public TradeOptionList(IconCache icons)
    {
        _icons = icons;
        _icons.Loaded += Invalidate;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Height = Row;
    }

    public event Action<TradeOption>? Picked;

    public void SetItems(IReadOnlyList<TradeOption> items, string selected, string empty)
    {
        string signature = selected + "|" + empty + "|" + string.Join(",", items.Select(i => $"{i.Info.Id}:{i.Owned}:{i.CanUse}"));
        if (signature == _signature)
            return;
        (_signature, _items, _selected, _empty) = (signature, items, selected, empty);
        _ranked = null;
        Height = Math.Max(Row, items.Count * Row);
        Invalidate();
    }

    /// <summary>A list where several items are picked in an order (<paramref name="ranked"/>: server names, first = 1).</summary>
    public void SetRanked(IReadOnlyList<TradeOption> items, IReadOnlyList<string> ranked, string empty)
    {
        string signature = "R|" + string.Join(",", ranked) + "|" + empty + "|"
            + string.Join(",", items.Select(i => $"{i.Info.Id}:{i.Owned}:{i.CanUse}:{Extra?.Invoke(i)}"));
        if (signature == _signature)
            return;
        (_signature, _items, _selected, _empty, _ranked) = (signature, items, "", empty, [.. ranked]);
        Height = Math.Max(Row, items.Count * Row);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, _empty, SroTheme.Small, new Rectangle(0, 4, Width, Row), Muted, TextFormatFlags.Left | TextFormatFlags.WordBreak);
            return;
        }
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        for (int i = 0; i < _items.Count; i++)
        {
            var option = _items[i];
            var row = new Rectangle(0, i * Row, Width - 1, Row - 4);
            if (!row.IntersectsWith(e.ClipRectangle))
                continue;
            int rank = _ranked is null ? -1 : _ranked.ToList().FindIndex(n => string.Equals(n, option.Info.ServerName, StringComparison.OrdinalIgnoreCase));
            bool picked = rank >= 0 || string.Equals(option.Info.ServerName, _selected, StringComparison.OrdinalIgnoreCase);
            using (var back = new SolidBrush(picked ? Hex("#1c2338") : i == _hover ? Hex("#1a1712") : Hex("#0e0d0a")))
                g.FillRectangle(back, row);
            StrokeRect(g, picked ? Gold : Hex("#2a261d"), row);

            var frame = new Rectangle(row.X + 4, row.Y + (row.Height - IconSize) / 2, IconSize, IconSize);
            using (var well = Vertical(frame, Hex("#070605"), Hex("#16140f")))
                g.FillRectangle(well, frame);
            if (_icons.Get(string.IsNullOrEmpty(option.Info.Icon) ? null : option.Info.Icon) is { } icon)
                g.DrawImage(icon, Rectangle.Inflate(frame, -1, -1));
            StrokeRect(g, option.Owned > 0 ? GoldDim : Hex("#3a352a"), frame);

            int x = frame.Right + 8, w = row.Right - x - (picked ? 78 : 6);
            var nameColor = !option.CanUse ? Muted : option.Owned > 0 ? SroTheme.Text : Hex("#c8bfa8");
            TextRenderer.DrawText(g, option.Info.Name, SroTheme.BodyBold, new Rectangle(x, row.Y + 4, w, 18), nameColor, TextFormatFlags.EndEllipsis);
            string detail = $"LV. {option.Info.Level}" + (option.Owned > 0 ? $"  ·  IN BAG ×{option.Owned}" : "  ·  NOT IN BAG") + (option.CanUse ? "" : "  ·  LEVEL TOO LOW")
                + (Extra?.Invoke(option) is { Length: > 0 } extra ? "  ·  " + extra : "");
            TextRenderer.DrawText(g, detail, SroTheme.Tiny, new Rectangle(x, row.Y + 23, w, 15),
                !option.CanUse ? Bad : option.Owned > 0 ? Good : Muted, TextFormatFlags.EndEllipsis);
            if (picked)
                TextRenderer.DrawText(g, rank >= 0 ? $"{RankWord} {rank + 1}" : "PREFERRED", SroTheme.Tiny, new Rectangle(row.Right - 76, row.Y, 72, row.Height), Gold,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hover = e.Y / Row < _items.Count ? e.Y / Row : -1;
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int index = e.Y / Row;
        if (e.Button == MouseButtons.Left && index >= 0 && index < _items.Count)
            Picked?.Invoke(_items[index]);
        base.OnMouseClick(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _icons.Loaded -= Invalidate;
        base.Dispose(disposing);
    }
}

/// <summary>The goods in the transport: icon, name, amount.</summary>
internal sealed class CargoList : Control
{
    private const int Row = 30;
    private readonly IconCache _icons;
    private IReadOnlyList<Item> _items = [];
    private string _signature = "";

    public CargoList(IconCache icons)
    {
        _icons = icons;
        _icons.Loaded += Invalidate;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public void SetItems(IReadOnlyList<Item> items)
    {
        string signature = string.Join(",", items.Select(i => $"{i.Id}:{i.Quantity}"));
        if (signature == _signature)
            return;
        (_signature, _items) = (signature, items);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, "Nothing.", SroTheme.Small, ClientRectangle, Muted, TextFormatFlags.Left | TextFormatFlags.Top);
            return;
        }
        // Same goods together: one row per kind with the total.
        int y = 0;
        foreach (var kind in _items.GroupBy(i => i.Id))
        {
            var first = kind.First();
            var frame = new Rectangle(0, y, 26, 26);
            if (_icons.Get(string.IsNullOrEmpty(first.Info.Icon) ? null : first.Info.Icon) is { } icon)
                g.DrawImage(icon, frame);
            StrokeRect(g, Hex("#3a352a"), frame);
            int total = kind.Sum(i => i.Quantity);
            TextRenderer.DrawText(g, first.Name, SroTheme.Body, new Rectangle(34, y + 4, Width - 110, 20), SroTheme.Text, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, string.Create(CultureInfo.CurrentCulture, $"×{total:N0}  ({kind.Count()} slot{(kind.Count() == 1 ? "" : "s")})"),
                SroTheme.Small, new Rectangle(Width - 120, y + 5, 118, 20), SroTheme.Value, TextFormatFlags.Right);
            y += Row;
            if (y > Height)
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _icons.Loaded -= Invalidate;
        base.Dispose(disposing);
    }
}
