using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Map;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>Pieces shared by the Party, Guild, Academy and Players pages.</summary>
internal static class SocialUi
{
    public static TableLayoutPanel Columns(int count, int rows = 1)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = count, RowCount = rows, BackColor = Bg, Margin = Padding.Empty };
        for (int i = 0; i < count; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / count));
        for (int i = 0; i < rows; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        return grid;
    }

    public static SroPanel Panel(string title) => new() { Title = title, Dock = DockStyle.Fill, Margin = new Padding(5) };

    /// <summary>Adds controls docked to the top so they read top to bottom in the order given.</summary>
    public static void Stack(Control parent, params Control[] controls)
    {
        for (int i = controls.Length - 1; i >= 0; i--)
        {
            controls[i].Dock = controls[i].Dock == DockStyle.None ? DockStyle.Top : controls[i].Dock;
            parent.Controls.Add(controls[i]);
        }
    }

    public static SroSwitch Switch(string text, bool value, Action<bool> set, Action changed)
    {
        var s = new SroSwitch { Text = text, Dock = DockStyle.Top, Height = 28, Checked = value };
        s.CheckedChanged += (_, _) =>
        {
            set(s.Checked);
            changed();
        };
        return s;
    }

    public static Label Caption(string text)
    {
        var caption = Sro.Label(text, GoldDim, SroTheme.Tiny);
        caption.AutoSize = false;
        caption.Dock = DockStyle.Top;
        caption.Height = 22;
        caption.TextAlign = ContentAlignment.BottomLeft;
        return caption;
    }

    public static Label Hint(string text, int height = 36)
    {
        var hint = Sro.Label(text, Muted, SroTheme.Small);
        hint.AutoSize = false;
        hint.Dock = DockStyle.Top;
        hint.Height = height;
        return hint;
    }

    /// <summary>
    /// A caption with an input on its right; <paramref name="set"/> gets the text on every change. A caption given
    /// with leading spaces belongs to the switch above it: it starts where the sub-options' texts start.
    /// </summary>
    public static Panel Field(string text, string value, int width, Action<string> set, Action changed)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        bool sub = text.StartsWith(' ');
        var label = Sro.Label(text.TrimStart(), sub ? Muted : SroTheme.Text, SroTheme.Small);
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        if (sub)
            label.Padding = new Padding(SroSwitch.SubOptionIndent + SroSwitch.TextOffset, 0, 0, 0);
        var input = new SroInput { Dock = DockStyle.Right, Width = width, Text = value };
        input.Box.TextChanged += (_, _) =>
        {
            set(input.Text.Trim());
            changed();
        };
        row.Controls.Add(label);
        row.Controls.Add(input);
        return row;
    }

    public static int Int(string text, int min, int max) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) ? Math.Clamp(n, min, max) : min;

    public static ListBox List() => new()
    {
        Dock = DockStyle.Fill, BackColor = InputBack, ForeColor = SroTheme.Text, BorderStyle = BorderStyle.None,
        Font = SroTheme.Body, IntegralHeight = false,
    };

    /// <summary>Shows <paramref name="lines"/> in a list box, keeping the selected line when it is still there.</summary>
    public static void Fill(ListBox list, IReadOnlyList<string> lines)
    {
        if (list.Items.Count == lines.Count && list.Items.Cast<string>().SequenceEqual(lines))
            return;
        string? selected = list.SelectedItem as string;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (string line in lines)
            list.Items.Add(line);
        if (selected is not null && lines.ToList().IndexOf(selected) is var at and >= 0)
            list.SelectedIndex = at;
        list.EndUpdate();
    }

    /// <summary>One row of a portrait list: the face (Media.pk2 path), the name line, a detail line, and HP / MP (0-100) or -1.</summary>
    public sealed record PortraitRow(string? Portrait, string Name, string Detail, int Hp = -1, int Mp = -1, bool Master = false);

    /// <summary>
    /// Makes <paramref name="list"/> draw its rows like the game's party window: a round portrait, the name, a
    /// detail line and, when given, red HP and blue MP bars. <paramref name="rows"/> gives each row's content.
    /// </summary>
    public static void PortraitList(ListBox list, IconCache icons, Func<int, PortraitRow?> rows, int height)
    {
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = height;
        icons.Loaded += list.Invalidate;
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || rows(e.Index) is not { } row)
                return;
            var g = e.Graphics;
            var r = e.Bounds;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(selected ? Hex("#2a2112") : InputBack))
                g.FillRectangle(back, r);
            if (selected)
                StrokeRect(g, GoldDim, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1));
            using (var line = new Pen(Hex("#1a1813")))
                g.DrawLine(line, r.Left + 4, r.Bottom - 1, r.Right - 4, r.Bottom - 1);

            int size = r.Height - 14;
            var disc = new RectangleF(r.X + 9, r.Y + 7, size, size);
            PortraitArt.DrawRound(g, disc, icons.Get(row.Portrait));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

            int x = (int)disc.Right + 12, right = r.Right - 8;
            bool bars = row.Hp >= 0;
            int nameTop = bars ? r.Y + 4 : r.Y + r.Height / 2 - 18;
            string name = row.Master ? "★ " + row.Name : row.Name;
            if (bars)
            {
                // Party frame: name on the left and level / guild on the right of the first line, the bars below.
                int barWidth = Math.Min(300, right - x);
                int detailWidth = Math.Min(TextRenderer.MeasureText(row.Detail, SroTheme.Small).Width + 4, barWidth / 2);
                TextRenderer.DrawText(g, row.Detail, SroTheme.Small, new Rectangle(x + barWidth - detailWidth, nameTop, detailWidth, 18), Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, name, SroTheme.Body, new Rectangle(x, nameTop, barWidth - detailWidth - 6, 18), row.Master ? Gold : SroTheme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                Bar(g, new Rectangle(x, nameTop + 22, barWidth, 7), row.Hp, Hex("#ff4a3d"), Hex("#8e1410"));
                Bar(g, new Rectangle(x, nameTop + 32, barWidth, 7), row.Mp, Hex("#5a8cff"), Hex("#16307e"));
                return;
            }
            TextRenderer.DrawText(g, name, SroTheme.Body, new Rectangle(x, nameTop, right - x, 18), row.Master ? Gold : SroTheme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, row.Detail, SroTheme.Small, new Rectangle(x, nameTop + 19, right - x, 18), Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
    }

    /// <summary>A thin gauge in the game's style: dark well, gradient fill, light top edge.</summary>
    private static void Bar(Graphics g, Rectangle r, int percent, Color top, Color bottom)
    {
        using (var well = new SolidBrush(Hex("#050404")))
            g.FillRectangle(well, r);
        int width = (int)(r.Width * Math.Clamp(percent, 0, 100) / 100f);
        if (width > 0)
        {
            var fill = new Rectangle(r.X, r.Y, width, r.Height);
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2), top, bottom, 90f);
            g.FillRectangle(brush, fill);
            using var shine = new Pen(Color.FromArgb(90, 255, 255, 255));
            g.DrawLine(shine, fill.Left, fill.Top, fill.Right - 1, fill.Top);
        }
        StrokeRect(g, Hex("#3a352a"), r);
    }

    /// <summary>The face portrait of a character model (CHAR_CH_MAN_ADVENTURER ...), or null.</summary>
    public static string? PortraitOf(SroClient? client, uint modelId) =>
        client?.Data is { } data && data.GetModel(modelId) is { } model ? CharacterSheet.PortraitOf(data, model.ServerName, model.Id) : null;

    public static Panel Buttons(params SroButton[] buttons)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        foreach (var b in buttons)
        {
            b.Height = 28;
            b.Margin = new Padding(0, 0, 6, 0);
        }
        bar.Controls.AddRange(buttons);
        return bar;
    }

    public static SroButton Button(string text, int width, Action click)
    {
        var b = new SroButton { Text = text, Width = width };
        b.Click += (_, _) => click();
        return b;
    }
}

/// <summary>A list of player names with an input to add one and a button to remove the selected one.</summary>
internal sealed class NameListEditor : UserControl
{
    private readonly List<string> _names;
    private readonly ListBox _list = SocialUi.List();
    private readonly SroInput _input = new() { Dock = DockStyle.Fill };

    public NameListEditor(List<string> names, Action changed)
    {
        _names = names;
        BackColor = Color.Transparent;
        var add = new SroButton { Text = "Add", Width = 56, Height = 28, Dock = DockStyle.Right };
        var remove = new SroButton { Text = "Remove", Width = 74, Height = 28, Dock = DockStyle.Right };
        void Add()
        {
            string name = _input.Text.Trim();
            if (name.Length == 0 || _names.Contains(name, StringComparer.OrdinalIgnoreCase))
                return;
            _names.Add(name);
            _input.Text = "";
            Show();
            changed();
        }
        add.Click += (_, _) => Add();
        _input.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Add();
            }
        };
        remove.Click += (_, _) =>
        {
            if (_list.SelectedItem is string name)
            {
                _names.Remove(name);
                Show();
                changed();
            }
        };
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(0, 4, 0, 0), BackColor = Color.Transparent };
        bar.Controls.Add(_input);
        bar.Controls.Add(add);
        bar.Controls.Add(remove);
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(_list);
        Controls.Add(well);
        Controls.Add(bar);
        Show();
    }

    private new void Show() => SocialUi.Fill(_list, [.. _names]);
}

/// <summary>Party (phBot's Party tab): members, accept / invite rules and lists, party matching.</summary>
internal sealed class PartyPage : UserControl
{
    private readonly Func<SroClient?> _game;
    private readonly Func<SocialManager?> _social;
    private readonly ListBox _members = SocialUi.List();
    private readonly ListBox _matches = SocialUi.List();
    private readonly Label _partyInfo = Sro.Label("", Muted, SroTheme.Small);
    private PartySettings _settings = new();
    private Action _changed = () => { };
    private readonly Panel _rules = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
    private readonly Panel _matching = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    private SocialUi.PortraitRow[] _memberRows = [];

    /// <summary>Members shown while not in game (screenshots for the guide only).</summary>
    internal SocialUi.PortraitRow[]? DemoRows { get; set; }

    public PartyPage(Func<SroClient?> game, Func<SocialManager?> social, IconCache icons)
    {
        _game = game;
        _social = social;
        BackColor = Bg;
        SocialUi.PortraitList(_members, icons, i => i < _memberRows.Length ? _memberRows[i] : null, 56);
        Dock = DockStyle.Fill;
        var grid = SocialUi.Columns(3);

        var membersPanel = SocialUi.Panel("Party");
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(_members);
        _partyInfo.AutoSize = false;
        _partyInfo.Height = 40;
        membersPanel.Controls.Add(well);
        SocialUi.Stack(membersPanel, _partyInfo, SocialUi.Buttons(
            SocialUi.Button("Leave party", 100, () => _game()?.LeaveParty()),
            SocialUi.Button("Kick selected", 110, Kick)));
        grid.Controls.Add(membersPanel, 0, 0);

        var rulesPanel = SocialUi.Panel("Accept / Invite");
        rulesPanel.Controls.Add(_rules);
        grid.Controls.Add(rulesPanel, 1, 0);

        var matchPanel = SocialUi.Panel("Matching");
        matchPanel.Controls.Add(_matching);
        grid.Controls.Add(matchPanel, 2, 0);

        Controls.Add(grid);
    }

    public void Bind(PartySettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        BuildRules();
        BuildMatching();
    }

    private void BuildRules()
    {
        var s = _settings;
        _rules.Controls.Clear();
        var setup = new SroCombo { Dock = DockStyle.Top };
        string[] setups = ["Exp shared · item shared · anyone invites", "Exp shared · item free · anyone invites", "Exp free · item shared · anyone invites",
            "Exp free · item free · anyone invites", "Exp shared · item shared · master invites", "Exp free · item free · master invites"];
        PartySetup[] values = [PartySetup.All, PartySetup.ExpShared | PartySetup.AnyoneCanInvite, PartySetup.ItemShared | PartySetup.AnyoneCanInvite,
            PartySetup.AnyoneCanInvite, PartySetup.ExpShared | PartySetup.ItemShared, PartySetup.None];
        setup.SetItems(setups);
        setup.SelectedIndex = Math.Max(0, Array.IndexOf(values, s.Setup));
        setup.SelectedIndexChanged += (_, _) =>
        {
            s.Setup = values[Math.Max(0, setup.SelectedIndex)];
            _changed();
        };
        var leaders = new NameListEditor(s.LeaderList, _changed) { Dock = DockStyle.Top, Height = 120 };
        var invites = new NameListEditor(s.AcceptInviteList, _changed) { Dock = DockStyle.Top, Height = 150 };
        SocialUi.Stack(_rules,
            SocialUi.Switch("Accept party invites", s.AcceptInvites, v => s.AcceptInvites = v, _changed),
            SocialUi.Switch("   Accept all invites (ignore the list)", s.AcceptAll, v => s.AcceptAll = v, _changed),
            SocialUi.Switch("   Accept invites from the list", s.AcceptFromList, v => s.AcceptFromList = v, _changed),
            SocialUi.Switch("   Refuse invites from players not in the list", s.RefuseNotInList, v => s.RefuseNotInList = v, _changed),
            SocialUi.Field("   Accept delay (seconds)", s.AcceptDelay.ToString(CultureInfo.CurrentCulture), 60, v => s.AcceptDelay = SocialUi.Int(v, 0, 60), _changed),
            SocialUi.Switch("Invite other players", s.InvitePlayers, v => s.InvitePlayers = v, _changed),
            SocialUi.Switch("   Invite all nearby players", s.InviteAll, v => s.InviteAll = v, _changed),
            SocialUi.Switch("   Invite only from the list", s.InviteFromList, v => s.InviteFromList = v, _changed),
            SocialUi.Caption("PARTY TYPE (WHEN WE CREATE IT)"), setup,
            SocialUi.Switch("Leave if the master is not in the leader list", s.LeaveIfLeaderNotInList, v => s.LeaveIfLeaderNotInList = v, _changed),
            SocialUi.Caption("PARTY LEADER LIST (EMPTY: NEVER LEAVE)"), leaders,
            SocialUi.Caption("ACCEPT / INVITE LIST"), invites);
    }

    private void BuildMatching()
    {
        var s = _settings;
        _matching.Controls.Clear();
        var listWell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        listWell.Controls.Add(_matches);
        _matching.Controls.Add(listWell);
        SocialUi.Stack(_matching,
            SocialUi.Field("Title", s.MatchTitle, 170, v => s.MatchTitle = v.Length > 0 ? v : "NexBot party", _changed),
            SocialUi.Field("Level from", s.MatchLevelMin.ToString(CultureInfo.CurrentCulture), 60, v => s.MatchLevelMin = (byte)SocialUi.Int(v, 1, 140), _changed),
            SocialUi.Field("Level to", s.MatchLevelMax.ToString(CultureInfo.CurrentCulture), 60, v => s.MatchLevelMax = (byte)SocialUi.Int(v, 1, 140), _changed),
            SocialUi.Switch("Auto form party matching (every 10 minutes)", s.AutoFormMatch, v => s.AutoFormMatch = v, _changed),
            SocialUi.Switch("Accept players asking to join (accept rules)", s.AcceptMatchJoins, v => s.AcceptMatchJoins = v, _changed),
            SocialUi.Field("Join by name (master)", s.JoinByName, 140, v => s.JoinByName = v, _changed),
            SocialUi.Field("Join by title (contains)", s.JoinByTitle, 140, v => s.JoinByTitle = v, _changed),
            SocialUi.Buttons(
                SocialUi.Button("View matching", 116, () => _social()?.RefreshMatches()),
                SocialUi.Button("Join selected", 110, JoinSelected)));
    }

    private void Kick()
    {
        if (_game() is not { World.Party: { } party } client || _members.SelectedIndex < 0 || _members.SelectedIndex >= party.Members.Count)
            return;
        client.KickFromParty(party.Members.GetAt(_members.SelectedIndex).Value.Id);
    }

    private void JoinSelected()
    {
        var matches = _social()?.Matches ?? [];
        if (_game() is { } client && _matches.SelectedIndex >= 0 && _matches.SelectedIndex < matches.Count)
            client.JoinPartyMatch(matches[_matches.SelectedIndex].Number);
    }

    public void RefreshView()
    {
        var client = _game();
        var party = client?.World.Party;
        _memberRows = party is null ? (client is null ? DemoRows ?? [] : []) : [.. party.Members.Values.Select((m, i) => new SocialUi.PortraitRow(
            SocialUi.PortraitOf(client, m.ModelId), m.Name,
            string.Create(CultureInfo.CurrentCulture, $"Lv. {m.Level}{(m.GuildName.Length > 0 ? "  ·  " + m.GuildName : "")}"),
            m.HpPercent, m.MpPercent, i == 0))];
        // The text keys the rows (selection kept across refreshes); HP / MP changes repaint them.
        SocialUi.Fill(_members, [.. _memberRows.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Name}|{r.Detail}|{r.Hp}|{r.Mp}"))]);
        _members.Invalidate();
        _partyInfo.Text = party is null ? "Not in a party." : $"{party.Members.Count} / {party.Capacity} members  ·  {party}";
        var matches = _social()?.Matches ?? [];
        SocialUi.Fill(_matches, [.. matches.Select(m =>
            string.Create(CultureInfo.CurrentCulture, $"#{m.Number}  {m.Title}  ·  {m.MasterName}  ·  {m.MemberCount}/{m.MemberMax}  ·  Lv. {m.LevelMin}-{m.LevelMax}"))]);
    }
}

/// <summary>Guild (phBot's Guild tab): accept invites, invite players above a level.</summary>
internal sealed class GuildPage : UserControl
{
    private readonly Func<SroClient?> _game;
    private readonly Label _guild = Sro.Value();
    private readonly Label _summary = Sro.Label("", Muted, SroTheme.Small);
    private readonly Label _notice = Sro.Label("", SroTheme.Text, SroTheme.Small);
    private readonly ListBox _members = SocialUi.List();
    private readonly Panel _options = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    private SocialUi.PortraitRow[] _rows = [];

    /// <summary>Members shown while not in game (screenshots for the guide only).</summary>
    internal SocialUi.PortraitRow[]? DemoRows { get; set; }

    public GuildPage(Func<SroClient?> game, IconCache icons)
    {
        _game = game;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        var grid = SocialUi.Columns(2);
        var info = SocialUi.Panel("Guild");
        _guild.AutoSize = false;
        _guild.Height = 28;
        _summary.AutoSize = false;
        _summary.Height = 22;
        _notice.AutoSize = false;
        _notice.Height = 0;
        SocialUi.PortraitList(_members, icons, i => i < _rows.Length ? _rows[i] : null, 48);
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(_members);
        info.Controls.Add(well);
        SocialUi.Stack(info, SocialUi.Caption("YOUR GUILD"), _guild, _summary, _notice, SocialUi.Caption("MEMBERS"));
        grid.Controls.Add(info, 0, 0);
        var options = SocialUi.Panel("Options");
        options.Controls.Add(_options);
        grid.Controls.Add(options, 1, 0);
        Controls.Add(grid);
    }

    public void Bind(GuildSettings s, Action changed)
    {
        _options.Controls.Clear();
        SocialUi.Stack(_options,
            SocialUi.Switch("Accept all guild invites", s.AcceptAllInvites, v => s.AcceptAllInvites = v, changed),
            SocialUi.Switch("Auto invite players without a guild", s.AutoInvite, v => s.AutoInvite = v, changed),
            SocialUi.Field("   with level above", s.InviteAboveLevel.ToString(CultureInfo.CurrentCulture), 60, v => s.InviteAboveLevel = SocialUi.Int(v, 0, 140), changed),
            SocialUi.Hint("Other players' levels are estimated from the gear they wear, so they are not exact (as in phBot)."));
    }

    public void RefreshView()
    {
        var client = _game();
        var world = client?.World;
        var guild = world?.Guild;
        _guild.Text = world?.Character is null ? (DemoRows is null ? "-" : "Phoenix  ·  Lv. 3")
            : guild is not null ? $"{guild.Name}  ·  Lv. {guild.Level}"
            : world.Character.GuildName.Length > 0 ? world.Character.GuildName : "No guild";
        var members = guild?.Members.Values
            .OrderBy(m => m.IsMaster ? 0 : 1).ThenBy(m => m.IsOffline).ThenByDescending(m => m.Level).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        _summary.Text = guild is null ? "" : string.Create(CultureInfo.CurrentCulture,
            $"{members.Count} members  ·  {members.Count(m => !m.IsOffline)} online  ·  {guild.GuildPoints:N0} GP{(guild.Master is { } master ? "  ·  master " + master.Name : "")}");
        string notice = guild is null ? "" : string.Join("\n", new[] { guild.Notice, guild.Message }.Where(t => t.Length > 0)).Replace("\r", "", StringComparison.Ordinal);
        _notice.Text = notice;
        _notice.Height = notice.Length == 0 ? 0 : Math.Min(80, 6 + 16 * (notice.Count(c => c == '\n') + 1));
        _rows = world is null && DemoRows is { } demo ? demo : [.. members.Select(m => new SocialUi.PortraitRow(
            SocialUi.PortraitOf(client, m.ModelId),
            m.Nickname.Length > 0 ? $"{m.Name}  ({m.Nickname})" : m.Name,
            string.Create(CultureInfo.CurrentCulture, $"{(m.IsMaster ? "Master" : "Member")}  ·  Lv. {m.Level}  ·  {(m.IsOffline ? "offline" : "online")}{(m.GuildPoints > 0 ? $"  ·  {m.GuildPoints:N0} GP" : "")}"),
            Master: m.IsMaster))];
        SocialUi.Fill(_members, [.. _rows.Select(r => r.Name + "|" + r.Detail)]);
    }
}

/// <summary>Academy (phBot's Academy tab): accept / invite rules, the list, a password players can whisper.</summary>
internal sealed class AcademyPage : UserControl
{
    private readonly Panel _options = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
    private readonly Panel _list = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };

    public AcademyPage()
    {
        BackColor = Bg;
        Dock = DockStyle.Fill;
        var grid = SocialUi.Columns(2);
        var options = SocialUi.Panel("Options");
        options.Controls.Add(_options);
        grid.Controls.Add(options, 0, 0);
        var list = SocialUi.Panel("Accept / Invite list");
        list.Controls.Add(_list);
        grid.Controls.Add(list, 1, 0);
        Controls.Add(grid);
    }

    public void Bind(AcademySettings s, Action changed)
    {
        _options.Controls.Clear();
        _list.Controls.Clear();
        SocialUi.Stack(_options,
            SocialUi.Switch("Accept academy invites", s.AcceptInvites, v => s.AcceptInvites = v, changed),
            SocialUi.Switch("   Accept all invites (ignore the list)", s.AcceptAll, v => s.AcceptAll = v, changed),
            SocialUi.Switch("   Accept invites from the list", s.AcceptFromList, v => s.AcceptFromList = v, changed),
            SocialUi.Switch("   Refuse invites from players not in the list", s.RefuseNotInList, v => s.RefuseNotInList = v, changed),
            SocialUi.Switch("Invite players to the academy", s.InvitePlayers, v => s.InvitePlayers = v, changed),
            SocialUi.Switch("   Invite all nearby players", s.InviteAll, v => s.InviteAll = v, changed),
            SocialUi.Switch("   Invite only from the list", s.InviteFromList, v => s.InviteFromList = v, changed),
            SocialUi.Field("Password (whisper it to be invited)", s.Password, 140, v => s.Password = v, changed),
            SocialUi.Hint("Academy matching, graduation and the member list are not built yet: their packets need a capture from your server.", 52));
        _list.Controls.Add(new NameListEditor(s.AcceptInviteList, changed) { Dock = DockStyle.Fill });
    }
}

/// <summary>Players (phBot's Players tab): who is near, their gear, invite buttons, and following a player.</summary>
internal sealed class PlayersPage : UserControl
{
    private readonly Func<SroClient?> _game;
    private readonly Func<SocialManager?> _social;
    private readonly ListBox _players = SocialUi.List();
    private readonly Label _message = Sro.Label("", Muted, SroTheme.Small);
    private readonly Label _traceState = Sro.Label("Not tracing.", Muted, SroTheme.Small);
    private readonly Panel _trace = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    private const int Cell = 44;
    private readonly EquipmentBoard _board = new() { Location = new Point(12, 40), Size = new Size(260, 420), EmptyName = "No player", EmptyLine = "Select a player in the list" };
    private readonly Dictionary<int, SlotView> _equip = [];
    private readonly Dictionary<int, SlotView> _avatar = [];
    private readonly SroButton _avatarToggle;
    private bool _showAvatar;
    private readonly IconCache _icons;
    private string _gearSignature = "";
    private string? _selectedName;
    private IReadOnlyList<Player> _shown = [];
    private SocialUi.PortraitRow[] _rows = [];
    private TraceSettings _settings = new();

    /// <summary>Players shown while not in game (screenshots for the guide only).</summary>
    internal SocialUi.PortraitRow[]? DemoRows { get; set; }
    private SroInput? _traceName;

    public PlayersPage(Func<SroClient?> game, Func<SocialManager?> social, IconCache icons)
    {
        _game = game;
        _social = social;
        _icons = icons;
        BackColor = Bg;
        SocialUi.PortraitList(_players, icons, i => i < _rows.Length ? _rows[i] : null, 48);
        _players.SelectedIndexChanged += (_, _) =>
        {
            if (Selected() is { } p)
                _selectedName = p.Name;
            ShowGear();
        };
        Dock = DockStyle.Fill;
        var grid = SocialUi.Columns(3);
        grid.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 50);
        grid.ColumnStyles[1] = new ColumnStyle(SizeType.Absolute, 300);
        grid.ColumnStyles[2] = new ColumnStyle(SizeType.Percent, 50);
        var listPanel = SocialUi.Panel("Players nearby");
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(_players);
        _message.AutoSize = false;
        _message.Dock = DockStyle.Bottom;
        _message.Height = 22;
        listPanel.Controls.Add(well);
        listPanel.Controls.Add(_message);
        // Two rows of buttons (docked to the top: the second row is added first).
        listPanel.Controls.Add(SocialUi.Buttons(
            SocialUi.Button("Academy invite", 116, () => Invite("academy", (c, p) => c.InviteToAcademy(p.UniqueId))),
            SocialUi.Button("Trace", 70, () =>
            {
                if (Selected() is { } p)
                {
                    _traceName!.Text = p.Name;
                    StartTrace();
                }
            })));
        listPanel.Controls.Add(SocialUi.Buttons(
            SocialUi.Button("Party invite", 100, () => Invite("party", (c, p) => c.InvitePlayerToParty(p.UniqueId))),
            SocialUi.Button("Guild invite", 100, () => Invite("guild", (c, p) => c.InviteToGuild(p.UniqueId))),
            SocialUi.Button("Union invite", 100, () => Invite("union", (c, p) => c.InviteToUnion(p.UniqueId)))));
        grid.Controls.Add(listPanel, 0, 0);

        // Middle: the selected player's gear, laid out like the Inventory page's equipment.
        var gearPanel = SocialUi.Panel("Equipment");
        foreach (var (slot, right, row) in InventoryPage.s_equipLayout)
            _equip[slot] = Place(new SlotView(icons, slot, false, CharacterSheet.EquipmentSlots[slot]), right, row, false);
        _equip[8] = Place(new SlotView(icons, 8, false, "Job"), true, -1, false);
        foreach (var (slot, right, row) in InventoryPage.s_avatarLayout)
            _avatar[slot] = Place(new SlotView(icons, slot, true, CharacterSheet.AvatarSlots[slot]) { Visible = false }, right, row, true);
        _avatarToggle = new SroButton { Text = "Avatar", Width = 90, Height = 28, Location = new Point(8, _board.Height - 36), TabStop = false };
        _avatarToggle.Click += (_, _) =>
        {
            _showAvatar = !_showAvatar;
            _gearSignature = "";
            ShowGear();
        };
        _board.Controls.Add(_avatarToggle);
        gearPanel.Controls.Add(_board);
        grid.Controls.Add(gearPanel, 1, 0);

        var tracePanel = SocialUi.Panel("Trace");
        tracePanel.Controls.Add(_trace);
        grid.Controls.Add(tracePanel, 2, 0);
        Controls.Add(grid);
        ShowGear();
    }

    private SlotView Place(SlotView slot, bool right, int row, bool avatar)
    {
        slot.Size = new Size(Cell, Cell);
        slot.AllowDrop = false;
        int x = right ? _board.Width - 8 - Cell : 8;
        int y = row < 0 ? _board.Height - 8 - Cell : 8 + row * (Cell + 10) + (row > 0 ? 16 : 0);
        slot.Location = new Point(x, y);
        slot.Hovered += s =>
        {
            if (s.Item is { } item && Selected() is { } owner)
                ItemPreview.Show(s, ItemCard.For(item, avatar, _game()?.Data.GetItem(item.ServerName), owner.Name), _icons);
        };
        slot.Unhovered += _ => ItemPreview.Hide();
        _board.Controls.Add(slot);
        return slot;
    }

    /// <summary>
    /// The selected player's worn gear and avatar as item slots (rare glow; the item card on hover). The server sends
    /// only what a player wears, with its plus: their bag, stat rolls and blues are never sent to other players.
    /// </summary>
    private void ShowGear()
    {
        var player = Selected();
        var client = _game();
        var me = client?.World.Character?.Position;
        var worn = new Dictionary<int, ItemView>();
        var avatar = new Dictionary<int, ItemView>();
        if (player is not null)
        {
            foreach (var item in player.Inventory.Where(i => i is not null).Cast<NexBot.Game.Items.Item>())
            {
                int slot = CharacterSheet.WornSlotOf(item, worn.Keys);
                if (slot >= 0 && !worn.ContainsKey(slot))
                    worn[slot] = CharacterSheet.ViewOf(item, slot);
            }
            foreach (var item in player.AvatarInventory.Where(i => i is not null).Cast<NexBot.Game.Items.Item>())
            {
                int slot = CharacterSheet.AvatarSlotOf(item);
                if (slot >= 0 && !avatar.ContainsKey(slot))
                    avatar[slot] = CharacterSheet.ViewOf(item, slot);
            }
        }
        string distance = player is not null && me is { } at ? string.Create(CultureInfo.CurrentCulture, $"{player.Position.DistanceTo(at):0} m") : "-";
        string signature = player is null ? "" : $"{player.Name}|{_showAvatar}|{distance}|{player.Stall is not null}|{player.IsRiding}|"
            + string.Join(",", worn.Select(w => w.Key + ":" + w.Value.Signature)) + "|" + string.Join(",", avatar.Select(a => a.Key + ":" + a.Value.Signature));
        if (signature == _gearSignature && _gearSignature.Length > 0)
            return;
        _gearSignature = signature;

        foreach (var (slot, view) in _equip)
        {
            view.Visible = !_showAvatar || slot == 8;
            view.SetItem(worn.GetValueOrDefault(slot));
        }
        foreach (var (slot, view) in _avatar)
        {
            view.Visible = _showAvatar;
            view.SetItem(avatar.GetValueOrDefault(slot));
        }
        _board.ShowAvatar = _showAvatar;
        _avatarToggle.Text = _showAvatar ? "Equipment" : $"Avatar ({avatar.Count})";
        _avatarToggle.Selected = _showAvatar;

        if (player is null)
        {
            _board.SetCharacter(null, null, _icons);
            return;
        }
        int level = SocialManager.EstimateLevel(player);
        string race = player.Info.ServerName.Contains("_EU_", StringComparison.OrdinalIgnoreCase) ? "European" : "Chinese";
        string state = player.Stall is not null ? "Stall" : player.IsRiding ? "Riding" : player.InCombat ? "In combat" : "Normal";
        (string, string)[] rows =
        [
            ("Guild", player.GuildName.Length > 0 ? player.GuildName : "-"),
            ("Job", player.JobType != JobType.None && player.HasJobMode() ? player.JobType.ToString() : "-"),
            ("PK cape", player.PvpCape.ToString()),
            ("State", state),
            ("Distance", distance),
            ("Items", (worn.Count + avatar.Count).ToString(CultureInfo.CurrentCulture)),
        ];
        string? portrait = client?.Data is { } data ? CharacterSheet.PortraitOf(data, player.Info.ServerName, player.Info.Id) : null;
        _board.SetPlayer(player.Name, $"~Lv. {(level > 0 ? level : "?")}  ·  {race}", rows, portrait, _icons);
    }

    public void Bind(TraceSettings s, Action changed)
    {
        _settings = s;
        _trace.Controls.Clear();
        var name = SocialUi.Field("Player to trace", s.Player, 160, v => s.Player = v, changed);
        _traceName = name.Controls.OfType<SroInput>().First();
        _traceState.AutoSize = false;
        _traceState.Height = 40;
        SocialUi.Stack(_trace, name,
            SocialUi.Switch("Game trace (the game follows the player)", s.GameTrace, v => s.GameTrace = v, changed),
            SocialUi.Field("Walk again when further than (m)", s.Distance.ToString(CultureInfo.CurrentCulture), 60, v => s.Distance = SocialUi.Int(v, 2, 50), changed),
            SocialUi.Buttons(SocialUi.Button("Start tracing", 110, StartTrace), SocialUi.Button("Stop tracing", 104, () => _social()?.StopTrace())),
            _traceState,
            SocialUi.Hint("Attacking monsters and buffing while tracing are not built yet.", 40));
    }

    private void StartTrace()
    {
        if (_social() is not { } social)
        {
            _traceState.Text = "Start the bot and enter the game first.";
            return;
        }
        social.StartTrace(_traceName?.Text ?? _settings.Player);
    }

    private Player? Selected() =>
        _players.SelectedIndex >= 0 && _players.SelectedIndex < _shown.Count ? _shown[_players.SelectedIndex] : null;

    private void Invite(string what, Func<SroClient, Player, bool> send)
    {
        if (_game() is not { } client || Selected() is not { } player)
        {
            _message.Text = "Select a player first.";
            _message.ForeColor = Warn;
            return;
        }
        bool ok = send(client, player);
        _message.Text = ok ? $"{what[0].ToString().ToUpperInvariant()}{what[1..]} invite sent to {player.Name}." : "The bot is not connected.";
        _message.ForeColor = ok ? Good : Bad;
    }

    public void RefreshView()
    {
        var client = _game();
        var me = client?.World.Character?.Position;
        _shown = client is null ? [] : [.. client.World.Players.Values
            .OrderBy(p => me is { } at ? p.Position.DistanceTo(at) : 0)];
        _rows = [.. _shown.Select(p =>
        {
            string guild = p.GuildName.Length > 0 ? $"  [{p.GuildName}]" : "";
            string job = p.JobType != JobType.None && p.HasJobMode() ? $"  ·  {p.JobType}" : "";
            string distance = me is { } at ? string.Create(CultureInfo.CurrentCulture, $"  ·  {p.Position.DistanceTo(at):0} m") : "";
            int level = SocialManager.EstimateLevel(p);
            string detail = $"~Lv. {(level > 0 ? level : "?")}{job}{(p.Stall is not null ? "  ·  stall" : "")}{distance}";
            return new SocialUi.PortraitRow(client?.Data is { } data ? CharacterSheet.PortraitOf(data, p.Info.ServerName, p.Info.Id) : null, p.Name + guild, detail);
        })];
        if (client is null && DemoRows is { } demo)
            _rows = demo;
        SocialUi.Fill(_players, [.. _rows.Select(r => r.Name + "|" + r.Detail)]);
        // Keep the same player chosen when the list re-sorts or their distance changes.
        if (_selectedName is not null && Selected()?.Name != _selectedName)
        {
            int at = _shown.ToList().FindIndex(p => p.Name == _selectedName);
            if (at >= 0)
                _players.SelectedIndex = at;
        }
        ShowGear();
        string tracing = _social()?.Tracing ?? "";
        _traceState.Text = tracing.Length == 0 ? "Not tracing."
            : client?.World.Players.ContainsKey(tracing) == true ? $"Tracing {tracing}." : $"Tracing {tracing} (not near: waiting for them).";
        _traceState.ForeColor = tracing.Length == 0 ? Muted : Good;
    }
}
