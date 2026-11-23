using System.Globalization;
using NexBot.App.Theme;
using NexBot.Map;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// The small windows of the inventory: split a stack (how many to take off) and write a global message (what a
/// global chatting item sends to all players).
/// </summary>
internal static class ItemDialogs
{
    /// <summary>Asks how many of <paramref name="item"/> to split off; null when cancelled.</summary>
    public static ushort? AskSplit(IWin32Window? owner, ItemView item)
    {
        int max = item.Quantity - 1;
        using var form = Window($"Split {item.Name}", 380, 190);
        var hint = Hint(string.Create(CultureInfo.CurrentCulture, $"The stack has {item.Quantity:N0}. How many go to the new slot? (1 - {max:N0})"));
        var amount = new SroInput { Width = 120, Location = new Point(16, 58) };
        amount.Text = Math.Max(1, item.Quantity / 2).ToString(CultureInfo.InvariantCulture);
        amount.Box.MaxLength = 5;
        var one = Quick("1", 146, () => amount.Text = "1");
        var half = Quick("Half", 196, () => amount.Text = Math.Max(1, item.Quantity / 2).ToString(CultureInfo.InvariantCulture));
        var most = Quick("All but 1", 256, () => amount.Text = max.ToString(CultureInfo.InvariantCulture));
        var error = Error(96);
        ushort result = 0;
        var ok = Button("Split", form, 104, () =>
        {
            if (!int.TryParse(amount.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) || n < 1 || n > max)
            {
                error.Text = string.Create(CultureInfo.CurrentCulture, $"Enter a number from 1 to {max:N0}.");
                return false;
            }
            result = (ushort)n;
            return true;
        });
        form.Controls.AddRange([hint, amount, one, half, most, error, ok, Cancel(form, 104)]);
        form.AcceptButton = null;
        amount.Box.KeyDown += (_, e) => Enter(e, ok);
        form.Shown += (_, _) =>
        {
            amount.Box.Focus();
            amount.Box.SelectAll();
        };
        return form.ShowDialog(owner) == DialogResult.OK ? result : null;
    }

    /// <summary>Asks for a global message (max <paramref name="maxLength"/> characters); null when cancelled.</summary>
    public static string? AskGlobal(IWin32Window? owner, string itemName, int maxLength)
    {
        using var form = Window("Global message", 460, 196);
        var hint = Hint($"Sent to every player on the server with {itemName}. One item is used.");
        var text = new SroInput { Width = 428, Location = new Point(16, 58) };
        text.Box.MaxLength = maxLength;
        var counter = Sro.Label($"0 / {maxLength}", Muted, SroTheme.Small);
        counter.AutoSize = false;
        counter.Size = new Size(120, 18);
        counter.Location = new Point(324, 90);
        counter.TextAlign = ContentAlignment.TopRight;
        text.Box.TextChanged += (_, _) => counter.Text = $"{text.Box.TextLength} / {maxLength}";
        var error = Error(90);
        error.Width = 300;
        string result = "";
        var send = Button("Send", form, 118, () =>
        {
            string message = text.Text.Trim();
            if (message.Length == 0)
            {
                error.Text = "Write the message first.";
                return false;
            }
            result = message;
            return true;
        });
        send.Left = 236;
        var cancel = Cancel(form, 118);
        cancel.Left = 340;
        form.Controls.AddRange([hint, text, counter, error, send, cancel]);
        text.Box.KeyDown += (_, e) => Enter(e, send);
        form.Shown += (_, _) => text.Box.Focus();
        return form.ShowDialog(owner) == DialogResult.OK ? result : null;
    }

    // ---------------------------------------------------------------- building blocks

    private static Form Window(string title, int width, int height) => new()
    {
        Text = title,
        FormBorderStyle = FormBorderStyle.FixedToolWindow,
        StartPosition = FormStartPosition.CenterParent,
        ClientSize = new Size(width, height),
        BackColor = Bg,
        ForeColor = SroTheme.Text,
        ShowInTaskbar = false,
        MaximizeBox = false,
        MinimizeBox = false,
        KeyPreview = true,
    };

    private static Label Hint(string text)
    {
        var label = Sro.Label(text, SroTheme.Text, SroTheme.Body);
        label.AutoSize = false;
        label.Location = new Point(16, 14);
        label.Size = new Size(430, 40);
        return label;
    }

    private static Label Error(int y)
    {
        var label = Sro.Label("", Bad, SroTheme.Small);
        label.AutoSize = false;
        label.Location = new Point(16, y);
        label.Size = new Size(340, 18);
        return label;
    }

    private static SroButton Quick(string text, int x, Action click)
    {
        var button = new SroButton { Text = text, Width = text.Length > 4 ? 86 : 46, Height = 28, Location = new Point(x, 58), TabStop = false };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The OK button: closes with OK when <paramref name="accept"/> returns true.</summary>
    private static SroButton Button(string text, Form form, int y, Func<bool> accept)
    {
        var button = new SroButton { Text = text, Width = 96, Height = 30, Location = new Point(form.ClientSize.Width - 216, y) };
        button.Click += (_, _) =>
        {
            if (!accept())
                return;
            form.DialogResult = DialogResult.OK;
            form.Close();
        };
        return button;
    }

    private static SroButton Cancel(Form form, int y)
    {
        var button = new SroButton { Text = "Cancel", Width = 96, Height = 30, Location = new Point(form.ClientSize.Width - 112, y) };
        button.Click += (_, _) =>
        {
            form.DialogResult = DialogResult.Cancel;
            form.Close();
        };
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                form.DialogResult = DialogResult.Cancel;
                form.Close();
            }
        };
        return button;
    }

    private static void Enter(KeyEventArgs e, SroButton button)
    {
        if (e.KeyCode != Keys.Enter)
            return;
        e.SuppressKeyPress = true;
        button.PerformClick();
    }
}
