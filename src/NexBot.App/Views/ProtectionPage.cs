using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>Protection (phBot's Protection tab): potions, return to town, dead, pet return, stats, misc, berserk.</summary>
internal sealed class ProtectionPage : UserControl
{
    private readonly Func<ProtectionManager?> _manager;
    private readonly Func<IGameData?> _data;
    private readonly Panel _potions = Scroller(), _scrolls = Scroller(), _supplies = Scroller(), _time = Scroller(),
        _dead = Scroller(), _misc = Scroller(), _berserk = Scroller(), _monsters = Scroller();
    private readonly SroTabs _tabs = new() { Dock = DockStyle.Fill };
    private Action _changed = () => { };

    public ProtectionPage(Func<ProtectionManager?> manager, Func<IGameData?> data)
    {
        _manager = manager;
        _data = data;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        Padding = new Padding(0, 4, 0, 0);
        _tabs.Add("Potions", Pair(("Potions and pills", _potions), ("Scrolls and stats", _scrolls)));
        _tabs.Add("Return to town", Pair(("Supplies and durability", _supplies), ("Time and other", _time)));
        _tabs.Add("Dead and pet", Pair(("Dead", _dead), ("Pet return and misc", _misc)));
        _tabs.Add("Berserk and monsters", Pair(("Berserk", _berserk), ("Monster preferences", _monsters)));
        Controls.Add(_tabs);
    }

    /// <summary>Shows a tab (guide screenshots).</summary>
    public void SelectTab(int index) => _tabs.SelectedIndex = index;

    /// <summary>Two framed columns, each scrolling on its own.</summary>
    private static Control Pair((string Title, Panel Body) left, (string Title, Panel Body) right)
    {
        var grid = SocialUi.Columns(2);
        int column = 0;
        foreach (var (title, body) in new[] { left, right })
        {
            var panel = SocialUi.Panel(title);
            panel.Controls.Add(body);
            grid.Controls.Add(panel, column++, 0);
        }
        return grid;
    }

    private static Panel Scroller() => new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(0, 0, 6, 0) };

    public void Bind(ProtectionSettings s, Action changed)
    {
        _changed = changed;
        foreach (var p in new[] { _potions, _scrolls, _supplies, _time, _dead, _misc, _berserk, _monsters })
            p.Controls.Clear();

        SocialUi.Stack(_potions,
            SocialUi.Caption("USE WHEN BELOW THIS PERCENT"),
            Row("HP potion", s.HpPotion, "%", 99),
            Row("MP potion", s.MpPotion, "%", 99),
            Row("Vigor potion (HP)", s.VigorHp, "%", 99),
            Row("Vigor potion (MP)", s.VigorMp, "%", 99),
            SocialUi.Caption("BAD STATUS (FROZEN, BURNT, POISONED ...)"),
            SocialUi.Switch("Universal pill", s.UniversalPill, v => s.UniversalPill = v, changed),
            SocialUi.Switch("Abnormal state (purification) pill", s.PurificationPill, v => s.PurificationPill = v, changed),
            SocialUi.Field("Delay between two potions (ms)", s.PotionDelayMs.ToString(CultureInfo.CurrentCulture), 70,
                v => s.PotionDelayMs = SocialUi.Int(v, 200, 10000), changed),
            SocialUi.Hint("Potions and pills are used all the time the character is in game, also clientless. When the server says a potion's cooldown is not over, the bot waits a little longer between potions by itself.", 50));

        SocialUi.Stack(_scrolls,
            SocialUi.Caption("STATS"),
            SocialUi.Switch("Add stat points automatically", s.AutoStats, v => s.AutoStats = v, changed),
            StatSplit(s),
            SocialUi.Buttons(
                SocialUi.Button("+1 STR", 80, () => AddStat(true)),
                SocialUi.Button("+1 INT", 80, () => AddStat(false))),
            SocialUi.Caption("SCROLLS (USED AGAIN WHEN THEIR BUFF IS GONE)"),
            SocialUi.Switch("While botting", s.ScrollsWhileBotting, v => s.ScrollsWhileBotting = v, changed),
            SocialUi.Switch("While not botting", s.ScrollsWhenNotBotting, v => s.ScrollsWhenNotBotting = v, changed),
            SocialUi.Hint("Parts of scroll names, e.g. speed, damage increase, damage absorption, HP+, hit, evasion.", 34),
            new NameListEditor(s.Scrolls, changed) { Dock = DockStyle.Top, Height = 150 });

        SocialUi.Stack(_supplies,
            SocialUi.Hint("Uses a return scroll and stops the bot. Checked while the bot is busy (script, trade, hunting) and, with the switch below, whenever you are outside a town.", 50),
            SocialUi.Switch("Also check when not botting (outside towns)", s.ReturnOutsideTown, v => s.ReturnOutsideTown = v, changed),
            SocialUi.Buttons(SocialUi.Button("Check now", 110, CheckNow)),
            SocialUi.Caption("SUPPLIES"),
            Row("HP potions left ≤", s.HpPotionsLeft, "", 9999),
            Row("MP potions left ≤", s.MpPotionsLeft, "", 9999),
            SocialUi.Switch("Out of universal pills", s.OutOfUniversalPills, v => s.OutOfUniversalPills = v, changed),
            SocialUi.Switch("Out of vigor potions", s.OutOfVigors, v => s.OutOfVigors = v, changed),
            SocialUi.Switch("Out of arrows / bolts (bow or crossbow)", s.OutOfArrows, v => s.OutOfArrows = v, changed),
            SocialUi.Switch("Out of speed scrolls", s.OutOfSpeedScrolls, v => s.OutOfSpeedScrolls = v, changed),
            SocialUi.Switch("Out of Energy of Life", s.OutOfEnergyOfLife, v => s.OutOfEnergyOfLife = v, changed),
            Row("Free inventory slots ≤", s.InventoryFull, "", 50),
            SocialUi.Caption("DURABILITY BELOW"),
            Row("Weapon", s.WeaponDurability, "", 999),
            Row("Shield", s.ShieldDurability, "", 999),
            Row("Any armor piece", s.ArmorDurability, "", 999),
            SocialUi.Switch("Change the weapon if broken", s.ChangeWeaponIfBroken, v => s.ChangeWeaponIfBroken = v, changed));

        SocialUi.Stack(_time,
            SocialUi.Caption("TIME"),
            Row("Minutes before the next hour", s.BeforeNextHour, "", 59),
            Row("Every (minutes)", s.EveryMinutes, "", 1440),
            TimeRow(s),
            Row("Return and disconnect every (minutes)", s.ReturnDisconnectMinutes, "", 10000),
            SocialUi.Caption("OTHER"),
            Row("Fewer players in the party than", s.PartyBelow, "", 8),
            Row("Not attacked for (minutes)", s.NotAttackedMinutes, "", 600),
            SocialUi.Switch("A unique spawns near you", s.UniqueNear, v => s.UniqueNear = v, changed),
            SocialUi.Switch("The job transport dies", s.TransportDied, v => s.TransportDied = v, changed),
            SocialUi.Switch("Pink (assaulter) status", s.PinkStatus, v => s.PinkStatus = v, changed));

        SocialUi.Stack(_dead,
            SocialUi.Caption("DEAD"),
            Row("Revive in town after (minutes)", s.DeadReturn, "", 60),
            SocialUi.Switch("Right away when dead outside the training area", s.DeadOutsideAreaNow, v => s.DeadOutsideAreaNow = v, changed),
            SocialUi.Switch("Use a resurrection scroll instead", s.UseResurrectionScroll, v => s.UseResurrectionScroll = v, changed),
            Row("Stop the bot after deaths >", s.StopAfterDeaths, "", 100));

        SocialUi.Stack(_misc,
            SocialUi.Caption("PET RETURN: GO BACK WHEN OUT OF"),
            SocialUi.Switch("Pet recovery kits", s.PetRecoveryKits, v => s.PetRecoveryKits = v, changed),
            SocialUi.Switch("Pet revive (grass of life)", s.PetRevive, v => s.PetRevive = v, changed),
            SocialUi.Switch("Pet food (HGP potions)", s.PetFeed, v => s.PetFeed = v, changed),
            SocialUi.Switch("Pet abnormal state potions", s.PetAbnormal, v => s.PetAbnormal = v, changed),
            SocialUi.Caption("MISC"),
            SocialUi.Switch("Disconnect if a GM spawns", s.DisconnectOnGm, v => s.DisconnectOnGm = v, changed),
            SocialUi.Switch("Disconnect if a player attacks you while botting", s.DisconnectWhenAttackedByPlayer, v => s.DisconnectWhenAttackedByPlayer = v, changed),
            SocialUi.Switch("Disconnect on murder status", s.DisconnectOnMurderStatus, v => s.DisconnectOnMurderStatus = v, changed),
            Row("Disconnect after (minutes in game)", s.DisconnectEveryMinutes, "", 10000),
            SocialUi.Switch("Re-spawn the job transport if it dies", s.RespawnTransport, v => s.RespawnTransport = v, changed),
            SocialUi.Switch("Stop if a thief player is in town", s.StopIfThiefInTown, v => s.StopIfThiefInTown = v, changed));

        SocialUi.Stack(_berserk,
            SocialUi.Caption("BERSERK (WHEN THE GAUGE IS FULL)"),
            BerserkTypeRow(s),
            Row("When more monsters attack than", s.BerserkWhenMonstersAttack, "", 50),
            SocialUi.Switch("While walking the script", s.BerserkWhileScript, v => s.BerserkWhileScript = v, changed));

        SocialUi.Stack(_monsters,
            SocialUi.Caption("MONSTER PREFERENCES (USED BY THE HUNTING BOT)"),
            SocialUi.Switch("Switch monster based on position in the list", s.SwitchToPreferred, v => s.SwitchToPreferred = v, changed),
            Preferences(s));
    }

    private void CheckNow()
    {
        if (_manager() is not { } manager)
        {
            MessageBox.Show(this, "Start the bot and enter the game first.", "NexBot", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        MessageBox.Show(this, string.Join(Environment.NewLine, manager.Explain()), "Return to town: check now",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AddStat(bool strength)
    {
        if (_manager() is not { } manager)
        {
            MessageBox.Show(this, "Start the bot and enter the game first.", "NexBot", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        manager.AddStat(strength);
    }

    /// <summary>A switch with a number on its right (phBot's "[x] ... X" rows).</summary>
    private Panel Row(string text, ProtectionOption option, string unit, int max)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.Transparent };
        var toggle = new SroSwitch { Text = text, Checked = option.On, Dock = DockStyle.Fill };
        var input = new SroInput { Dock = DockStyle.Right, Width = 58, Text = option.Value.ToString(CultureInfo.CurrentCulture), Enabled = option.On };
        var label = Sro.Label(unit, SroTheme.Text, SroTheme.Small);
        label.AutoSize = false;
        label.Dock = DockStyle.Right;
        label.Width = unit.Length > 0 ? 22 : 0;
        label.TextAlign = ContentAlignment.MiddleLeft;
        toggle.CheckedChanged += (_, _) =>
        {
            option.On = toggle.Checked;
            input.Enabled = toggle.Checked;
            _changed();
        };
        input.Box.TextChanged += (_, _) =>
        {
            option.Value = SocialUi.Int(input.Text, 0, max);
            _changed();
        };
        row.Controls.Add(toggle);
        row.Controls.Add(input);
        row.Controls.Add(label);
        return row;
    }

    private Panel StatSplit(ProtectionSettings s)
    {
        string[] items = ["3 STR", "2 STR · 1 INT", "1 STR · 2 INT", "3 INT"];
        return ComboRow("Each level's 3 points", items, 3 - Math.Clamp(s.StrPerLevel, 0, 3), i => s.StrPerLevel = 3 - i, 130);
    }

    /// <summary>Berserk on monster types: a switch, then one toggle per type (several may be on).</summary>
    private Panel BerserkTypeRow(ProtectionSettings s)
    {
        var box = new Panel { Dock = DockStyle.Top, Height = 32 + 4 * 28 + 4, BackColor = Color.Transparent };
        var toggle = new SroSwitch { Text = "On these monster types", Checked = s.BerserkOnMonsterType, Dock = DockStyle.Top, Height = 30 };
        toggle.CheckedChanged += (_, _) => { s.BerserkOnMonsterType = toggle.Checked; _changed(); };
        var types = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 4 * 28 + 4, BackColor = Color.Transparent, Padding = new Padding(SroSwitch.SubOptionIndent, 0, 0, 0) };
        var chosen = s.BerserkMonsterType.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string type in MonsterPreferences.Types)
        {
            var one = new SroSwitch { Text = type, Checked = chosen.Contains(type), Width = 112, Height = 26, Margin = new Padding(0, 0, 2, 2) };
            one.CheckedChanged += (_, _) =>
            {
                if (one.Checked)
                    chosen.Add(type);
                else
                    chosen.Remove(type);
                s.BerserkMonsterType = string.Join(",", MonsterPreferences.Types.Where(chosen.Contains));
                _changed();
            };
            types.Controls.Add(one);
        }
        box.Controls.Add(types);
        box.Controls.Add(toggle);
        return box;
    }

    private Panel ComboRow(string text, string[] items, int index, Action<int> set, int width)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        var label = Sro.Label(text, SroTheme.Text, SroTheme.Small);
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        var combo = new SroCombo { Dock = DockStyle.Right, Width = width };
        combo.SetItems(items);
        combo.SelectedIndex = Math.Clamp(index, 0, items.Length - 1);
        combo.SelectedIndexChanged += (_, _) => { set(Math.Max(0, combo.SelectedIndex)); _changed(); };
        row.Controls.Add(label);
        row.Controls.Add(combo);
        return row;
    }

    private Panel TimeRow(ProtectionSettings s)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.Transparent };
        var toggle = new SroSwitch { Text = "Every day at (HH:mm)", Checked = s.DailyAt, Dock = DockStyle.Fill };
        var input = new SroInput { Dock = DockStyle.Right, Width = 58, Text = s.DailyTime, Enabled = s.DailyAt };
        toggle.CheckedChanged += (_, _) => { s.DailyAt = toggle.Checked; input.Enabled = toggle.Checked; _changed(); };
        input.Box.TextChanged += (_, _) =>
        {
            if (TimeOnly.TryParse(input.Text, CultureInfo.InvariantCulture, out var t))
            {
                s.DailyTime = t.ToString("HH:mm", CultureInfo.InvariantCulture);
                _changed();
            }
        };
        row.Controls.Add(toggle);
        row.Controls.Add(input);
        return row;
    }

    /// <summary>
    /// Monster preferences (phBot): add a type, or search a monster by name and add that exact one (right-click or
    /// double-click a result). The higher in the list, the more preferred; Up / Down change the order.
    /// </summary>
    private Panel Preferences(ProtectionSettings s)
    {
        var box = new Panel { Dock = DockStyle.Top, Height = 470, BackColor = Color.Transparent };
        var list = SocialUi.List();
        var results = SocialUi.List();
        var found = new List<(string ServerName, string Text)>();
        var mode = new SroCombo { Dock = DockStyle.Right, Width = 110 };
        mode.SetItems(["Type", "Name"]);
        var typeCombo = new SroCombo { Dock = DockStyle.Fill };
        typeCombo.SetItems(MonsterPreferences.Types);
        typeCombo.SelectedIndex = 0;
        var search = new SroInput { Dock = DockStyle.Fill };

        string Label(string entry)
        {
            if (MonsterPreferences.IsTypeEntry(entry))
                return "Type: " + entry;
            if (entry.StartsWith("MOB_", StringComparison.OrdinalIgnoreCase))
                return (_data()?.GetModel(entry)?.Name is { Length: > 0 } name ? name : entry) + "  ·  " + entry;
            return "Name contains: " + entry;
        }
        void Show(int select)
        {
            SocialUi.Fill(list, [.. s.MonsterPreferences.Select((e, i) => $"{i + 1}.  {Label(e)}")]);
            if (select >= 0 && select < list.Items.Count)
                list.SelectedIndex = select;
        }
        void Add(string entry)
        {
            if (entry.Length == 0 || s.MonsterPreferences.Contains(entry, StringComparer.OrdinalIgnoreCase))
                return;
            s.MonsterPreferences.Add(entry);
            Show(s.MonsterPreferences.Count - 1);
            _changed();
        }
        void Move(int by)
        {
            int i = list.SelectedIndex, j = i + by;
            if (i < 0 || j < 0 || j >= s.MonsterPreferences.Count)
                return;
            (s.MonsterPreferences[i], s.MonsterPreferences[j]) = (s.MonsterPreferences[j], s.MonsterPreferences[i]);
            Show(j);
            _changed();
        }
        void Remove()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= s.MonsterPreferences.Count)
                return;
            s.MonsterPreferences.RemoveAt(list.SelectedIndex);
            Show(-1);
            _changed();
        }
        void Search()
        {
            found.Clear();
            string text = search.Text.Trim();
            if (_data() is not { } data)
            {
                SocialUi.Fill(results, ["Start the bot once (Server page) so the game database is loaded."]);
                return;
            }
            if (text.Length < 2)
            {
                SocialUi.Fill(results, ["Type at least 2 letters of a monster name."]);
                return;
            }
            foreach (var (model, level) in data.SearchMonsters(text, 200))
                found.Add((model.ServerName, $"{(model.Name.Length > 0 ? model.Name : model.ServerName)}  ·  Lv. {level}  ·  {model.ServerName}"));
            SocialUi.Fill(results, found.Count == 0 ? ["No monster found."] : [.. found.Select(f => f.Text)]);
        }
        void AddResult()
        {
            if (results.SelectedIndex >= 0 && results.SelectedIndex < found.Count)
                Add(found[results.SelectedIndex].ServerName);
        }

        var resultMenu = new ContextMenuStrip { Renderer = new SroMenuRenderer(), ShowImageMargin = false, Font = SroTheme.Body };
        resultMenu.Items.Add("Add to the preferences", null, (_, _) => AddResult());
        results.ContextMenuStrip = resultMenu;
        results.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && results.IndexFromPoint(e.Location) is var i and >= 0)
                results.SelectedIndex = i;
        };
        results.DoubleClick += (_, _) => AddResult();
        var listMenu = new ContextMenuStrip { Renderer = new SroMenuRenderer(), ShowImageMargin = false, Font = SroTheme.Body };
        listMenu.Items.Add("Move up", null, (_, _) => Move(-1));
        listMenu.Items.Add("Move down", null, (_, _) => Move(1));
        listMenu.Items.Add("Remove", null, (_, _) => Remove());
        list.ContextMenuStrip = listMenu;
        list.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && list.IndexFromPoint(e.Location) is var i and >= 0)
                list.SelectedIndex = i;
        };
        search.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Search();
            }
        };

        Panel RowOf(Control fill, params SroButton[] buttons)
        {
            var row = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent, Padding = new Padding(0, 2, 0, 2) };
            row.Controls.Add(fill);
            foreach (var b in buttons)
            {
                b.Dock = DockStyle.Right;
                b.Height = 28;
                row.Controls.Add(b);
            }
            return row;
        }
        Panel Well(ListBox l, int height)
        {
            var well = new Panel { Dock = DockStyle.Top, Height = height, Padding = new Padding(4), BackColor = InputBack };
            well.Controls.Add(l);
            return well;
        }
        var modeLabel = Sro.Label("Add a monster by", SroTheme.Text, SroTheme.Small);
        modeLabel.AutoSize = false;
        modeLabel.Dock = DockStyle.Fill;
        modeLabel.TextAlign = ContentAlignment.MiddleLeft;
        var modeRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        modeRow.Controls.Add(modeLabel);
        modeRow.Controls.Add(mode);
        var typeRow = RowOf(typeCombo, SocialUi.Button("Add", 56, () => Add(MonsterPreferences.Types[Math.Max(0, typeCombo.SelectedIndex)])));
        var searchRow = RowOf(search, SocialUi.Button("Search", 70, Search));
        var resultsWell = Well(results, 120);
        var resultsHint = SocialUi.Hint("Right-click (or double-click) a monster to add that exact monster.", 22);

        SocialUi.Stack(box,
            SocialUi.Hint("The higher a type or monster is in the list, the more the hunting bot prefers it. Type: General, Champion, Giant ... Name: an exact monster (Mangyang, Tiger ...).", 46),
            modeRow, typeRow, searchRow, resultsHint, resultsWell,
            SocialUi.Caption("YOUR PREFERENCES (TOP = MOST PREFERRED)"),
            Well(list, 130),
            SocialUi.Buttons(SocialUi.Button("Remove", 70, Remove), SocialUi.Button("Up", 44, () => Move(-1)), SocialUi.Button("Down", 56, () => Move(1))));

        void Mode()
        {
            bool byName = mode.SelectedIndex == 1;
            typeRow.Visible = !byName;
            searchRow.Visible = resultsHint.Visible = resultsWell.Visible = byName;
            box.Height = byName ? 480 : 480 - 34 - 22 - 120;
        }
        mode.SelectedIndexChanged += (_, _) => Mode();
        mode.SelectedIndex = 0;
        Mode();
        Show(-1);
        return box;
    }
}
