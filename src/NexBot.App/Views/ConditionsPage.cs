using System.Globalization;
using NexBot.App.Theme;
using NexBot.Configuration;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>Conditions (phBot's Conditions tab): rules made of "If" checks and "Then" actions.</summary>
internal sealed class ConditionsPage : UserControl
{
    private readonly ListBox _rules = SocialUi.List();
    private readonly ListBox _ifs = SocialUi.List();
    private readonly ListBox _thens = SocialUi.List();
    private readonly SroInput _name = new() { Dock = DockStyle.Right, Width = 170 };
    private readonly SroInput _repeat = new() { Dock = DockStyle.Right, Width = 60 };
    private readonly SroSwitch _enabled = new() { Text = "Rule on", Dock = DockStyle.Top, Height = 28 };
    private readonly SroCombo _ifType = new() { Dock = DockStyle.Top };
    private readonly SroCombo _thenType = new() { Dock = DockStyle.Top };
    private readonly (Label Label, SroInput Input) _ifNumber = Labeled(70), _ifText = Labeled(170), _thenNumber = Labeled(70), _thenText = Labeled(170);
    private readonly Panel _ifNumberRow, _ifTextRow, _thenNumberRow, _thenTextRow;
    private ConditionSettings _settings = new();
    private Action _changed = () => { };
    private bool _loading;

    private static readonly IfType[] IfTypes = Enum.GetValues<IfType>();
    private static readonly ThenType[] ThenTypes = Enum.GetValues<ThenType>();

    public ConditionsPage()
    {
        BackColor = Bg;
        Dock = DockStyle.Fill;
        _ifNumberRow = Row(_ifNumber);
        _ifTextRow = Row(_ifText);
        _thenNumberRow = Row(_thenNumber);
        _thenTextRow = Row(_thenText);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Rules
        var rules = SocialUi.Panel("Rules");
        rules.Controls.Add(Well(_rules));
        SocialUi.Stack(rules,
            SocialUi.Hint("Rules run all the time the character is in game: started, stopped, tracing or stalling. A rule acts when all its If checks become true.", 52),
            SocialUi.Buttons(SocialUi.Button("New rule", 90, NewRule), SocialUi.Button("Delete", 76, DeleteRule)),
            NamedRow("Name", _name),
            _enabled,
            NamedRow("Repeat every (s, 0 = once)", _repeat),
            SocialUi.Caption("RULES"));
        _rules.SelectedIndexChanged += (_, _) => ShowRule();
        _name.Box.TextChanged += (_, _) =>
        {
            if (_loading || Rule() is not { } r)
                return;
            r.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : "Rule";
            _changed();
            ShowRules();
        };
        _repeat.Box.TextChanged += (_, _) =>
        {
            if (_loading || Rule() is not { } r)
                return;
            r.RepeatSeconds = SocialUi.Int(_repeat.Text, 0, 86400);
            _changed();
        };
        _enabled.CheckedChanged += (_, _) =>
        {
            if (_loading || Rule() is not { } r)
                return;
            r.Enabled = _enabled.Checked;
            _changed();
            ShowRules();
        };
        grid.Controls.Add(rules, 0, 0);

        // If
        _ifType.SetItems(IfTypes.Select(t => Split(t.ToString())));
        _ifType.SelectedIndex = 0;
        _ifType.SelectedIndexChanged += (_, _) => ShowFields();
        var ifs = SocialUi.Panel("If (all of these)");
        ifs.Controls.Add(Well(_ifs));
        SocialUi.Stack(ifs, _ifType, _ifNumberRow, _ifTextRow,
            SocialUi.Buttons(SocialUi.Button("Add", 70, AddIf), SocialUi.Button("Remove", 80, () => RemoveAt(_ifs, Rule()?.Ifs))),
            SocialUi.Caption("CHECKS"));
        grid.Controls.Add(ifs, 1, 0);

        // Then
        _thenType.SetItems(ThenTypes.Select(t => Split(t.ToString())));
        _thenType.SelectedIndex = 0;
        _thenType.SelectedIndexChanged += (_, _) => ShowFields();
        var thens = SocialUi.Panel("Then (in this order)");
        thens.Controls.Add(Well(_thens));
        SocialUi.Stack(thens, _thenType, _thenNumberRow, _thenTextRow,
            SocialUi.Buttons(SocialUi.Button("Add", 70, AddThen), SocialUi.Button("Remove", 80, () => RemoveAt(_thens, Rule()?.Thens)),
                SocialUi.Button("Up", 50, MoveUp)),
            SocialUi.Caption("ACTIONS"));
        grid.Controls.Add(thens, 2, 0);
        Controls.Add(grid);
        ShowFields();
    }

    private static string Split(string pascal) =>
        string.Concat(pascal.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : ch.ToString()));

    private static Panel Well(Control list)
    {
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(list);
        return well;
    }

    private static (Label, SroInput) Labeled(int width)
    {
        var label = Sro.Label("", SroTheme.Text, SroTheme.Small);
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        return (label, new SroInput { Dock = DockStyle.Right, Width = width });
    }

    private static Panel Row((Label Label, SroInput Input) field)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        row.Controls.Add(field.Label);
        row.Controls.Add(field.Input);
        return row;
    }

    private static Panel NamedRow(string text, SroInput input)
    {
        var label = Sro.Label(text, SroTheme.Text, SroTheme.Small);
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        var row = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
        row.Controls.Add(label);
        row.Controls.Add(input);
        return row;
    }

    private void ShowFields()
    {
        var (ifNumber, ifText) = ConditionSettings.Fields(IfTypes[Math.Max(0, _ifType.SelectedIndex)]);
        _ifNumber.Label.Text = ifNumber;
        _ifText.Label.Text = ifText;
        _ifNumberRow.Visible = ifNumber.Length > 0;
        _ifTextRow.Visible = ifText.Length > 0;
        var (thenNumber, thenText) = ConditionSettings.Fields(ThenTypes[Math.Max(0, _thenType.SelectedIndex)]);
        _thenNumber.Label.Text = thenNumber;
        _thenText.Label.Text = thenText;
        _thenNumberRow.Visible = thenNumber.Length > 0;
        _thenTextRow.Visible = thenText.Length > 0;
    }

    public void Bind(ConditionSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        ShowRules();
        if (_rules.Items.Count > 0)
            _rules.SelectedIndex = 0;
    }

    private ConditionRule? Rule() =>
        _rules.SelectedIndex >= 0 && _rules.SelectedIndex < _settings.Rules.Count ? _settings.Rules[_rules.SelectedIndex] : null;

    private void ShowRules() =>
        SocialUi.Fill(_rules, [.. _settings.Rules.Select(r => $"{(r.Enabled ? "●" : "○")}  {r.Name}  ({r.Ifs.Count} if · {r.Thens.Count} then)")]);

    private void ShowRule()
    {
        var rule = Rule();
        _loading = true;
        _name.Text = rule?.Name ?? "";
        _repeat.Text = (rule?.RepeatSeconds ?? 0).ToString(CultureInfo.CurrentCulture);
        _enabled.Checked = rule?.Enabled ?? false;
        _loading = false;
        SocialUi.Fill(_ifs, rule is null ? [] : [.. rule.Ifs.Select(ConditionSettings.Describe)]);
        SocialUi.Fill(_thens, rule is null ? [] : [.. rule.Thens.Select(ConditionSettings.Describe)]);
    }

    private void NewRule()
    {
        _settings.Rules.Add(new ConditionRule { Name = $"Rule {_settings.Rules.Count + 1}" });
        _changed();
        ShowRules();
        _rules.SelectedIndex = _settings.Rules.Count - 1;
    }

    private void DeleteRule()
    {
        if (Rule() is not { } rule)
            return;
        _settings.Rules.Remove(rule);
        _changed();
        ShowRules();
        ShowRule();
    }

    private void AddIf()
    {
        if (Rule() is not { } rule)
        {
            NewRule();
            rule = Rule()!;
        }
        rule.Ifs.Add(new ConditionIf
        {
            Type = IfTypes[Math.Max(0, _ifType.SelectedIndex)],
            Number = SocialUi.Int(_ifNumber.Input.Text, 0, int.MaxValue),
            Text = _ifText.Input.Text.Trim(),
        });
        _changed();
        ShowRules();
        ShowRule();
    }

    private void AddThen()
    {
        if (Rule() is not { } rule)
        {
            NewRule();
            rule = Rule()!;
        }
        var type = ThenTypes[Math.Max(0, _thenType.SelectedIndex)];
        string text = _thenText.Input.Text.Trim();
        if (type == ThenType.PlaySound && text.Length == 0)
        {
            using var dialog = new OpenFileDialog { Filter = "Wave sound (*.wav)|*.wav", Title = "Sound to play (Cancel: Windows sound)" };
            if (dialog.ShowDialog() == DialogResult.OK)
                text = dialog.FileName;
        }
        rule.Thens.Add(new ConditionThen { Type = type, Number = SocialUi.Int(_thenNumber.Input.Text, 0, int.MaxValue), Text = text });
        _changed();
        ShowRules();
        ShowRule();
    }

    private void RemoveAt<T>(ListBox list, List<T>? items)
    {
        if (items is null || list.SelectedIndex < 0 || list.SelectedIndex >= items.Count)
            return;
        items.RemoveAt(list.SelectedIndex);
        _changed();
        ShowRules();
        ShowRule();
    }

    private void MoveUp()
    {
        if (Rule() is not { } rule || _thens.SelectedIndex <= 0)
            return;
        int i = _thens.SelectedIndex;
        (rule.Thens[i - 1], rule.Thens[i]) = (rule.Thens[i], rule.Thens[i - 1]);
        _changed();
        ShowRule();
        _thens.SelectedIndex = i - 1;
    }
}
