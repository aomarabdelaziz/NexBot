using System.Globalization;
using Microsoft.Extensions.Logging;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Map;
using NexBot.Media;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// Walk scripts: record the character's way (points, teleports, return scrolls), edit the script text, see the path
/// on the map, save / open script files, and walk a script automatically.
/// </summary>
internal sealed class ScriptPage : UserControl
{
    private readonly Func<SroClient?> _game;
    private readonly MapCanvas _map = new() { Dock = DockStyle.Fill };
    private readonly TextBox _editor;
    private readonly SroButton _record = new() { Text = "● Record", Style = SroButtonStyle.Danger };
    private readonly SroButton _stopRecord = new() { Text = "■ Stop" };
    private readonly SroButton _addWait = new() { Text = "+ Wait 3 s" };
    private readonly SroButton _clear = new() { Text = "Clear" };
    private readonly SroButton _open = new() { Text = "Open…" };
    private readonly SroButton _save = new() { Text = "Save…", Style = SroButtonStyle.Gold };
    private readonly SroButton _walk = new() { Text = "▶ Start walking", Style = SroButtonStyle.Primary };
    private readonly SroButton _stopWalk = new() { Text = "■ Stop" };
    private readonly SroInput _distance = new() { Width = 60 };
    private readonly SroSwitch _loop = new() { Text = "Loop at the end", Checked = true };
    private readonly Label _recordState = Sro.Label("Not recording.", Muted, SroTheme.Small);
    private readonly Label _scriptInfo = Sro.Label("Empty script.", Muted, SroTheme.Small);
    private readonly Label _walkState = Sro.Label("Stopped.", Muted, SroTheme.Small);
    private readonly Label _mapHint = Sro.Label("Click the map to walk there (also while recording).", Muted, SroTheme.Small);
    private ScriptRecorder? _recorder;
    private SroClient? _recorderClient;
    private int _appended;
    private string _baseText = "";
    private AutoWalker? _walker;
    private WalkScript _script = WalkScript.Parse([], "script");
    private string? _file;

    public ScriptPage(Func<SroClient?> game)
    {
        _game = game;
        BackColor = Bg;
        Dock = DockStyle.Fill;

        _editor = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = false,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BorderStyle = BorderStyle.None,
            BackColor = InputBack,
            ForeColor = SroTheme.Text,
            Font = Mono,
            Dock = DockStyle.Fill,
        };
        _editor.TextChanged += (_, _) => Reparse();

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Bg, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 196));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Recorder -------------------------------------------------------------------------------
        var recorder = Panel("Recorder");
        var recStack = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var distanceRow = new FlowLayoutPanel { Height = 34, BackColor = Color.Transparent, WrapContents = false };
        var caption = Sro.Label("A point every", Muted, SroTheme.Small);
        caption.Margin = new Padding(0, 6, 6, 0);
        var units = Sro.Label("units walked, and where the character stops.", Muted, SroTheme.Small);
        units.Margin = new Padding(6, 6, 0, 0);
        _distance.Box.Text = "25";
        _distance.Margin = Padding.Empty;
        distanceRow.Controls.AddRange([caption, _distance, units]);
        var help = Sro.Label("Walk in the game (or click the map). Teleporters and return scrolls you use are recorded too.", Muted, SroTheme.Small);
        help.AutoSize = false;
        help.Height = 34;
        _recordState.AutoSize = false;
        _recordState.Height = 20;
        foreach (var c in new Control[] { _recordState, help, distanceRow, Row(_record, _stopRecord, _addWait, _clear) })
        {
            c.Dock = DockStyle.Top;
            recStack.Controls.Add(c);
        }
        recorder.Controls.Add(recStack);
        grid.Controls.Add(recorder, 0, 0);

        // Script text ----------------------------------------------------------------------------
        var script = Panel("Script");
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = InputBack, Padding = new Padding(8, 6, 4, 4) };
        frame.Controls.Add(_editor);
        _scriptInfo.AutoSize = false;
        _scriptInfo.Height = 22;
        _scriptInfo.Dock = DockStyle.Bottom;
        _scriptInfo.TextAlign = ContentAlignment.MiddleLeft;
        var files = Row(_open, _save);
        files.Dock = DockStyle.Top;
        script.Controls.Add(frame);
        script.Controls.Add(_scriptInfo);
        script.Controls.Add(files);
        grid.Controls.Add(script, 0, 1);

        // Auto walk ------------------------------------------------------------------------------
        var walk = Panel("Auto walk");
        var walkStack = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _walkState.AutoSize = false;
        _walkState.Height = 40;
        var walkHelp = Sro.Label("Starts at the script point nearest the character (or line 1), uses the teleports and return scrolls on the way.", Muted, SroTheme.Small);
        walkHelp.AutoSize = false;
        walkHelp.Height = 36;
        var loopRow = new FlowLayoutPanel { Height = 32, BackColor = Color.Transparent };
        loopRow.Controls.Add(_loop);
        foreach (var c in new Control[] { _walkState, walkHelp, loopRow, Row(_walk, _stopWalk) })
        {
            c.Dock = DockStyle.Top;
            walkStack.Controls.Add(c);
        }
        walk.Controls.Add(walkStack);
        grid.Controls.Add(walk, 1, 0);

        // Path on the map ------------------------------------------------------------------------
        var path = Panel("Path");
        _mapHint.Dock = DockStyle.Bottom;
        _mapHint.AutoSize = false;
        _mapHint.Height = 22;
        _mapHint.TextAlign = ContentAlignment.MiddleLeft;
        path.Controls.Add(_map);
        path.Controls.Add(_mapHint);
        _map.WalkRequested = (x, y) => _game() is { } client ? MapSnapshot.WalkTo(client, x, y) : "The bot is not running.";
        _map.Message += message => _mapHint.Text = message;
        grid.Controls.Add(path, 1, 1);

        Controls.Add(grid);

        _record.Click += (_, _) => StartRecording();
        _stopRecord.Click += (_, _) => StopRecording();
        _addWait.Click += (_, _) =>
        {
            if (IsRecording)
                _recorder!.AddWait(TimeSpan.FromSeconds(3)); // part of the recorded block
            else
                AppendLine(new WalkCommand(WalkCommandType.Wait, Delay: TimeSpan.FromSeconds(3)).ToLine());
        };
        _clear.Click += (_, _) => ClearScript();
        _open.Click += (_, _) => OpenFile();
        _save.Click += (_, _) => SaveFile();
        _walk.Click += (_, _) => StartWalking();
        _stopWalk.Click += (_, _) => StopWalking();
        UpdateButtons();
        Reparse();
    }

    /// <summary>Minimap tiles for the path view.</summary>
    public MediaImages? Tiles
    {
        get => _map.Tiles;
        set => _map.Tiles = value;
    }

    /// <summary>Where recording and walking are reported (set once the window's logger exists).</summary>
    public ILogger Logger { get; set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>The AutoTrade choices (preferred transport, job suit) used by RIDE,TRANSPORT.</summary>
    public NexBot.Configuration.TradeSettings Trade { get; set; } = new();

    public bool IsRecording => _recorder?.IsRecording == true;

    public bool IsWalking => _walker?.IsRunning == true;

    private static SroPanel Panel(string title) => new() { Title = title, Dock = DockStyle.Fill, Margin = new Padding(5) };

    private static TableLayoutPanel Row(params SroButton[] buttons)
    {
        var table = new TableLayoutPanel { ColumnCount = buttons.Length, Height = 40, BackColor = Color.Transparent, Margin = Padding.Empty };
        for (int i = 0; i < buttons.Length; i++)
        {
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / buttons.Length));
            buttons[i].Dock = DockStyle.Fill;
            buttons[i].Margin = new Padding(i == 0 ? 0 : 3, 0, i == buttons.Length - 1 ? 0 : 3, 8);
            table.Controls.Add(buttons[i], i, 0);
        }
        return table;
    }

    /// <summary>Called by the main window's timer: the character and spawns for the path view.</summary>
    public void SetSnapshot(MapSnapshot? snapshot) => _map.SetSnapshot(snapshot);

    /// <summary>Stops recording and walking (the bot stopped).</summary>
    public void StopAll()
    {
        StopRecording();
        StopWalking();
    }

    // ---- recording ----------------------------------------------------------------------------

    private void StartRecording()
    {
        if (_game() is not { } client)
        {
            _recordState.Text = "Start the bot and enter the game first.";
            _recordState.ForeColor = Warn;
            return;
        }
        if (_recorder is null || !ReferenceEquals(_recorderClient, client))
        {
            _recorder?.Dispose();
            _recorder = new ScriptRecorder(client);
            _recorder.Changed += (_, _) => RunOnUi(AppendRecorded);
            _recorderClient = client;
            _appended = 0;
        }
        if (double.TryParse(_distance.Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double distance))
            _recorder.MinDistance = Math.Clamp(distance, 5, 500);

        if (_editor.TextLength == 0)
            _editor.AppendText($"// NexBot walk script, recorded {DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}");
        // The recorded block is rewritten after the text that was there (the recorder may move its last point).
        _baseText = _editor.Text.Length == 0 || _editor.Text.EndsWith('\n') ? _editor.Text : _editor.Text + Environment.NewLine;
        _appended = _recorder.Commands.Count;
        string? error = _recorder.Start();
        _recordState.Text = error ?? "● Recording… walk the way you want the bot to go.";
        _recordState.ForeColor = error is null ? Bad : Warn;
        if (error is null)
            Logger.LogInformation("Script recording started");
        UpdateButtons();
    }

    private void StopRecording()
    {
        if (_recorder is not { IsRecording: true } recorder)
            return;
        recorder.Stop();
        AppendRecorded();
        _recordState.Text = "Recording stopped. Save the script, or press Record to add more.";
        _recordState.ForeColor = Muted;
        Logger.LogInformation("Script recording stopped");
        UpdateButtons();
    }

    /// <summary>Shows the lines recorded in this session after the text the editor had when recording started.</summary>
    private void AppendRecorded()
    {
        if (_recorder is null)
            return;
        var lines = _recorder.Commands.Skip(_appended).Select(c => c.ToLine() + Environment.NewLine);
        _editor.Text = _baseText + string.Concat(lines);
        _editor.SelectionStart = _editor.TextLength;
        _editor.ScrollToCaret();
    }

    private void AppendLine(string line)
    {
        if (_editor.TextLength > 0 && !_editor.Text.EndsWith('\n'))
            _editor.AppendText(Environment.NewLine);
        _editor.AppendText(line + Environment.NewLine);
    }

    private void ClearScript()
    {
        if (_editor.TextLength > 0 && MessageBox.Show(this, "Clear the script?", "NexBot", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _recorder?.Clear();
        _appended = 0;
        _editor.Clear();
        _file = null;
    }

    // ---- files --------------------------------------------------------------------------------

    private static string ScriptFolder
    {
        get
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "Scripts");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private void OpenFile()
    {
        using var dialog = new OpenFileDialog { Filter = "Walk scripts (*.txt)|*.txt|All files|*.*", InitialDirectory = ScriptFolder };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            _editor.Text = File.ReadAllText(dialog.FileName);
            _file = dialog.FileName;
            _recorder?.Clear();
            _appended = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _scriptInfo.Text = "Could not open: " + ex.Message;
        }
    }

    private void SaveFile()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Walk scripts (*.txt)|*.txt",
            InitialDirectory = _file is null ? ScriptFolder : Path.GetDirectoryName(_file),
            FileName = _file is null ? $"walk_{DateTime.Now:yyyyMMdd_HHmm}.txt" : Path.GetFileName(_file),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, _editor.Text);
            _file = dialog.FileName;
            Logger.LogInformation("Script saved to {File}", _file);
            Reparse();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _scriptInfo.Text = "Could not save: " + ex.Message;
        }
    }

    private void Reparse()
    {
        _script = WalkScript.Parse(_editor.Text, _file is null ? "script" : Path.GetFileName(_file));
        _map.SetScript(_script.Commands, _walker?.IsRunning == true ? _walker.CurrentIndex : -1);
        int moves = _script.Commands.Count(c => c.Type == WalkCommandType.Move);
        int teleports = _script.Commands.Count(c => c.Type is WalkCommandType.Teleport or WalkCommandType.Return);
        int shops = _script.Commands.Count(c => c.Type is WalkCommandType.Buy or WalkCommandType.Sell);
        string name = _file is null ? "Not saved" : Path.GetFileName(_file);
        _scriptInfo.Text = _script.Commands.Count == 0 ? $"{name} · empty script."
            : $"{name} · {moves} points, {teleports} teleports{(shops > 0 ? $", {shops} buy/sell" : "")}{(_script.Warnings.Count > 0 ? $" · {_script.Warnings.Count} lines ignored ({_script.Warnings[0]})" : "")}";
        _scriptInfo.ForeColor = _script.Warnings.Count > 0 ? Warn : Muted;
        UpdateButtons();
    }

    // ---- walking ------------------------------------------------------------------------------

    private void StartWalking()
    {
        if (_game() is not { } client)
        {
            ShowWalkState("Start the bot and enter the game first.", Warn);
            return;
        }
        StopRecording();
        _walker?.Dispose();
        _walker = new AutoWalker(client, _script, new AutoWalkOptions { Loop = _loop.Checked, Trade = Trade }, Logger);
        var walker = _walker;
        walker.StatusChanged += (_, _) => RunOnUi(() =>
        {
            if (!ReferenceEquals(walker, _walker))
                return;
            ShowWalkState(walker.Status, walker.IsRunning ? Good : Muted);
            _map.SetScript(_script.Commands, walker.IsRunning ? walker.CurrentIndex : -1);
            UpdateButtons();
        });
        walker.Start();
        ShowWalkState("Starting…", Good);
        UpdateButtons();
    }

    private void StopWalking()
    {
        _walker?.Stop();
        UpdateButtons();
    }

    private void ShowWalkState(string text, Color color)
    {
        _walkState.Text = text;
        _walkState.ForeColor = color;
    }

    private void UpdateButtons()
    {
        bool recording = IsRecording, walking = IsWalking;
        _record.Enabled = !recording && !walking;
        _stopRecord.Enabled = recording;
        _addWait.Enabled = !walking;
        _clear.Enabled = !recording && !walking;
        _open.Enabled = !recording && !walking;
        _walk.Enabled = !recording && !walking && _script.Commands.Any(c => c.Type == WalkCommandType.Move);
        _stopWalk.Enabled = walking;
        _editor.ReadOnly = recording || walking;
        _distance.Enabled = !recording;
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recorder?.Dispose();
            _walker?.Dispose();
        }
        base.Dispose(disposing);
    }
}
