using System.Globalization;
using NexBot.App.Theme;
using NexBot.Game;
using NexBot.Game.Items;
using NexBot.Map;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// The game's exchange window on the Inventory page: pick a nearby player and ask to exchange (or accept their
/// request); the other player's items and gold above, ours below. Drag bag items onto our slots to put them in (drag
/// one back to the bag, or right-click it, to take it out), set the gold, Confirm, then Approve when both confirmed.
/// </summary>
internal sealed class ExchangeView : Panel
{
    private const int Cell = 44;
    private const int Columns = 6;

    private readonly Func<SroClient?> _game;
    private readonly IconCache _icons;
    private readonly Action<string, bool> _say;
    private readonly SroCombo _players = new() { Width = 200 };
    private readonly SroButton _ask;
    private readonly SroButton _accept;
    private readonly SroButton _refuse;
    private readonly Label _state = Sro.Label("", Muted, SroTheme.Small);
    private readonly Label _partnerTitle = Sro.Label("Their items", Gold, BodyBold);
    private readonly Label _partnerGold = Sro.Label("0", SroTheme.Value, BodyBold);
    private readonly Label _partnerState = Sro.Label("", Muted, SroTheme.Small);
    private readonly Label _myTitle = Sro.Label("Your items", Gold, BodyBold);
    private readonly Label _myGold = Sro.Label("0", SroTheme.Value, BodyBold);
    private readonly Label _myState = Sro.Label("", Muted, SroTheme.Small);
    private readonly SroInput _goldInput = new() { Width = 150 };
    private readonly SroButton _setGold;
    private readonly SroButton _confirm;
    private readonly SroButton _cancel;
    private readonly SlotView[] _theirs = new SlotView[ExchangeState.Slots];
    private readonly SlotView[] _mine = new SlotView[ExchangeState.Slots];
    private string _playersSignature = "";
    private string _signature = "";
    private bool _busy;

    public ExchangeView(Func<SroClient?> game, IconCache icons, Action<string, bool> say)
    {
        _game = game;
        _icons = icons;
        _say = say;
        BackColor = Color.Transparent;
        AutoScroll = true;

        int y = 4;
        var pick = Sro.Label("Player nearby", Muted, SroTheme.Small);
        pick.Location = new Point(4, y + 6);
        pick.AutoSize = true;
        _players.Location = new Point(96, y);
        _ask = new SroButton { Text = "Exchange", Width = 92, Height = 28, Location = new Point(_players.Right + 8, y), TabStop = false };
        _ask.Click += (_, _) => Ask();
        _accept = new SroButton { Text = "Accept", Width = 76, Height = 28, Location = new Point(_players.Right + 8, y), TabStop = false, Visible = false };
        _accept.Click += (_, _) => Answer(true);
        _refuse = new SroButton { Text = "Refuse", Width = 76, Height = 28, Location = new Point(_accept.Right + 6, y), TabStop = false, Visible = false };
        _refuse.Click += (_, _) => Answer(false);
        y += 34;
        _state.AutoSize = false;
        _state.AutoEllipsis = true;
        _state.Bounds = new Rectangle(4, y, 520, 20);
        y += 26;
        Controls.AddRange([pick, _players, _ask, _accept, _refuse, _state]);

        // The other player's side.
        y = Side(y, _partnerTitle, _theirs, false, _partnerGold, _partnerState);

        // Our side.
        y = Side(y + 6, _myTitle, _mine, true, _myGold, _myState);
        _goldInput.Location = new Point(4, y);
        _setGold = new SroButton { Text = "Set gold", Width = 84, Height = 28, Location = new Point(_goldInput.Right + 6, y), TabStop = false };
        _setGold.Click += (_, _) => _ = SetGoldAsync();
        _goldInput.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _ = SetGoldAsync();
            }
        };
        y += 40;
        _confirm = new SroButton { Text = "Confirm", Width = 100, Height = 30, Location = new Point(4, y), TabStop = false };
        _confirm.Click += (_, _) => ConfirmOrApprove();
        _cancel = new SroButton { Text = "Cancel", Width = 100, Height = 30, Location = new Point(_confirm.Right + 8, y), TabStop = false };
        _cancel.Click += (_, _) =>
        {
            if (_game()?.ExchangeCancel() != true)
                _say("No exchange is open.", false);
        };
        Controls.AddRange([_goldInput, _setGold, _confirm, _cancel]);
        _optionsTop = y + 44;
        RefreshView();
    }

    private readonly int _optionsTop;
    private readonly List<Control> _options = [];

    /// <summary>The auto accept options, under the buttons.</summary>
    public void Bind(NexBot.Configuration.ExchangeSettings s, Action changed)
    {
        foreach (var c in _options)
        {
            Controls.Remove(c);
            c.Dispose();
        }
        _options.Clear();
        _options.AddRange(
        [
            SocialUi.Switch("Auto accept exchange requests", s.AutoAccept, v => s.AutoAccept = v, changed),
            SocialUi.Switch("   Accept while botting", s.AcceptWhileBotting, v => s.AcceptWhileBotting = v, changed),
            SocialUi.Switch("   Accept in town only", s.TownOnly, v => s.TownOnly = v, changed),
        ]);
        int y = _optionsTop;
        foreach (var c in _options)
        {
            c.Dock = DockStyle.None;
            c.Bounds = new Rectangle(4, y, 360, 28);
            y += 30;
            Controls.Add(c);
        }
    }

    /// <summary>One side of the window: title, 2 x 6 slots, the gold and how far that side got.</summary>
    private int Side(int y, Label title, SlotView[] slots, bool mine, Label gold, Label state)
    {
        title.AutoSize = false;
        title.AutoEllipsis = true;
        title.Bounds = new Rectangle(4, y, 520, 22);
        y += 24;
        var well = new Panel { Location = new Point(4, y), Size = new Size(Columns * (Cell + 4) + 8, 2 * (Cell + 4) + 8), BackColor = Hex("#080706") };
        well.Paint += (_, e) => StrokeRect(e.Graphics, Line, well.ClientRectangle);
        for (int i = 0; i < slots.Length; i++)
        {
            var slot = new SlotView(_icons, i) { Location = new Point(6 + i % Columns * (Cell + 4), 6 + i / Columns * (Cell + 4)), Size = new Size(Cell, Cell), AllowDrop = mine };
            slot.Hovered += s =>
            {
                if (s.Item is { } item)
                    ItemPreview.Show(s, ItemCard.For(item, false, _game()?.Data.GetItem(item.ServerName),
                        place: mine ? "In the exchange (yours)" : $"In the exchange ({_game()?.World.Exchange?.PartnerName ?? "theirs"})"), _icons);
                else
                    ItemPreview.Hide();
            };
            slot.Unhovered += _ => ItemPreview.Hide();
            if (mine)
            {
                slot.MoveRequested += (from, target) => _ = AddAsync(from);
                slot.Activated += s => _ = RemoveAsync(s);
                slot.AltClicked += s => _ = RemoveAsync(s);
            }
            slots[i] = slot;
            well.Controls.Add(slot);
        }
        gold.AutoSize = false;
        gold.TextAlign = ContentAlignment.MiddleLeft;
        gold.Bounds = new Rectangle(well.Right + 10, y + 4, 150, 22);
        var goldCaption = Sro.Label("Gold", Muted, SroTheme.Small);
        goldCaption.AutoSize = true;
        goldCaption.Location = new Point(well.Right + 10, y - 14);
        state.AutoSize = false;
        state.Bounds = new Rectangle(well.Right + 10, y + 34, 160, 40);
        Controls.AddRange([title, well, goldCaption, gold, state]);
        return well.Bottom + 8;
    }

    /// <summary>A slot of this window (ours or theirs).</summary>
    public bool Owns(SlotView slot) => _mine.Contains(slot) || _theirs.Contains(slot);

    /// <summary>A slot of ours was dragged back to the bag: take it out (theirs cannot be moved).</summary>
    public Task TakeBackAsync(SlotView slot) => _mine.Contains(slot) ? RemoveAsync(slot) : Task.CompletedTask;

    /// <summary>Alt + click on a bag item: into the window's next empty slot.</summary>
    public Task OfferAsync(SlotView bagSlot)
    {
        if (_game()?.World.Exchange is null)
        {
            _say("Open an exchange first (pick a player and click Exchange).", false);
            return Task.CompletedTask;
        }
        return AddAsync(bagSlot);
    }

    /// <summary>The bag slots whose items are in the window (the bag marks them).</summary>
    public IReadOnlyCollection<byte> InWindow => (IReadOnlyCollection<byte>?)_game()?.World.Exchange?.MyItems.Keys ?? [];

    private void Ask()
    {
        if (_game() is not { } client || client.World.Character is null)
        {
            _say("Not in game.", false);
            return;
        }
        if (client.World.Exchange is not null)
        {
            _say("An exchange is already open.", false);
            return;
        }
        string name = _players.Text;
        if (name.Length == 0 || !client.World.Players.TryGetValue(name, out var player))
        {
            _say("Pick a player nearby first.", false);
            return;
        }
        bool sent = client.RequestExchange(player.UniqueId);
        _say(sent ? $"Exchange request sent to {player.Name}: waiting for them to accept." : "The bot is not connected.", sent);
    }

    private void Answer(bool accept)
    {
        if (_game() is not { } client || client.World.ExchangeRequest is not { } request)
            return;
        bool sent = client.AnswerExchangeRequest(accept);
        _say(!sent ? "The bot is not connected." : accept ? $"Accepted {request.Name}'s exchange." : $"Refused {request.Name}'s exchange.", sent);
        RefreshView();
    }

    private async Task AddAsync(SlotView from)
    {
        if (_busy || Owns(from) || from.Avatar || from.Slot < CharacterSheet.FirstBagSlot || _game() is not { } client)
        {
            if (from.Avatar || (from.Slot >= 0 && from.Slot < CharacterSheet.FirstBagSlot && !Owns(from)))
                _say("Take the item off first: only bag items can be exchanged.", false);
            return;
        }
        await RunAsync(() => client.ExchangeAddItemAsync((byte)from.Slot));
    }

    private async Task RemoveAsync(SlotView slot)
    {
        if (_busy || slot.Item is null || _game() is not { } client)
            return;
        int index = Array.IndexOf(_mine, slot);
        await RunAsync(() => client.ExchangeRemoveItemAsync(index));
    }

    private async Task SetGoldAsync()
    {
        if (_busy || _game() is not { } client)
            return;
        string text = _goldInput.Box.Text.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal).Trim();
        if (!ulong.TryParse(text.Length == 0 ? "0" : text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong gold))
        {
            _say("Type the gold as a number.", false);
            return;
        }
        await RunAsync(() => client.ExchangeGoldAsync(gold));
    }

    private async Task RunAsync(Func<Task<ItemMoveResult>> action)
    {
        _busy = true;
        try
        {
            var result = await action();
            _say(result.Message, result.Ok);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            _say(ex.Message, false);
        }
        finally
        {
            _busy = false;
            RefreshView();
        }
    }

    private void ConfirmOrApprove()
    {
        if (_game() is not { } client || client.World.Exchange is not { } exchange)
        {
            _say("No exchange is open.", false);
            return;
        }
        if (!exchange.MyConfirmed)
            _say(client.ExchangeConfirm() ? "Confirmed: your side is locked." : "The bot is not connected.", true);
        else if (!exchange.PartnerConfirmed)
            _say($"Waiting for {exchange.PartnerName} to confirm.", false);
        else if (!exchange.MyApproved)
            _say(client.ExchangeApprove() ? "Approved: the exchange ends when they approve too." : "The bot is not connected.", true);
    }

    public void RefreshView()
    {
        var client = _game();
        var world = client?.World;
        var me = world?.Character?.Position;

        // Nearby players, nearest first; the choice stays while the list changes.
        string[] names = world is null ? [] : [.. world.Players.Values
            .OrderBy(p => me is { } at ? p.Position.DistanceTo(at) : 0).Select(p => p.Name)];
        string playersSignature = string.Join("|", names.Order(StringComparer.Ordinal));
        if (playersSignature != _playersSignature)
        {
            _playersSignature = playersSignature;
            string chosen = _players.Text;
            _players.SetItems(names);
            int at = Array.IndexOf(names, chosen);
            _players.SelectedIndex = at >= 0 ? at : names.Length > 0 ? 0 : -1;
        }

        var request = world?.ExchangeRequest;
        if (request is { } r && DateTime.UtcNow - r.At > TimeSpan.FromSeconds(30))
            request = null;
        var exchange = world?.Exchange;
        _accept.Visible = _refuse.Visible = request is not null && exchange is null;
        _ask.Visible = !_accept.Visible;
        _ask.Enabled = exchange is null;

        string signature = exchange is null ? $"none|{client is null}|{world?.ExchangeError}|{world?.LastExchange}|{request?.Name}"
            : $"{exchange.PartnerId}|{exchange.Version}";
        if (signature == _signature)
            return;
        _signature = signature;

        if (exchange is null)
        {
            _state.Text = request is { } asking ? $"{asking.Name} wants to exchange with you: Accept or Refuse."
                : world?.ExchangeError ?? world?.LastExchange ?? (client is null ? "Not in game." : "Pick a player and click Exchange, or wait for a request.");
            _state.ForeColor = request is not null ? Gold : world?.ExchangeError is not null ? Bad : Muted;
            _partnerTitle.Text = "Their items";
            _myTitle.Text = "Your items";
            Fill(_theirs, []);
            Fill(_mine, []);
            _partnerGold.Text = _myGold.Text = "0";
            _partnerState.Text = _myState.Text = "";
            _confirm.Text = "Confirm";
            _confirm.Enabled = _cancel.Enabled = _setGold.Enabled = false;
            return;
        }

        _state.Text = $"Exchanging with {exchange.PartnerName}. Drag or Alt + click bag items to offer them; Alt + click or right-click one to take it out.";
        _state.ForeColor = Good;
        _partnerTitle.Text = $"{exchange.PartnerName}'s items";
        _myTitle.Text = "Your items";
        Fill(_theirs, exchange.PartnerItems);
        Fill(_mine, exchange.MyItems.Values);
        _partnerGold.Text = exchange.PartnerGold.ToString("N0", CultureInfo.CurrentCulture);
        _myGold.Text = exchange.MyGold.ToString("N0", CultureInfo.CurrentCulture);
        _partnerState.Text = exchange.PartnerConfirmed ? "Confirmed" : "Choosing";
        _partnerState.ForeColor = exchange.PartnerConfirmed ? Good : Muted;
        _myState.Text = exchange.MyApproved ? "Approved: waiting for them" : exchange.MyConfirmed ? "Confirmed" : "Choosing";
        _myState.ForeColor = exchange.MyConfirmed ? Good : Muted;
        _confirm.Text = exchange.MyConfirmed && exchange.PartnerConfirmed ? "Approve" : "Confirm";
        _confirm.Enabled = !exchange.MyApproved && !(exchange.MyConfirmed && !exchange.PartnerConfirmed);
        _cancel.Enabled = true;
        _setGold.Enabled = !exchange.MyConfirmed;
    }

    private static void Fill(SlotView[] slots, IEnumerable<Item> items)
    {
        var list = items.ToList();
        for (int i = 0; i < slots.Length; i++)
            slots[i].SetItem(i < list.Count ? CharacterSheet.ViewOf(list[i], i) : null);
    }
}
