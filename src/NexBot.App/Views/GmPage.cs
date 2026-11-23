using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Data;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// Game master tools, shown in the menu only while the character is a GM: spawn monsters by name, kill them, keep
/// monsters spawned next to the players (power leveling), move players, and a typed console.
/// </summary>
internal sealed class GmPage : UserControl
{
    private readonly Func<IGameData?> _data;
    private readonly Func<GameMasterConsole?> _console;
    private readonly SroInput _search = new() { Dock = DockStyle.Fill };
    private readonly ListBox _results = SocialUi.List();
    private readonly SroCombo _type = new() { Dock = DockStyle.Right, Width = 130 };
    private readonly Label _selected = Sro.Label("Select a monster on the left.", Muted, SroTheme.Small);
    private readonly Label _loopStatus = Sro.Label("Stopped.", Muted, SroTheme.Small);
    private readonly ListBox _output = SocialUi.List();
    private readonly SroInput _command = new() { Dock = DockStyle.Fill };
    private IReadOnlyList<(ModelInfo Model, byte Level)> _found = [];
    private int _count = 5, _keepAlive = 8, _killAfter;
    private double _radius = 50;
    private string _playerName = "";
    private GameMasterConsole? _subscribed;

    public GmPage(Func<IGameData?> data, Func<GameMasterConsole?> console)
    {
        _data = data;
        _console = console;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        var grid = SocialUi.Columns(3);
        grid.Controls.Add(SpawnPanel(), 0, 0);
        grid.Controls.Add(LevelingPanel(), 1, 0);
        grid.Controls.Add(ConsolePanel(), 2, 0);
        Controls.Add(grid);
        Say("Type help for the commands. GM characters only.");
    }

    private ModelInfo? Selected => _results.SelectedIndex >= 0 && _results.SelectedIndex < _found.Count ? _found[_results.SelectedIndex].Model : null;

    private byte SpawnType => GameMasterConsole.SpawnTypes[Math.Max(0, _type.SelectedIndex)].Value;

    // ---------------------------------------------------------------- spawn

    private SroPanel SpawnPanel()
    {
        var panel = SocialUi.Panel("Spawn monsters");
        var searchRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        searchRow.Controls.Add(_search);
        var searchButton = SocialUi.Button("Search", 80, Search);
        searchButton.Dock = DockStyle.Right;
        searchRow.Controls.Add(searchButton);
        _search.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Search();
            }
        };
        _results.SelectedIndexChanged += (_, _) => _selected.Text = Selected is { } m ? $"Selected: {m.Name} ({m.ServerName})" : "Select a monster on the left.";

        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(_results);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 176, BackColor = Color.Transparent };
        _type.SetItems(GameMasterConsole.SpawnTypes.Select(t => t.Name).ToArray());
        _type.SelectedIndex = 0;
        var typeRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        var typeLabel = Sro.Label("Type", SroTheme.Text, SroTheme.Small);
        typeLabel.AutoSize = false;
        typeLabel.Dock = DockStyle.Fill;
        typeLabel.TextAlign = ContentAlignment.MiddleLeft;
        typeRow.Controls.Add(typeLabel);
        typeRow.Controls.Add(_type);
        SocialUi.Stack(bottom,
            SocialUi.Field("How many", _count.ToString(CultureInfo.CurrentCulture), 70, v => _count = SocialUi.Int(v, 1, 50), () => { }),
            typeRow,
            SocialUi.Buttons(SocialUi.Button("Spawn", 90, SpawnSelected), SocialUi.Button("Zoe", 70, () => Run("zoe"))),
            SocialUi.Field("Kill radius", "50", 70, v => _radius = SocialUi.Int(v, 5, 500), () => { }),
            SocialUi.Buttons(SocialUi.Button("Kill all near", 120, () => Run($"killmob {_radius.ToString(CultureInfo.InvariantCulture)}"))));

        panel.Controls.Add(well);
        panel.Controls.Add(searchRow);
        panel.Controls.Add(SocialUi.Hint("Search by name or code (tiger, MOB_CH_TIGER). Lowest level first.", 34));
        panel.Controls.Add(bottom);
        return panel;
    }

    private void Search()
    {
        if (_data() is not { } data)
        {
            Say("The game database is not available.");
            return;
        }
        _found = data.SearchMonsters(_search.Text, 300);
        SocialUi.Fill(_results, _found.Select(m => $"Lv {m.Level,3}  {m.Model.Name}  ·  {m.Model.ServerName}").ToList());
        if (_found.Count > 0)
            _results.SelectedIndex = 0;
        else
            Say($"No monster matches \"{_search.Text}\".");
    }

    private void SpawnSelected()
    {
        if (Selected is not { } mob)
        {
            Say("Select a monster first.");
            return;
        }
        if (Ready() is not { } console)
            return;
        console.Spawn(mob, _count, SpawnType);
        Say($"Spawn {_count} x {mob.Name}.");
    }

    // ---------------------------------------------------------------- power leveling and players

    private SroPanel LevelingPanel()
    {
        var panel = SocialUi.Panel("Power leveling");
        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
        _selected.AutoSize = false;
        _selected.Dock = DockStyle.Top;
        _selected.Height = 22;
        _loopStatus.AutoSize = false;
        _loopStatus.Dock = DockStyle.Top;
        _loopStatus.Height = 36;
        SocialUi.Stack(body,
            SocialUi.Hint("Keeps the selected monster spawned next to you: when the players kill them, new ones are spawned. Monsters give EXP to the players who kill them.", 50),
            _selected,
            SocialUi.Field("Keep alive", _keepAlive.ToString(CultureInfo.CurrentCulture), 70, v => _keepAlive = SocialUi.Int(v, 1, 50), () => { }),
            SocialUi.Field("Within (radius)", "50", 70, v => _radius = SocialUi.Int(v, 5, 500), () => { }),
            SocialUi.Field("GM-kill after (seconds, 0 = never)", "0", 70, v => _killAfter = SocialUi.Int(v, 0, 3600), () => { }),
            SocialUi.Hint("A GM kill usually gives nobody EXP. Leave it at 0 when the players should get the EXP.", 34),
            SocialUi.Buttons(SocialUi.Button("Start", 90, StartLoop), SocialUi.Button("Stop", 90, () => _console()?.StopLoop())),
            _loopStatus,
            SocialUi.Caption("PLAYERS"),
            SocialUi.Field("Character name", "", 150, v => _playerName = v, () => { }),
            SocialUi.Buttons(
                SocialUi.Button("Recall", 80, () => Run("recall " + _playerName)),
                SocialUi.Button("Go to", 80, () => Run("moveto " + _playerName)),
                SocialUi.Button("To town", 80, () => Run("totown " + _playerName))),
            SocialUi.Caption("GAME MASTER"),
            SocialUi.Buttons(
                SocialUi.Button("Go town", 80, () => Run("gotown")),
                SocialUi.Button("Invisible", 80, () => Run("invisible")),
                SocialUi.Button("Invincible", 90, () => Run("invincible"))));
        panel.Controls.Add(body);
        return panel;
    }

    private void StartLoop()
    {
        if (Selected is not { } mob)
        {
            Say("Select a monster first.");
            return;
        }
        if (Ready() is not { } console)
            return;
        console.StartLoop(mob, SpawnType, _keepAlive, _radius, _killAfter);
        Say($"Power leveling with {mob.Name}: keep {_keepAlive} alive.");
    }

    // ---------------------------------------------------------------- console

    private SroPanel ConsolePanel()
    {
        var panel = SocialUi.Panel("Console");
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        _output.HorizontalScrollbar = true;
        well.Controls.Add(_output);
        var inputRow = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 0) };
        inputRow.Controls.Add(_command);
        var send = SocialUi.Button("Send", 70, SendCommand);
        send.Dock = DockStyle.Right;
        inputRow.Controls.Add(send);
        _command.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendCommand();
            }
        };
        panel.Controls.Add(well);
        panel.Controls.Add(inputRow);
        return panel;
    }

    private void SendCommand()
    {
        string line = _command.Text.Trim();
        if (line.Length == 0)
            return;
        _command.Text = "";
        Run(line);
    }

    private void Run(string line)
    {
        Say("> " + line);
        if (line.Trim().TrimStart('/').Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var l in GameMasterConsole.Help.Split('\n'))
                Say(l);
            return;
        }
        if (Ready() is not { } console)
            return;
        foreach (var l in console.Execute(line).Split('\n'))
            Say(l);
    }

    private GameMasterConsole? Ready()
    {
        var console = _console();
        if (console is null)
            Say("Start the bot and enter the game first.");
        else if (!console.IsGameMaster)
            Say("This character is not a game master.");
        else
        {
            Watch(console);
            return console;
        }
        return null;
    }

    private void Watch(GameMasterConsole console)
    {
        if (ReferenceEquals(console, _subscribed))
            return;
        _subscribed = console;
        console.LoopChanged += () =>
        {
            if (IsHandleCreated)
                BeginInvoke(() => _loopStatus.Text = console.LoopStatus);
        };
    }

    private void Say(string text)
    {
        _output.Items.Add(text);
        while (_output.Items.Count > 500)
            _output.Items.RemoveAt(0);
        _output.TopIndex = Math.Max(0, _output.Items.Count - 1);
    }
}
