using System.Drawing.Drawing2D;
using System.Globalization;
using NexBot.App.Theme;
using NexBot.Configuration;
using NexBot.Map;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// The character window: name, level and gauges, the stat block (STR, INT, attack, defense ...), the game-style skill
/// window (masteries and skills, with raise / learn), and the details (gold, SP, job, PvP, guild, location, pets).
/// </summary>
internal sealed class CharacterPage : UserControl
{
    private readonly Font _nameFont = Title(16f);
    private readonly Label _name = Sro.Label("Not in game", SroTheme.Value, null);
    private readonly Label _subtitle = Sro.Label("Log in to see the character.", Muted, SroTheme.Body);
    private readonly SroBar _hp = new() { Kind = SroBarKind.Hp, Dock = DockStyle.Top, Height = 20 };
    private readonly SroBar _mp = new() { Kind = SroBarKind.Mp, Dock = DockStyle.Top, Height = 20 };
    private readonly SroBar _exp = new() { Kind = SroBarKind.Exp, Dock = DockStyle.Top, Height = 20 };
    private readonly SroBar _spExp = new() { Kind = SroBarKind.Exp, Dock = DockStyle.Top, Height = 14 };
    private readonly Dictionary<string, Label> _stats = [];
    private readonly Dictionary<string, Label> _info = [];
    private readonly SkillWindowView _skills;
    private readonly PetList _pets;
    private readonly BuffBar _buffs;
    private readonly PortraitBox _portrait;
    private readonly SroSwitch _autoMasteries = new() { Text = "Mastery" };
    private readonly SroSwitch _autoSkills = new() { Text = "Skills" };
    private readonly Label _autoStatus = Sro.Label("Auto level is off.", Muted, SroTheme.Small);
    private readonly SroInput _gap = new() { Width = 38 };
    private readonly SroInput _keepSp = new() { Width = 82 };
    private AutoLevelSettings? _auto;
    private Action? _autoChanged;
    private SkillWindow? _window;
    private bool _loadingAuto;

    public CharacterPage(Func<NexBot.Sessions.SroClient?> game, IconCache icons)
    {
        BackColor = Bg;
        Dock = DockStyle.Fill;
        _skills = new SkillWindowView(game, icons) { Dock = DockStyle.Fill };
        _skills.AutoChanged += (_, _) =>
        {
            // ALL SKILLS on a mastery needs the Skills switch on.
            if (_auto is { AllSkillMasteries.Count: > 0 } && !_autoSkills.Checked)
                _autoSkills.Checked = true;
            _autoChanged?.Invoke();
        };
        _pets = new PetList(icons) { Dock = DockStyle.Fill };
        _buffs = new BuffBar(icons);
        _portrait = new PortraitBox(icons) { Dock = DockStyle.Left, Width = 84 };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Bg, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 55));

        // Character + stats -------------------------------------------------------------------
        var main = Panel("Character");
        var stack = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _name.Font = _nameFont;
        _name.UseCompatibleTextRendering = true; // GDI+: the embedded Cinzel font
        _name.AutoSize = false;
        _name.Height = 34;
        _name.AutoEllipsis = true;
        _subtitle.AutoSize = false;
        _subtitle.Height = 26;
        // Header: the round face portrait, with the name and level line beside it.
        var header = new Panel { Height = 84, BackColor = Color.Transparent };
        var names = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(8, 12, 0, 0) };
        _subtitle.Dock = DockStyle.Top;
        _name.Dock = DockStyle.Top;
        names.Controls.Add(_subtitle);
        names.Controls.Add(_name);
        header.Controls.Add(names);
        header.Controls.Add(_portrait);
        var statTable = Table(_stats, ("Strength", "STR"), ("Intellect", "INT"), ("Stat points", "Points"), ("Berserk", "Berserk"),
            ("Phy. attack", "PhyAtk"), ("Mag. attack", "MagAtk"), ("Phy. defense", "PhyDef"), ("Mag. defense", "MagDef"),
            ("Hit rate", "Hit"), ("Parry rate", "Parry"));
        var spLabel = Sro.Label("SKILL EXP", Muted, SroTheme.Tiny);
        spLabel.AutoSize = false;
        spLabel.Height = 16;
        // Dock order: added last = top.
        foreach (var c in new Control[] { statTable, new SroParchment { Text = "Stats" }, Gap(6), _spExp, spLabel, Gap(4), _exp, _mp, _hp, Gap(6), _buffs, Gap(2), header })
        {
            c.Dock = DockStyle.Top;
            stack.Controls.Add(c);
        }
        main.Controls.Add(stack);
        grid.Controls.Add(main, 0, 0);
        grid.SetRowSpan(main, 2);

        // Skill window (masteries + skills) ---------------------------------------------------
        var skills = Panel("Skills");
        skills.Controls.Add(_skills);
        _autoStatus.AutoSize = false;
        _autoStatus.AutoEllipsis = true;
        _autoStatus.Dock = DockStyle.Bottom;
        _autoStatus.Height = 20;
        skills.Controls.Add(_autoStatus);
        skills.Controls.Add(AutoBar());
        grid.Controls.Add(skills, 1, 0);
        grid.SetRowSpan(skills, 2);

        // Details + pets --------------------------------------------------------------------
        var details = Panel("Details");
        details.Controls.Add(Table(_info, ("Gold", "Gold"), ("Skill points", "SP"), ("Job", "Job"), ("PvP cape", "Cape"),
            ("Murders", "PK"), ("Guild", "Guild"), ("Location", "Location"), ("Riding", "Riding"), ("Unique ID", "Id"), ("Account", "GM")));
        grid.Controls.Add(details, 2, 0);
        var pets = Panel("Pets");
        pets.Controls.Add(_pets);
        grid.Controls.Add(pets, 2, 1);

        Controls.Add(grid);
        SetSheet(null);
    }

    /// <summary>The auto-level bar under the skill window: on / off switches, mastery gap, skill points to keep.</summary>
    private FlowLayoutPanel AutoBar()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        static Label Caption(string text)
        {
            var label = Sro.Label(text, Muted, SroTheme.Small);
            label.Margin = new Padding(6, 6, 4, 0);
            return label;
        }
        _gap.Margin = _keepSp.Margin = new Padding(0, 0, 4, 0);
        _gap.Box.Text = "0";
        _keepSp.Box.Text = "0";
        bar.Controls.AddRange([Caption("AUTO"), _autoMasteries, _autoSkills, Caption("Gap"), _gap, Caption("Keep SP"), _keepSp]);
        _autoMasteries.Enabled = _autoSkills.Enabled = _gap.Enabled = _keepSp.Enabled = false;

        _autoMasteries.CheckedChanged += (_, _) => OnAutoSwitch(masteries: true);
        _autoSkills.CheckedChanged += (_, _) => OnAutoSwitch(masteries: false);
        _gap.Box.TextChanged += (_, _) => OnAutoNumbers();
        _keepSp.Box.TextChanged += (_, _) => OnAutoNumbers();
        return bar;
    }

    /// <summary>Connects the auto-level settings; <paramref name="changed"/> runs after every change (save, apply).</summary>
    public void BindAuto(AutoLevelSettings settings, Action changed)
    {
        _auto = settings;
        _autoChanged = changed;
        _loadingAuto = true;
        _autoMasteries.Checked = settings.Masteries;
        _autoSkills.Checked = settings.Skills;
        _gap.Box.Text = settings.MasteryGap.ToString(CultureInfo.InvariantCulture);
        _keepSp.Box.Text = settings.KeepSp.ToString(CultureInfo.InvariantCulture);
        _loadingAuto = false;
        _autoMasteries.Enabled = _autoSkills.Enabled = _gap.Enabled = _keepSp.Enabled = true;
        _skills.Auto = settings;
    }

    /// <summary>The trainer's status: what it learns now, or why it waits.</summary>
    public void ShowAutoStatus(string text)
    {
        _autoStatus.Text = text;
        _autoStatus.ForeColor = text.StartsWith("Learning", StringComparison.Ordinal) ? Gold : Muted;
    }

    private void OnAutoSwitch(bool masteries)
    {
        if (_loadingAuto || _auto is null)
            return;
        _auto.Masteries = _autoMasteries.Checked;
        _auto.Skills = _autoSkills.Checked;
        // Nothing chosen yet: start with what the character already trains.
        if (masteries && _auto.Masteries && _auto.MasteryIds.Count == 0 && _window is not null)
            _auto.MasteryIds.AddRange(_window.Tabs.SelectMany(t => t.Masteries).Where(m => m.Level > 0).Select(m => m.Id));
        if (!masteries && _auto.Skills && _auto.SkillGroups.Count == 0 && _window is not null)
            _auto.SkillGroups.AddRange(_window.Tabs.SelectMany(t => t.Masteries).SelectMany(m => m.Lines).SelectMany(l => l.Slots)
                .Where(s => s is { Level: > 0 }).Select(s => s!.Group).Distinct());
        _skills.Invalidate();
        _autoChanged?.Invoke();
    }

    private void OnAutoNumbers()
    {
        if (_loadingAuto || _auto is null)
            return;
        if (int.TryParse(_gap.Box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int gap))
            _auto.MasteryGap = Math.Min(gap, 200);
        if (uint.TryParse(_keepSp.Box.Text.Replace(",", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out uint keep))
            _auto.KeepSp = keep;
        _autoChanged?.Invoke();
    }

    private static SroPanel Panel(string title) => new() { Title = title, Dock = DockStyle.Fill, Margin = new Padding(5) };

    private static Panel Gap(int height) => new() { Height = height, BackColor = Color.Transparent };

    /// <summary>Two columns: caption, value; the value labels go into <paramref name="labels"/>.</summary>
    private static TableLayoutPanel Table(Dictionary<string, Label> labels, params (string Caption, string Key)[] rows)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent, Padding = new Padding(0, 2, 0, 0) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (caption, key) in rows)
        {
            var name = Sro.Label(caption);
            name.Margin = new Padding(0, 3, 0, 3);
            var value = Sro.Label("-", SroTheme.Value, SroTheme.Body);
            value.Margin = new Padding(0, 2, 0, 2);
            value.AutoEllipsis = true;
            value.Dock = DockStyle.Fill;
            labels[key] = value;
            table.Controls.Add(name);
            table.Controls.Add(value);
        }
        return table;
    }

    /// <summary>Shows the skill window (null = not in game).</summary>
    public void SetSkills(SkillWindow? window)
    {
        _window = window;
        _skills.SetWindow(window);
    }

    /// <summary>A portrait shown while not in game (screenshots for the guide only).</summary>
    internal string? DemoPortrait { get; set; }

    public void SetSheet(CharacterSheet? s)
    {
        var inv = CultureInfo.CurrentCulture;
        if (s is null)
        {
            _name.Text = "Not in game";
            _subtitle.Text = "Log in to see the character.";
            _portrait.Portrait = DemoPortrait;
            _hp.SetValue(0, 1, "HP");
            _mp.SetValue(0, 1, "MP");
            _exp.SetValue(0, 100, "EXP");
            _spExp.SetValue(0, 100, "");
            foreach (var label in _stats.Values.Concat(_info.Values))
                label.Text = "-";
            _pets.SetItems([]);
            _buffs.SetBuffs([]);
            return;
        }

        _name.Text = s.Name;
        _portrait.Portrait = s.Portrait;
        _subtitle.Text = $"Lv. {s.Level}  ·  {s.Race}{(s.IsGameMaster ? "  ·  Game Master" : "")}{(s.Dead ? "  ·  Dead" : "")}";
        _subtitle.ForeColor = s.Dead ? Bad : Muted;
        _hp.SetValue(s.Hp, s.HpMax, string.Create(inv, $"HP  {s.Hp:N0} / {s.HpMax:N0}"));
        _mp.SetValue(s.Mp, s.MpMax, string.Create(inv, $"MP  {s.Mp:N0} / {s.MpMax:N0}"));
        _exp.SetValue(s.ExpPercent, 100, string.Create(inv, $"EXP  {s.ExpPercent:0.00} %"));
        _spExp.SetValue(s.SpExpPercent, 100, string.Create(inv, $"{s.SpExpPercent:0.#} %"));

        _stats["STR"].Text = s.Strength.ToString(inv);
        _stats["INT"].Text = s.Intellect.ToString(inv);
        _stats["Points"].Text = s.StatPoints.ToString(inv);
        _stats["Points"].ForeColor = s.StatPoints > 0 ? Good : SroTheme.Value;
        _stats["Berserk"].Text = $"{s.BerserkPoints} / 5";
        _stats["PhyAtk"].Text = string.Create(inv, $"{s.PhysicalAttackMin:N0} ~ {s.PhysicalAttackMax:N0}");
        _stats["MagAtk"].Text = string.Create(inv, $"{s.MagicalAttackMin:N0} ~ {s.MagicalAttackMax:N0}");
        _stats["PhyDef"].Text = s.PhysicalDefense.ToString("N0", inv);
        _stats["MagDef"].Text = s.MagicalDefense.ToString("N0", inv);
        _stats["Hit"].Text = s.HitRate.ToString("N0", inv);
        _stats["Parry"].Text = s.ParryRate.ToString("N0", inv);

        _info["Gold"].Text = s.Gold.ToString("N0", inv);
        _info["SP"].Text = s.Sp.ToString("N0", inv);
        _info["Job"].Text = s.JobType == "None" ? "None" : $"{s.JobType} (Lv. {s.JobLevel}){(s.JobName.Length > 0 ? " · " + s.JobName : "")}";
        _info["Cape"].Text = s.PvpCape;
        _info["PK"].Text = string.Create(inv, $"{s.PkDaily} today · {s.PkTotal} total{(s.PkPenalty > 0 ? $" · penalty {s.PkPenalty:N0}" : "")}");
        _info["Guild"].Text = s.Guild.Length > 0 ? s.Guild : "None";
        _info["Location"].Text = s.Location;
        _info["Riding"].Text = s.Riding ? "Yes" : "No";
        _info["Id"].Text = s.UniqueId.ToString(CultureInfo.InvariantCulture);
        _info["GM"].Text = s.IsGameMaster ? "Game Master" : "Player";

        _pets.SetItems(s.Pets);
        _buffs.SetBuffs(s.Buffs ?? []);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _nameFont.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Summoned pets and mounts: name, kind, HP gauge (and hunger for attack pets).</summary>
internal sealed class PetList : Control
{
    private const int Portrait = 52;
    private readonly IconCache _icons;
    private IReadOnlyList<PetView> _items = [];

    public PetList(IconCache icons)
    {
        _icons = icons;
        _icons.Loaded += Invalidate;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public void SetItems(IReadOnlyList<PetView> items)
    {
        _items = items;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, "No pet or mount summoned.", SroTheme.Small, ClientRectangle, Muted, TextFormatFlags.Left | TextFormatFlags.Top);
            return;
        }
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int y = 0;
        foreach (var p in _items)
        {
            // Portrait from Media.pk2 (icon\cos\...), framed like an item slot; gold frame for the mount being ridden.
            var frame = new Rectangle(0, y, Portrait, Portrait);
            using (var well = Vertical(frame, Hex("#070605"), Hex("#16140f")))
                g.FillRectangle(well, frame);
            if (_icons.Get(p.Icon) is { } portrait)
                g.DrawImage(portrait, Rectangle.Inflate(frame, -2, -2));
            else
                TextRenderer.DrawText(g, p.Kind, SroTheme.Tiny, frame, Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            StrokeRect(g, p.Riding ? Gold : Hex("#5a4f38"), frame);

            int x = Portrait + 10, w = Width - x - 1;
            string title = p.Name.Length > 0 ? p.Name : p.Model.Length > 0 ? p.Model : p.Kind;
            TextRenderer.DrawText(g, title, SroTheme.BodyBold, new Rectangle(x, y, w, 18), SroTheme.Text, TextFormatFlags.EndEllipsis);
            // Second line: kind, level, riding, and the pet type when the player renamed it.
            string kind = (p.Kind + (p.Level is { } l ? $" · Lv. {l}" : "") + (p.Riding ? " · riding" : "")).ToUpperInvariant()
                          + (p.Model.Length > 0 && p.Model != title ? "  ·  " + p.Model : "");
            TextRenderer.DrawText(g, kind, SroTheme.Tiny, new Rectangle(x, y + 19, w, 15), p.Riding ? Gold : Muted, TextFormatFlags.EndEllipsis);
            int bar = y + 36;
            Gauge(g, new Rectangle(x, bar, w, 12), p.HpMax == 0 ? 0 : p.Hp / (double)p.HpMax, Hex("#ff8a7a"), Hex("#7a1414"),
                p.HpMax > 0 ? string.Create(CultureInfo.CurrentCulture, $"{p.Hp:N0} / {p.HpMax:N0}") : "");
            bar += 15;
            if (p.HgpPercent is { } hgp)
            {
                Gauge(g, new Rectangle(x, bar, w, 10), hgp / 100.0, Hex("#b8f08a"), Hex("#2f5a14"), $"hunger {hgp} %");
                bar += 13;
            }
            y = Math.Max(bar, y + Portrait) + 12;
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

    private static void Gauge(Graphics g, Rectangle bar, double ratio, Color top, Color bottom, string text)
    {
        using (var back = new SolidBrush(Hex("#0a0908")))
            g.FillRectangle(back, bar);
        int fill = (int)Math.Round((bar.Width - 2) * Math.Clamp(ratio, 0, 1));
        if (fill > 0)
        {
            var f = new Rectangle(bar.X + 1, bar.Y + 1, fill, bar.Height - 2);
            using var b = Vertical(f, top, bottom);
            g.FillRectangle(b, f);
        }
        StrokeRect(g, Hex("#3a352a"), bar);
        if (text.Length > 0)
            TextRenderer.DrawText(g, text, SroTheme.Tiny, bar, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
