using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Game;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// Attack (phBot's Attack page): Start / Stop of the bot over seven tabs: Attack (skills and imbue per monster type,
/// attack options), Buffs, Party Buffs, Resurrect, Healing, Lure and the Attack Log.
/// </summary>
internal sealed class AttackPage : UserControl
{
    private const string NoImbue = "(no imbue)";
    private const string NoSkill = "(none)";

    private readonly Func<SroClient?> _game;
    private readonly Func<HuntManager?> _hunt;
    private readonly IconCache _icons;
    private readonly Label _status = Sro.Label("", Muted, SroTheme.Small);
    private readonly List<Action> _refresh = [];
    private SroTabs? _tabs;
    private AttackSettings _settings = new();
    private TrainingAreaSettings _area = new();
    private Action _changed = () => { };
    private Action _areaChanged = () => { };
    private Action _startBot = () => { };

    public AttackPage(Func<SroClient?> game, Func<HuntManager?> hunt, IconCache icons)
    {
        _game = game;
        _hunt = hunt;
        _icons = icons;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        Padding = new Padding(5);
        Controls.Add(TopBar());
    }

    /// <param name="startBot">Starts the bot: the town loop first when in town, then hunting.</param>
    /// <param name="areaChanged">The training area was set here (Train here): keep the area list the same.</param>
    public void Bind(AttackSettings s, TrainingAreaSettings area, Action changed, Action startBot, Action areaChanged)
    {
        _settings = s;
        _area = area;
        _changed = changed;
        _startBot = startBot;
        _areaChanged = areaChanged;
        _refresh.Clear();
        var tabs = new SroTabs { Dock = DockStyle.Fill };
        tabs.Add("Attack", AttackTab());
        tabs.Add("Buffs", BuffsTab());
        tabs.Add("Party Buffs", PartyBuffsTab());
        tabs.Add("Resurrect", ResurrectTab());
        tabs.Add("Healing", HealingTab());
        tabs.Add("Lure", LureTab());
        tabs.Add("Attack Log", AttackLogTab());
        if (_tabs is not null)
        {
            Controls.Remove(_tabs);
            _tabs.Dispose();
        }
        _tabs = tabs;
        Controls.Add(tabs);
        tabs.BringToFront();
        RefreshView();
    }

    /// <summary>Shows a tab (for the guide's screenshots).</summary>
    public void SelectTab(int index)
    {
        if (_tabs is not null)
            _tabs.SelectedIndex = index;
    }

    // ------------------------------------------------------------------ top bar: start / stop

    private Panel TopBar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 6) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 300, BackColor = Color.Transparent, WrapContents = false };
        foreach (var b in new[]
        {
            SocialUi.Button("Start bot", 100, Start),
            SocialUi.Button("Stop", 70, () => _hunt()?.Stop("stopped by you")),
            SocialUi.Button("Train here", 96, TrainHere),
        })
        {
            b.Height = 30;
            b.Margin = new Padding(0, 0, 6, 0);
            buttons.Controls.Add(b);
        }
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        bar.Controls.Add(_status);
        bar.Controls.Add(buttons);
        return bar;
    }

    private void Start()
    {
        if (_hunt() is null)
        {
            _status.Text = "Start the bot first (Server page).";
            return;
        }
        _startBot();
        RefreshView();
    }

    /// <summary>Sets the training area (a circle, the radius kept) where the character stands.</summary>
    private void TrainHere()
    {
        if (_game()?.World.Character is not { } c)
        {
            _status.Text = "Log in first: the training area is set where the character stands.";
            return;
        }
        var at = c.EstimatePosition();
        _area.X = Math.Round(at.PosX, 1);
        _area.Y = Math.Round(at.PosY, 1);
        _area.Region = at.InDungeon ? at.Region : (ushort)0;
        _area.UsePolygon = false;
        _areaChanged();
        _changed();
        _status.Text = string.Create(CultureInfo.CurrentCulture, $"Training area set here: X {at.PosX:0}, Y {at.PosY:0}, radius {_area.Radius}.");
    }

    // ------------------------------------------------------------------ building blocks

    private static TableLayoutPanel Grid(params int[] percents)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = percents.Length, RowCount = 1, BackColor = Bg, Margin = Padding.Empty };
        foreach (int p in percents)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, p));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return grid;
    }

    /// <summary>A titled box whose content scrolls, stacked top to bottom.</summary>
    private static SroPanel Box(string title, params Control[] items)
    {
        var panel = SocialUi.Panel(title);
        var inner = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
        SocialUi.Stack(inner, items);
        panel.Controls.Add(inner);
        return panel;
    }

    private static Panel Well(ListBox list, int height)
    {
        var well = new Panel { Dock = DockStyle.Top, Height = height, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(list);
        return well;
    }

    private static Panel Row(string caption, Control fill, int captionWidth = 92)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent, Padding = new Padding(0, 2, 0, 2) };
        var label = Sro.Label(caption, SroTheme.Text, SroTheme.Small);
        label.AutoSize = false;
        label.Dock = DockStyle.Left;
        label.Width = captionWidth;
        label.TextAlign = ContentAlignment.MiddleLeft;
        fill.Dock = DockStyle.Fill;
        row.Controls.Add(fill);
        row.Controls.Add(label);
        return row;
    }

    private SroSwitch Switch(string text, bool value, Action<bool> set) => SocialUi.Switch(text, value, set, _changed);

    /// <summary>A caption with a number on its right ("Heal when HP % is under [X]").</summary>
    private Panel Number(string text, int value, int min, int max, Action<int> set) =>
        SocialUi.Field(text, value.ToString(CultureInfo.CurrentCulture), 64, v => set(SocialUi.Int(v, min, max)), _changed);

    /// <summary>A switch that is shown but cannot be used (the feature is not in vSRO 1.188).</summary>
    internal static SroSwitch Missing(string text, string why) =>
        new() { Text = $"{text} ({why})", Dock = DockStyle.Top, Height = 28, Enabled = false };

    private Skill[] Learned() =>
        [.. (_game()?.World.Character?.Skills.Values ?? Enumerable.Empty<Skill>())
            .GroupBy(k => AttackPlan.LineOf(k.ServerName), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(k => k.Info.SkillLevel)!)
            .OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase)];

    private string NameOf(string line)
    {
        var client = _game();
        if (client?.World.Character is { } c && AttackPlan.Learned(c.Skills.Values, line) is { } learned)
            return learned.Name;
        return client?.Data.GetSkill(line + "_01")?.Name is { Length: > 0 } name ? name : line;
    }

    private string? IconOf(string line)
    {
        var client = _game();
        if (client?.World.Character is { } c && AttackPlan.Learned(c.Skills.Values, line) is { } learned)
            return learned.Info.Icon;
        return client?.Data.GetSkill(line + "_01")?.Icon;
    }

    private static string LabelOf(Skill k) =>
        k.Info.Mana > 0 ? $"{k.Name}  ·  Lv. {k.Info.SkillLevel}  ·  MP {k.Info.Mana}" : $"{k.Name}  ·  Lv. {k.Info.SkillLevel}";

    /// <summary>A list box whose rows show a skill icon on the left (owner drawn).</summary>
    private sealed class IconListBox : ListBox
    {
        public List<string?> Icons { get; } = [];
    }

    private IconListBox IconList()
    {
        var list = new IconListBox
        {
            Dock = DockStyle.Fill, BackColor = InputBack, ForeColor = SroTheme.Text, BorderStyle = BorderStyle.None,
            Font = SroTheme.Body, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 30,
        };
        _icons.Loaded += list.Invalidate;
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= list.Items.Count)
                return;
            var g = e.Graphics;
            var r = e.Bounds;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(selected ? Hex("#2a2112") : InputBack))
                g.FillRectangle(back, r);
            if (selected)
                StrokeRect(g, GoldDim, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1));
            int x = r.X + 4;
            if (e.Index < list.Icons.Count && list.Icons[e.Index] is { } path)
            {
                if (_icons.Get(path) is { } icon)
                    g.DrawImage(icon, new Rectangle(x, r.Y + 3, 24, 24));
                x += 30;
            }
            TextRenderer.DrawText(g, list.Items[e.Index]?.ToString(), SroTheme.Body, new Rectangle(x, r.Y, r.Right - x - 4, r.Height),
                selected ? Value : SroTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        return list;
    }

    private static void Show(IconListBox list, IReadOnlyList<string> rows, IReadOnlyList<string?> icons)
    {
        bool same = list.Icons.SequenceEqual(icons);
        list.Icons.Clear();
        list.Icons.AddRange(icons);
        SocialUi.Fill(list, rows);
        if (!same)
            list.Invalidate();
    }

    /// <summary>The learned skills of a kind, with a search box; a double-click gives <paramref name="pick"/> the skill line.</summary>
    private (Panel Search, Panel List, Func<string?> Selected) Picker(Func<Skill, bool> fits, string none, Action<string> pick, int height)
    {
        var search = new SroInput();
        var list = IconList();
        var lines = new List<string>();
        string? Selected() => list.SelectedIndex >= 0 && list.SelectedIndex < lines.Count ? lines[list.SelectedIndex] : null;
        void Refresh()
        {
            var all = Learned().Where(fits).ToList();
            string text = search.Text.Trim();
            var shown = all.Where(k => text.Length == 0 || k.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || k.ServerName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            lines.Clear();
            lines.AddRange(shown.Select(k => AttackPlan.LineOf(k.ServerName)));
            if (shown.Count > 0)
                Show(list, [.. shown.Select(LabelOf)], [.. shown.Select(k => (string?)k.Info.Icon)]);
            else
                Show(list, [all.Count == 0 ? none : "No skill matches the search."], [null]);
        }
        search.Box.TextChanged += (_, _) => Refresh();
        list.DoubleClick += (_, _) =>
        {
            if (Selected() is { } line)
                pick(line);
        };
        _refresh.Add(Refresh);
        return (Row("Search", search, 60), Well(list, height), Selected);
    }

    /// <summary>An ordered list of skill lines (icons, numbered) with Remove / Up / Down; <paramref name="lines"/> may change (monster type).</summary>
    private (Panel List, Panel Buttons) Lines(Func<List<string>> lines, int height, bool numbered = true)
    {
        var list = IconList();
        void Refresh()
        {
            var l = lines();
            Show(list, [.. l.Select((x, i) => numbered ? $"{i + 1}.  {NameOf(x)}" : NameOf(x))], [.. l.Select(IconOf)]);
        }
        void Move(int by)
        {
            var l = lines();
            int i = list.SelectedIndex, j = i + by;
            if (i < 0 || j < 0 || j >= l.Count)
                return;
            (l[i], l[j]) = (l[j], l[i]);
            _changed();
            Refresh();
            list.SelectedIndex = j;
        }
        void Remove()
        {
            var l = lines();
            if (list.SelectedIndex < 0 || list.SelectedIndex >= l.Count)
                return;
            l.RemoveAt(list.SelectedIndex);
            _changed();
            Refresh();
        }
        list.DoubleClick += (_, _) => Remove();
        _refresh.Add(Refresh);
        return (Well(list, height), SocialUi.Buttons(SocialUi.Button("Remove", 70, Remove), SocialUi.Button("Up", 44, () => Move(-1)), SocialUi.Button("Down", 56, () => Move(1))));
    }

    private void AddLine(List<string> list, string line)
    {
        if (list.Contains(line, StringComparer.OrdinalIgnoreCase))
            return;
        list.Add(line);
        _changed();
        RefreshLists();
    }

    /// <summary>A combo of learned skills of a kind (first entry: none) bound to a skill line.</summary>
    private SroCombo SkillCombo(Func<Skill, bool> fits, Func<string> get, Action<string> set, string none = NoSkill)
    {
        var combo = new SroCombo();
        var lines = new List<string>();
        bool filling = false;
        void Refresh()
        {
            var now = Learned().Where(fits).Select(k => AttackPlan.LineOf(k.ServerName)).ToList();
            string current = get();
            if (current.Length > 0 && !now.Contains(current, StringComparer.OrdinalIgnoreCase))
                now.Add(current);
            filling = true;
            try
            {
                if (!now.SequenceEqual(lines, StringComparer.OrdinalIgnoreCase) || combo.Items.Count == 0)
                {
                    lines.Clear();
                    lines.AddRange(now);
                    combo.SetItems([none, .. lines.Select(NameOf)]);
                }
                int want = lines.FindIndex(l => string.Equals(l, current, StringComparison.OrdinalIgnoreCase)) + 1;
                if (combo.SelectedIndex != want)
                    combo.SelectedIndex = want;
            }
            finally
            {
                filling = false;
            }
        }
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (filling)
                return;
            int i = combo.SelectedIndex - 1;
            set(i >= 0 && i < lines.Count ? lines[i] : "");
            _changed();
        };
        _refresh.Add(Refresh);
        return combo;
    }

    // ------------------------------------------------------------------ Attack tab

    private Control AttackTab()
    {
        var s = _settings;
        var type = new SroCombo();
        type.SetItems(AttackPlan.Types);
        type.SelectedIndex = 0;
        string Current() => AttackPlan.Types[Math.Max(0, type.SelectedIndex)];
        var imbue = SkillCombo(k => AttackPlan.KindOf(k.Info) == SkillKind.Imbue, () => s.Imbues.GetValueOrDefault(Current(), ""),
            v =>
            {
                if (v.Length > 0)
                    s.Imbues[Current()] = v;
                else
                    s.Imbues.Remove(Current());
            }, NoImbue);
        var (typeList, typeButtons) = Lines(() => s.SkillsOf(Current()), 150);
        var picker = Picker(k => AttackPlan.KindOf(k.Info) == SkillKind.Attack, "Log in to see the learned skills.", l => AddLine(s.SkillsOf(Current()), l), 170);
        type.SelectedIndexChanged += (_, _) => RefreshLists();
        var grid = Grid(50, 50);
        grid.Controls.Add(Box("Attack skills",
            Row("Monster type", type), Row("Imbue", imbue),
            SocialUi.Caption("SKILLS FOR THIS TYPE (CAST IN THIS ORDER)"), typeList, typeButtons,
            SocialUi.Caption("SKILLS (NO IMBUES OR PASSIVES)"), picker.Search, picker.List,
            SocialUi.Buttons(SocialUi.Button("Add", 60, () =>
            {
                if (picker.Selected() is { } l)
                    AddLine(s.SkillsOf(Current()), l);
            })),
            SocialUi.Hint("Double-click a skill to add it, or a skill of the type to remove it. A type with no skills uses a lower type's (see Use lower skills), and the common attack when no skill is ready.", 48)), 0, 0);
        grid.Controls.Add(Box("Options",
            Switch("Cast skills in order", s.CastInOrder, v => s.CastInOrder = v),
            Switch("Kill steal monsters", s.KillSteal, v => s.KillSteal = v),
            Switch("Don't attack monsters", s.DontAttack, v => s.DontAttack = v),
            Switch("Protect party members", s.ProtectParty, v => s.ProtectParty = v),
            SocialUi.Field("   Only this member (empty = all)", s.ProtectPartyName, 110, v => s.ProtectPartyName = v, _changed),
            Switch("Attack lower monsters first", s.AttackLowerFirst, v => s.AttackLowerFirst = v),
            Number("Switch monster after X DOT (0 = off)", s.SwitchAfterDots, 0, 20, v => s.SwitchAfterDots = v),
            Switch("Use lower skills if a type has none", s.UseLowerSkills, v => s.UseLowerSkills = v),
            Switch("Slower attack mode", s.SlowerAttack, v => s.SlowerAttack = v),
            Switch("Lagtastic (more packets, laggy servers)", s.Lagtastic, v => s.Lagtastic = v),
            Switch("Auto select nearby uniques (not botting)", s.AutoSelectUniques, v => s.AutoSelectUniques = v),
            Switch("Auto select nearby titans (not botting)", s.AutoSelectTitans, v => s.AutoSelectTitans = v),
            Switch("Use teleport skills (Ghost Walk / Teleport)", s.UseTeleportSkills, v => s.UseTeleportSkills = v),
            Number("Attack range (world units)", s.Range, 10, 500, v => s.Range = v),
            SocialUi.Hint("Monsters attacking you come first (with Attack lower monsters first: the weakest type first), then those attacking your party, then the Monster preferences (Protection page), then the nearest.", 62)), 1, 0);
        return grid;
    }

    // ------------------------------------------------------------------ Buffs tab

    private Control BuffsTab()
    {
        var s = _settings;
        var picker = Picker(k => AttackPlan.KindOf(k.Info) == SkillKind.Buff, "Log in to see the learned buffs.", l => AddLine(s.Buffs, l), 150);
        var (buffs, buffButtons) = Lines(() => s.Buffs, 130);
        var (emergency, emergencyButtons) = Lines(() => s.EmergencyBuffs, 90, numbered: false);
        var grid = Grid(50, 50);
        grid.Controls.Add(Box("Buffs",
            SocialUi.Caption("LEARNED BUFFS"), picker.Search, picker.List,
            SocialUi.Buttons(
                SocialUi.Button("Add to buffs", 104, () => { if (picker.Selected() is { } l) AddLine(s.Buffs, l); }),
                SocialUi.Button("Add to emergency", 130, () => { if (picker.Selected() is { } l) AddLine(s.EmergencyBuffs, l); })),
            SocialUi.Caption("BUFFS (ALL WEAPONS)"), buffs, buffButtons,
            SocialUi.Caption("EMERGENCY BUFFS"), emergency, emergencyButtons), 0, 0);
        grid.Controls.Add(Box("Options",
            Switch("Buff while attacking monsters", s.BuffWhileAttacking, v => s.BuffWhileAttacking = v),
            Switch("Switch weapons for buffs (from the bag)", s.SwitchWeaponsForBuffs, v => s.SwitchWeaponsForBuffs = v),
            Switch("Use Devil's Spirit (avatar skills)", s.UseDevilSpirit, v => s.UseDevilSpirit = v),
            Missing("Cast Mirror Reflect", "D12: not in vSRO 1.188"),
            Number("Emergency buff when HP % < X (0 = off)", s.EmergencyHp, 0, 100, v => s.EmergencyHp = v),
            Number("Emergency buff when MP % < X (0 = off)", s.EmergencyMp, 0, 100, v => s.EmergencyMp = v),
            Number("Emergency when monsters attacking > X (0 = off)", s.EmergencyMonsters, 0, 50, v => s.EmergencyMonsters = v),
            Switch("Cast Force Cure on bad status (Chinese)", s.ForceCure, v => s.ForceCure = v),
            Switch("Recast all buffs when one buff ends", s.RecastAllWhenOneEnds, v => s.RecastAllWhenOneEnds = v),
            SocialUi.Hint("A buff is cast when it is not on, it is ready and there is enough MP. A buff that needs another weapon puts on a weapon from the bag that fits it; then the first weapon goes back on. A refused buff is tried again after 30 seconds.", 76)), 1, 0);
        return grid;
    }

    // ------------------------------------------------------------------ Party Buffs tab

    private Control PartyBuffsTab()
    {
        var s = _settings;
        var buffList = IconList();
        var playersOfBuff = SocialUi.List();
        var players = SocialUi.List();
        var nearNames = new List<string>();
        PartyBuff? Chosen() => buffList.SelectedIndex >= 0 && buffList.SelectedIndex < s.PartyBuffs.Count ? s.PartyBuffs[buffList.SelectedIndex] : null;
        void ShowPlayersOfBuff() => SocialUi.Fill(playersOfBuff, Chosen() is { } pb ? [.. pb.Players] : ["Select a party buff."]);
        void Refresh()
        {
            Show(buffList, [.. s.PartyBuffs.Select(b => $"{NameOf(b.Skill)}  ·  {b.Players.Count} player(s)")], [.. s.PartyBuffs.Select(b => IconOf(b.Skill))]);
            ShowPlayersOfBuff();
            var game = _game();
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (game?.World.Party is { } party)
                foreach (var m in party.Members.Values)
                    names.Add(m.Name);
            if (game is not null)
                foreach (var p in game.World.Players.Values.Where(p => p != game.World.Character))
                    names.Add(p.Name);
            if (game?.World.Character is { } me)
                names.Remove(me.Name);
            nearNames.Clear();
            nearNames.AddRange(names);
            SocialUi.Fill(players, nearNames.Count == 0 ? ["No party member or player near."] : [.. nearNames]);
        }
        buffList.SelectedIndexChanged += (_, _) => ShowPlayersOfBuff();
        _refresh.Add(Refresh);
        void AddBuff(string line)
        {
            if (s.PartyBuffs.Any(b => string.Equals(b.Skill, line, StringComparison.OrdinalIgnoreCase)))
                return;
            s.PartyBuffs.Add(new PartyBuff { Skill = line });
            _changed();
            Refresh();
            buffList.SelectedIndex = s.PartyBuffs.Count - 1;
        }
        var picker = Picker(k => AttackPlan.KindOf(k.Info) == SkillKind.Buff, "Log in to see the learned buffs.", AddBuff, 130);
        void Keep(Action change)
        {
            int keep = buffList.SelectedIndex;
            change();
            _changed();
            Refresh();
            if (keep < buffList.Items.Count)
                buffList.SelectedIndex = keep;
        }
        void AddPlayer()
        {
            if (Chosen() is not { } pb || players.SelectedIndex < 0 || players.SelectedIndex >= nearNames.Count)
                return;
            string name = nearNames[players.SelectedIndex];
            if (!pb.Players.Contains(name, StringComparer.OrdinalIgnoreCase))
                Keep(() => pb.Players.Add(name));
        }
        void RemovePlayer()
        {
            if (Chosen() is { } pb && playersOfBuff.SelectedIndex >= 0 && playersOfBuff.SelectedIndex < pb.Players.Count)
                Keep(() => pb.Players.RemoveAt(playersOfBuff.SelectedIndex));
        }
        void RemoveBuff()
        {
            if (Chosen() is { } pb)
                Keep(() => s.PartyBuffs.Remove(pb));
        }
        players.DoubleClick += (_, _) => AddPlayer();
        var triggers = new NameListEditor(s.PartyBuffTriggerNames, _changed) { Dock = DockStyle.Top, Height = 110 };
        var grid = Grid(38, 30, 32);
        grid.Controls.Add(Box("Party buffs",
            SocialUi.Caption("PARTY BUFFS (SELECT ONE, THEN ADD PLAYERS)"), Well(buffList, 140),
            SocialUi.Buttons(SocialUi.Button("Remove buff", 100, RemoveBuff)),
            SocialUi.Caption("PLAYERS OF THIS BUFF"), Well(playersOfBuff, 100),
            SocialUi.Buttons(SocialUi.Button("Remove player", 110, RemovePlayer)),
            SocialUi.Caption("LEARNED BUFFS (DOUBLE-CLICK: NEW PARTY BUFF)"), picker.Search, picker.List,
            SocialUi.Buttons(SocialUi.Button("Add buff", 80, () => { if (picker.Selected() is { } l) AddBuff(l); }))), 0, 0);
        grid.Controls.Add(Box("Players",
            SocialUi.Caption("PARTY MEMBERS AND PLAYERS NEAR"), Well(players, 300),
            SocialUi.Buttons(SocialUi.Button("← Add to the buff", 130, AddPlayer)),
            SocialUi.Hint("Select a party buff on the left, a player here, and click Add (or double-click the player).", 46)), 1, 0);
        grid.Controls.Add(Box("Options",
            Number("Party buffing radius", s.PartyBuffRadius, 5, 300, v => s.PartyBuffRadius = v),
            Switch("Walk back to the center after buffing", s.WalkBackAfterBuffing, v => s.WalkBackAfterBuffing = v),
            Switch("Buff all nearby players", s.BuffAllNearbyPlayers, v => s.BuffAllNearbyPlayers = v),
            Switch("Start / stop party buffing if a player is near", s.PartyBuffOnlyIfNear, v => s.PartyBuffOnlyIfNear = v),
            SocialUi.Caption("PLAYER NAMES (EMPTY: ALWAYS BUFF)"), triggers,
            SocialUi.Hint("Don't let two characters of the same class buff the same player with the same buff.", 34)), 2, 0);
        return grid;
    }

    // ------------------------------------------------------------------ Resurrect tab

    private Control ResurrectTab()
    {
        var s = _settings;
        var list = SocialUi.List();
        var near = SocialUi.List();
        var nearNames = new List<string>();
        void Refresh()
        {
            SocialUi.Fill(list, s.ResurrectNames.Count == 0 ? ["(nobody: add players from the right)"] : [.. s.ResurrectNames]);
            var game = _game();
            nearNames.Clear();
            if (game is not null)
                nearNames.AddRange(game.World.Players.Values.Where(p => p != game.World.Character).Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase));
            SocialUi.Fill(near, nearNames.Count == 0 ? ["No player near."] : [.. nearNames.Select(n =>
                game!.World.Players.TryGetValue(n, out var p) && p.LifeState == LifeState.Dead ? n + "  (dead)" : n)]);
        }
        void Add()
        {
            if (near.SelectedIndex < 0 || near.SelectedIndex >= nearNames.Count)
                return;
            string name = nearNames[near.SelectedIndex];
            if (!s.ResurrectNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                s.ResurrectNames.Add(name);
                _changed();
            }
            Refresh();
        }
        void Remove()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= s.ResurrectNames.Count)
                return;
            s.ResurrectNames.RemoveAt(list.SelectedIndex);
            _changed();
            Refresh();
        }
        near.DoubleClick += (_, _) => Add();
        list.DoubleClick += (_, _) => Remove();
        _refresh.Add(Refresh);
        var grid = Grid(34, 33, 33);
        grid.Controls.Add(Box("Resurrect list", Well(list, 300), SocialUi.Buttons(SocialUi.Button("Remove", 70, Remove))), 0, 0);
        grid.Controls.Add(Box("Players near you", Well(near, 300), SocialUi.Buttons(SocialUi.Button("← Add", 70, Add))), 1, 0);
        grid.Controls.Add(Box("Options",
            Number("Resurrect radius", s.ResurrectRadius, 5, 300, v => s.ResurrectRadius = v),
            Number("Resurrect delay (seconds)", s.ResurrectDelay, 1, 120, v => s.ResurrectDelay = v),
            Switch("Resurrect all party members", s.ResurrectAllParty, v => s.ResurrectAllParty = v),
            Number("Resurrect try limit", s.ResurrectTryLimit, 1, 100, v => s.ResurrectTryLimit = v),
            Switch("Auto accept res from other players", s.AutoAcceptResurrect, v => s.AutoAcceptResurrect = v),
            Switch("   Only accept res from party members", s.OnlyAcceptPartyResurrect, v => s.OnlyAcceptPartyResurrect = v),
            SocialUi.Hint("The learned resurrect skills are used (Soul / Ghost / Spirit Rebirth Art ...): a group resurrect first when more than one is dead. Invisible players cannot be seen, so they are not resurrected.", 62)), 2, 0);
        return grid;
    }

    // ------------------------------------------------------------------ Healing tab

    private Control HealingTab()
    {
        var s = _settings;
        var picker = Picker(k => AttackPlan.IsHeal(k.Info), "Log in to see the learned heal skills.", l => AddLine(s.HealSkills, l), 160);
        var (heals, healButtons) = Lines(() => s.HealSkills, 150);
        var grid = Grid(55, 45);
        grid.Controls.Add(Box("Heal skills",
            SocialUi.Caption("HEAL SKILL LIST (THE FIRST READY ONE IS USED)"), heals, healButtons,
            SocialUi.Caption("LEARNED HEAL SKILLS"), picker.Search, picker.List,
            SocialUi.Buttons(SocialUi.Button("Add", 60, () => { if (picker.Selected() is { } l) AddLine(s.HealSkills, l); }))), 0, 0);
        grid.Controls.Add(Box("Healing",
            Number("Heal party member when HP % < X (0 = off)", s.HealMemberBelow, 0, 100, v => s.HealMemberBelow = v),
            Number("Group heal when average HP % < X (0 = off)", s.GroupHealBelow, 0, 100, v => s.GroupHealBelow = v),
            Number("Heal self when HP % < X (0 = off)", s.HealSelfBelow, 0, 100, v => s.HealSelfBelow = v),
            SocialUi.Hint("Yourself first, then the group (a heal without a target, e.g. Group Healing), then the member with the lowest HP (a heal with a target). The party list gives the members' HP in steps of 10 %.", 62)), 1, 0);
        return grid;
    }

    // ------------------------------------------------------------------ Lure tab

    private Control LureTab()
    {
        var s = _settings;
        var shout = SkillCombo(k => AttackPlan.KindOf(k.Info) == SkillKind.Attack && !k.Info.TargetRequired, () => s.LureSkill, v => s.LureSkill = v);
        var script = Sro.Label(ScriptName(s.LureScript), SroTheme.Text, SroTheme.Small);
        script.AutoSize = false;
        script.TextAlign = ContentAlignment.MiddleLeft;
        void ChooseScript()
        {
            using var dialog = new OpenFileDialog { Filter = "Walk scripts (*.txt)|*.txt|All files (*.*)|*.*", Title = "Lure script" };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            s.LureScript = dialog.FileName;
            script.Text = ScriptName(s.LureScript);
            _changed();
        }
        void ClearScript()
        {
            s.LureScript = "";
            script.Text = ScriptName("");
            _changed();
        }
        var grid = Grid(50, 50);
        grid.Controls.Add(Box("Lure",
            Switch("Lure monsters", s.LureEnabled, v => s.LureEnabled = v),
            Number("Walk X spaces, then back to the center", s.LureWalkDistance, 0, 300, v => s.LureWalkDistance = v),
            Row("Cast skill", shout, 80),
            Switch("Buff only after the lure script ends", s.LureBuffOnlyAfterScript, v => s.LureBuffOnlyAfterScript = v),
            Number("X ms delay at the edges", s.LureEdgeDelay, 0, 60000, v => s.LureEdgeDelay = v),
            Number("X ms delay at the center", s.LureCenterDelay, 0, 60000, v => s.LureCenterDelay = v),
            Number("X ms half delay", s.LureHalfDelay, 0, 60000, v => s.LureHalfDelay = v),
            Row("Script", script, 80),
            SocialUi.Buttons(SocialUi.Button("Choose script", 110, ChooseScript), SocialUi.Button("Clear", 60, ClearScript)),
            SocialUi.Hint("Luring is for parties: one character walks out (a random way, or along the script's points), casts the skill (Howling Shout ...) at the edge and comes back. Monsters near are attacked as usual (turn on Don't attack monsters to only lure).", 76)), 0, 0);
        grid.Controls.Add(Box("Stop luring",
            Number("If more than X party members are dead (0 = off)", s.LureStopIfDead, 0, 8, v => s.LureStopIfDead = v),
            Number("If more than X party giants in the area (0 = off)", s.LureStopIfPartyGiants, 0, 50, v => s.LureStopIfPartyGiants = v),
            Number("If more than X party champions in the area (0 = off)", s.LureStopIfPartyChampions, 0, 50, v => s.LureStopIfPartyChampions = v),
            Number("If more than X monsters in the area (0 = off)", s.LureStopIfMonsters, 0, 200, v => s.LureStopIfMonsters = v),
            Switch("If party members are not near the training area", s.LureStopIfPartyAway, v => s.LureStopIfPartyAway = v)), 1, 0);
        return grid;
    }

    private static string ScriptName(string path) => path.Length > 0 ? Path.GetFileName(path) : "(none: walk a random way)";

    // ------------------------------------------------------------------ Attack Log tab

    private Control AttackLogTab()
    {
        var list = SocialUi.List();
        void Refresh()
        {
            var log = _hunt()?.AttackLog ?? [];
            SocialUi.Fill(list, log.Count == 0 ? ["No player attacked you this session."]
                : [.. log.Select(e => string.Create(CultureInfo.CurrentCulture, $"{e.At:HH:mm:ss}  {e.Player}  {(e.Killed ? "killed you" : $"hit you for {e.Damage:N0}")}"))]);
        }
        _refresh.Add(Refresh);
        var box = SocialUi.Panel("Attack log");
        var inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(list);
        inner.Controls.Add(well);
        inner.Controls.Add(SocialUi.Buttons(SocialUi.Button("Clear", 70, () => { _hunt()?.ClearAttackLog(); Refresh(); })));
        inner.Controls.Add(SocialUi.Hint("Every player who attacked or killed you (Job Cave, PKers), newest first.", 26));
        box.Controls.Add(inner);
        return box;
    }

    // ------------------------------------------------------------------ refresh

    public void RefreshView()
    {
        RefreshLists();
        var hunt = _hunt();
        _status.Text = hunt is null ? "Not connected." : (hunt.IsRunning ? "Running. " : "") + hunt.Status;
    }

    private void RefreshLists()
    {
        foreach (var refresh in _refresh)
            refresh();
    }
}
