using System.Media;
using NexBot.App.Theme;
using NexBot.Configuration;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>Plays alert sounds: a .wav file, or the Windows exclamation sound when none is set or it fails.</summary>
internal static class SoundPlayback
{
    private static SoundPlayer? _player;

    public static void Play(string file)
    {
        try
        {
            if (file.Length > 0 && File.Exists(file))
            {
                _player?.Dispose();
                _player = new SoundPlayer(file);
                _player.Play(); // plays on its own thread
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // not a valid .wav: fall back to the system sound
        }
        SystemSounds.Exclamation.Play();
    }
}

/// <summary>Sound (phBot's Sound tab): a sound for game events, with a file per event and a list of uniques.</summary>
internal sealed class SoundPage : UserControl
{
    private readonly Panel _alerts = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
    private readonly Panel _uniques = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };

    public SoundPage()
    {
        BackColor = Bg;
        Dock = DockStyle.Fill;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var alerts = SocialUi.Panel("Play a sound when");
        alerts.Controls.Add(_alerts);
        grid.Controls.Add(alerts, 0, 0);
        var uniques = SocialUi.Panel("Specific uniques");
        uniques.Controls.Add(_uniques);
        grid.Controls.Add(uniques, 1, 0);
        Controls.Add(grid);
    }

    public void Bind(SoundSettings settings, Action changed)
    {
        _alerts.Controls.Clear();
        _uniques.Controls.Clear();
        var rows = new List<Control>
        {
            SocialUi.Switch("Mute all sounds", settings.Muted, v => settings.Muted = v, changed),
            SocialUi.Hint("Each sound plays a .wav file you choose, or the Windows alert sound when none is set. The same event for the same player or monster rings at most once every 10 seconds.", 40),
        };
        foreach (var e in Enum.GetValues<SoundEvent>())
            rows.Add(Row(settings.Get(e), changed));
        SocialUi.Stack(_alerts, [.. rows]);

        _uniques.Controls.Add(new NameListEditor(settings.Uniques, changed) { Dock = DockStyle.Fill });
        SocialUi.Stack(_uniques, SocialUi.Hint(
            "Names, or parts of names (\"Tiger\", \"Uruchi\"), matched against the in-game unique notice and uniques that appear near you. Turn on \"Specific unique\" on the left.", 70));
    }

    private static Panel Row(SoundAlert alert, Action changed)
    {
        var (name, description) = SoundSettings.Describe(alert.Event);
        var row = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.Transparent, Padding = new Padding(0, 2, 0, 2) };
        var toggle = new SroSwitch { Text = name, Checked = alert.Enabled, Dock = DockStyle.Left, Width = 230 };
        toggle.CheckedChanged += (_, _) =>
        {
            alert.Enabled = toggle.Checked;
            changed();
        };
        var file = Sro.Label("", Muted, SroTheme.Small);
        file.AutoSize = false;
        file.Dock = DockStyle.Fill;
        file.TextAlign = ContentAlignment.MiddleLeft;
        file.AutoEllipsis = true;
        void ShowFile()
        {
            file.Text = alert.File.Length > 0 ? Path.GetFileName(alert.File) : description + "  ·  Windows sound";
            file.ForeColor = alert.File.Length > 0 ? SroTheme.Text : Muted;
        }
        ShowFile();
        var browse = new SroButton { Text = "File…", Width = 62, Dock = DockStyle.Right };
        var clear = new SroButton { Text = "×", Width = 30, Dock = DockStyle.Right };
        var test = new SroButton { Text = "Test", Width = 54, Dock = DockStyle.Right };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Wave sound (*.wav)|*.wav", Title = $"Sound for \"{name}\"" };
            if (alert.File.Length > 0 && Path.GetDirectoryName(alert.File) is { Length: > 0 } dir && Directory.Exists(dir))
                dialog.InitialDirectory = dir;
            if (dialog.ShowDialog() != DialogResult.OK)
                return;
            alert.File = dialog.FileName;
            ShowFile();
            changed();
        };
        clear.Click += (_, _) =>
        {
            alert.File = "";
            ShowFile();
            changed();
        };
        test.Click += (_, _) => SoundPlayback.Play(alert.File);
        var gap = new Panel { Dock = DockStyle.Right, Width = 6, BackColor = Color.Transparent };
        var gap2 = new Panel { Dock = DockStyle.Right, Width = 4, BackColor = Color.Transparent };
        var gap3 = new Panel { Dock = DockStyle.Right, Width = 4, BackColor = Color.Transparent };
        row.Controls.Add(file);
        row.Controls.Add(gap);
        row.Controls.Add(browse);
        row.Controls.Add(gap2);
        row.Controls.Add(clear);
        row.Controls.Add(gap3);
        row.Controls.Add(test);
        row.Controls.Add(toggle);
        return row;
    }
}
