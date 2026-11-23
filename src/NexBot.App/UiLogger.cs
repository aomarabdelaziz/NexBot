using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexBot.App.Theme;

namespace NexBot.App;

/// <summary>What the Log page shows.</summary>
internal enum LogFilter { Normal, Warnings, Packets, Debug, All }

/// <summary>
/// Writes log lines into the window's log boxes (thread-safe). Every line is kept (the last <see cref="MaxKept"/>),
/// so the Log page can switch between normal lines, warnings, packet lines and debug lines and show them again.
/// The Activity box (Server page) shows only the normal lines.
/// </summary>
internal sealed partial class UiLogger : ILogger
{
    private const int MaxKept = 6000, MaxShown = 2000;

    internal sealed record Entry(DateTime Time, LogLevel Level, string Message, bool Packet);

    private readonly RichTextBox _log, _activity;
    private readonly List<Entry> _entries = [];
    private LogFilter _filter = LogFilter.Normal;
    private string _search = "";

    public UiLogger(RichTextBox log, RichTextBox activity) => (_log, _activity) = (log, activity);

    /// <summary>The lowest level written (Debug while the Log page shows debug lines, else Information).</summary>
    public LogLevel Minimum { get; private set; } = LogLevel.Information;

    public LogFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Minimum = value is LogFilter.Debug or LogFilter.All ? LogLevel.Debug : LogLevel.Information;
            Redraw();
        }
    }

    /// <summary>Only lines containing this text (any case); empty: all.</summary>
    public string Search
    {
        get => _search;
        set
        {
            _search = value?.Trim() ?? "";
            Redraw();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= Minimum;

    /// <summary>A packet dump line: "... S>C 0xB074 [0200]" (pick / pet / trace diagnostics).</summary>
    [GeneratedRegex(@"\b(S>C|C>S) 0x[0-9A-F]{4}\b|^Last packets before")]
    private static partial Regex PacketLine();

    public static bool IsPacket(string message) => PacketLine().IsMatch(message);

    public static bool Matches(Entry e, LogFilter filter, string search) =>
        filter switch
        {
            LogFilter.Normal => !e.Packet && e.Level >= LogLevel.Information,
            LogFilter.Warnings => e.Level >= LogLevel.Warning,
            LogFilter.Packets => e.Packet,
            LogFilter.Debug => e.Level < LogLevel.Information,
            _ => true,
        }
        && (search.Length == 0 || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase));

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        string message = formatter(state, exception);
        if (exception is not null)
            message += Environment.NewLine + exception;
        var entry = new Entry(DateTime.Now, logLevel, message, IsPacket(message));
        lock (_entries)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxKept)
                _entries.RemoveRange(0, _entries.Count - MaxKept);
        }
        if (Matches(entry, _filter, _search))
            Write(_log, entry);
        if (Matches(entry, LogFilter.Normal, ""))
            Write(_activity, entry);
    }

    /// <summary>Empties the Log page (and the Activity box): the kept lines too.</summary>
    public void Clear()
    {
        lock (_entries)
            _entries.Clear();
        OnUi(_log, () => _log.Clear());
        OnUi(_activity, () => _activity.Clear());
    }

    /// <summary>The lines the Log page shows now, as text (Save).</summary>
    public string ShownText()
    {
        List<Entry> shown;
        lock (_entries)
            shown = [.. _entries.Where(e => Matches(e, _filter, _search))];
        return string.Join(Environment.NewLine, shown.Select(e => $"[{e.Time:yyyy-MM-dd HH:mm:ss}] {Level(e.Level)}{e.Message}"));
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Warning => "WARN  ",
        LogLevel.Error or LogLevel.Critical => "ERROR ",
        LogLevel.Debug or LogLevel.Trace => "DEBUG ",
        _ => "",
    };

    private void Redraw()
    {
        List<Entry> shown;
        lock (_entries)
            shown = [.. _entries.Where(e => Matches(e, _filter, _search)).TakeLast(MaxShown)];
        OnUi(_log, () =>
        {
            _log.SuspendLayout();
            _log.Clear();
            foreach (var e in shown)
                Add(_log, e);
            _log.ResumeLayout();
            _log.Select(_log.TextLength, 0);
            _log.ScrollToCaret();
        });
    }

    private static void OnUi(Control box, Action action)
    {
        if (box.IsDisposed)
            return;
        if (!box.InvokeRequired)
        {
            action();
            return;
        }
        try
        {
            box.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // window closed
        }
    }

    private static void Write(RichTextBox box, Entry e) => OnUi(box, () =>
    {
        if (box.Lines.Length > MaxShown)
        {
            box.Select(0, box.GetFirstCharIndexFromLine(box.Lines.Length - MaxShown));
            box.SelectedText = "";
        }
        Add(box, e);
        box.ScrollToCaret();
    });

    private static void Add(RichTextBox box, Entry e)
    {
        Append(box, $"[{e.Time:HH:mm:ss}] ", SroTheme.GoldDim);
        Append(box, e.Message + Environment.NewLine, e.Level switch
        {
            LogLevel.Error or LogLevel.Critical => SroTheme.Bad,
            LogLevel.Warning => SroTheme.Warn,
            LogLevel.Debug or LogLevel.Trace => SroTheme.Muted,
            _ when e.Packet => SroTheme.Muted,
            _ => SroTheme.Text,
        });
    }

    // Insert at the end with a color. Selecting explicitly each time: a box on a hidden page does not move the caret
    // after AppendText, so the next color would land on the previous text.
    private static void Append(RichTextBox box, string text, Color color)
    {
        box.Select(box.TextLength, 0);
        box.SelectionColor = color;
        box.SelectedText = text;
    }
}
