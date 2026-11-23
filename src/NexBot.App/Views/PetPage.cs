using System.Globalization;
using NexBot.App.Theme;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Map;
using NexBot.Sessions;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// Pet (phBot's Pet tab): the summoned pets (attack, grab, horse, transport) with HP and HGP and an Unsummon button,
/// and the attack pet options: summon, heal, feed, revive, return to town when it dies, passive mode.
/// </summary>
internal sealed class PetPage : UserControl
{
    private readonly Func<SroClient?> _game;
    private readonly Func<PetManager?> _manager;
    private readonly Func<PickManager?> _pick;
    private readonly IconCache _icons;
    private readonly ListBox _petBag = SocialUi.List();
    private readonly ListBox _picked = SocialUi.List();
    private readonly Label _bagTitle = SocialUi.Caption("GRAB PET'S BAG");
    private IReadOnlyList<Item?> _bagItems = [];
    private IReadOnlyList<PickManager.PickedUp> _pickedRows = [];
    private readonly ListBox _pets = SocialUi.List();
    private readonly Label _message = Sro.Label("", Muted, SroTheme.Small);
    private readonly Label _status = Sro.Label("", Muted, SroTheme.Small);
    private readonly Panel _options = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent, AutoScroll = true };
    private readonly TradeOptionList _preferred;
    private PetSettings _settings = new();
    private Action _changed = () => { };
    private IReadOnlyList<Cos> _shown = [];
    private SocialUi.PortraitRow[] _rows = [];

    /// <summary>Pets shown while not in game (screenshots for the guide only).</summary>
    internal SocialUi.PortraitRow[]? DemoRows { get; set; }

    public PetPage(Func<SroClient?> game, Func<PetManager?> manager, Func<PickManager?> pick, IconCache icons)
    {
        _game = game;
        _manager = manager;
        _pick = pick;
        _icons = icons;
        BackColor = Bg;
        Dock = DockStyle.Fill;
        SocialUi.PortraitList(_pets, icons, i => i < _rows.Length ? _rows[i] : null, 52);
        var grid = SocialUi.Columns(3);
        grid.ColumnStyles[0].Width = 36;
        grid.ColumnStyles[1].Width = 34;
        grid.ColumnStyles[2].Width = 30;

        var petsPanel = SocialUi.Panel("Pets");
        var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack };
        well.Controls.Add(_pets);
        _message.AutoSize = false;
        _message.Dock = DockStyle.Bottom;
        _message.Height = 22;
        _status.AutoSize = false;
        _status.Dock = DockStyle.Bottom;
        _status.Height = 40;
        petsPanel.Controls.Add(well);
        petsPanel.Controls.Add(_message);
        petsPanel.Controls.Add(_status);
        petsPanel.Controls.Add(SocialUi.Buttons(SocialUi.Button("Unsummon", 100, Unsummon)));

        // Preferred pets (like AutoTrade's transports, but in an order): click to add as the next one, click to remove.
        _preferred = new TradeOptionList(icons) { Dock = DockStyle.Top, RankWord = "PET", Extra = DeadText };
        _preferred.Picked += option =>
        {
            var list = _settings.Preferred;
            int at = list.FindIndex(n => string.Equals(n, option.Info.ServerName, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
                list.RemoveAt(at);
            else
                list.Add(option.Info.ServerName);
            _changed();
            ShowPreferred();
        };
        var prefPanel = SocialUi.Panel("Preferred pets");
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
        scroll.Controls.Add(_preferred);
        scroll.Resize += (_, _) => _preferred.Width = scroll.ClientSize.Width;
        prefPanel.Controls.Add(scroll);
        prefPanel.Controls.Add(SocialUi.Hint("Click pets in the order you want them: PET 1 is summoned (or revived when dead) if it is in the bag, else PET 2, and so on. Click one again to take it off. With none of them in the bag, any attack pet is used.", 52));
        prefPanel.Controls.Add(SocialUi.Buttons(SocialUi.Button("Clear order", 110, () =>
        {
            _settings.Preferred.Clear();
            _changed();
            ShowPreferred();
        })));

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Bg, Margin = Padding.Empty };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        left.Controls.Add(petsPanel, 0, 0);
        left.Controls.Add(prefPanel, 0, 1);
        grid.Controls.Add(left, 0, 0);

        var attackPanel = SocialUi.Panel("Attack pet");
        attackPanel.Controls.Add(_options);
        grid.Controls.Add(attackPanel, 1, 0);
        grid.Controls.Add(BuildGrabPet(), 2, 0);
        Controls.Add(grid);
    }

    /// <summary>The grab pet's bag and what was picked up this session (Pick filter).</summary>
    private SroPanel BuildGrabPet()
    {
        var panel = SocialUi.Panel("Grab pet");
        ItemVisuals.IconList(_petBag, _icons, i => i < _bagItems.Count && _bagItems[i] is { } item
            ? ItemCard.For(CharacterSheet.ViewOf(item, i), false, _game()?.Data.GetItem(item.ServerName))
            : null);
        ItemVisuals.IconList(_picked, _icons, i => i < _pickedRows.Count && _pickedRows[i] is { } row
            ? new ItemPreviewInfo(row.Icon, row.Name, $"Picked up by the {row.By}\n{row.At:HH:mm:ss}")
            : null);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        Control Well(ListBox list)
        {
            var well = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = InputBack, Margin = new Padding(0, 0, 0, 4) };
            well.Controls.Add(list);
            return well;
        }
        _bagTitle.Dock = DockStyle.Fill;
        var pickedTitle = SocialUi.Caption("PICKED UP THIS SESSION");
        pickedTitle.Dock = DockStyle.Fill;
        body.Controls.Add(_bagTitle, 0, 0);
        body.Controls.Add(Well(_petBag), 0, 1);
        body.Controls.Add(pickedTitle, 0, 2);
        body.Controls.Add(Well(_picked), 0, 3);
        panel.Controls.Add(body);
        panel.Controls.Add(SocialUi.Hint("The grab pet's own bag (read when it is summoned and kept up to date while it picks), and every item and gold picked up by the pet or the character, newest first. Point at an item for its card.", 64));
        return panel;
    }

    private void ShowGrabPet(SroClient? client)
    {
        var pet = client?.PickPet;
        var inv = CultureInfo.CurrentCulture;
        if (pet is null)
        {
            _bagItems = [];
            _bagTitle.Text = "GRAB PET'S BAG";
            SocialUi.Fill(_petBag, [client is null ? "Not in game." : "No grab pet is summoned."]);
        }
        else
        {
            var slots = Enumerable.Range(0, pet.Inventory.Capacity).Where(i => pet.Inventory[i] is not null).ToList();
            _bagItems = [.. slots.Select(i => pet.Inventory[i])];
            _bagTitle.Text = string.Create(inv, $"GRAB PET'S BAG  ·  {slots.Count} / {pet.Inventory.Capacity}");
            SocialUi.Fill(_petBag, slots.Count == 0 ? ["The bag is empty."]
                : [.. slots.Select(i => pet.Inventory[i]!).Select(item =>
                    PickFilter.Label(item.Name, item.ServerName) + (item is Equipable { Plus: > 0 } e ? $" +{e.Plus}" : "")
                    + (item.QuantityMax > 1 ? string.Create(inv, $"  ×{item.Quantity:N0}") : ""))]);
        }
        _pickedRows = _pick()?.History ?? [];
        SocialUi.Fill(_picked, _pickedRows.Count == 0 ? ["Nothing picked up yet."]
            : [.. _pickedRows.Select(r => string.Create(inv, $"{r.At:HH:mm}  {r.Name}{(r.Quantity > 1 ? $" ×{r.Quantity:N0}" : "")}  ·  {r.By}"))]);
    }

    public void Bind(PetSettings s, Action changed)
    {
        _settings = s;
        _changed = changed;
        _options.Controls.Clear();
        SocialUi.Stack(_options,
            SocialUi.Switch("Use attack pet", s.UseAttackPet, v => s.UseAttackPet = v, changed),
            SocialUi.Field("   Recovery kit when HP below (%)", s.HealBelow.ToString(CultureInfo.CurrentCulture), 60, v => s.HealBelow = SocialUi.Int(v, 0, 99), changed),
            SocialUi.Field("   HGP potion when HGP below (%)", s.FeedBelow.ToString(CultureInfo.CurrentCulture), 60, v => s.FeedBelow = SocialUi.Int(v, 0, 99), changed),
            SocialUi.Switch("Auto summon attack pet", s.AutoSummon, v => s.AutoSummon = v, changed),
            SocialUi.Switch("   only in town", s.SummonOnlyInTown, v => s.SummonOnlyInTown = v, changed),
            SocialUi.Switch("   only at training area", s.SummonOnlyAtTrainingArea, v => s.SummonOnlyAtTrainingArea = v, changed),
            SocialUi.Switch("Auto revive attack pet", s.AutoRevive, v => s.AutoRevive = v, changed),
            SocialUi.Field("   Times (0 = no limit)", s.ReviveTimes.ToString(CultureInfo.CurrentCulture), 60, v => s.ReviveTimes = SocialUi.Int(v, 0, 999), changed),
            SocialUi.Switch("   only if HP potions are present", s.ReviveOnlyWithPotions, v => s.ReviveOnlyWithPotions = v, changed),
            SocialUi.Switch("Return to town when the attack pet dies", s.ReturnWhenDies, v => s.ReturnWhenDies = v, changed),
            SocialUi.Switch("   only if there aren't any revive items", s.ReturnOnlyIfNoRevive, v => s.ReturnOnlyIfNoRevive = v, changed),
            SocialUi.Switch("Don't attack monsters", s.DontAttack, v => s.DontAttack = v, changed),
            SocialUi.Hint("Use attack pet must be on for the rest. HP potions for the pet are recovery kits; revive items are the pet resurrection items. The return to town happens right away when the pet dies, and also after 3 failed revives or summons in a row (under attack, for example).", 84),
            SocialUi.Hint("Protect the attack pet (attack what attacks it) is not built yet. Fellow pets (SP recall) are not in vSRO 1.188.", 56));
    }

    /// <summary>"DEAD" when the scrolls of this pet in the bag are dead.</summary>
    private string? DeadText(TradeOption option)
    {
        if (_game()?.World.Character is not { } c)
            return null;
        int dead = 0;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            if (c.Inventory[i] is CosItem { State: PetItemState.Dead } scroll && scroll.Id == option.Info.Id)
                dead++;
        return dead == 0 ? null : dead == option.Owned ? "DEAD" : $"{dead} DEAD";
    }

    /// <summary>Every attack pet in the game: the chosen ones first in their order, then the ones in the bag.</summary>
    private void ShowPreferred()
    {
        var client = _game();
        if (client is null)
        {
            _preferred.SetRanked([], _settings.Preferred, "Start the bot and enter the game to see the attack pets.");
            return;
        }
        var order = _settings.Preferred;
        int Rank(TradeOption o)
        {
            int at = order.FindIndex(n => string.Equals(n, o.Info.ServerName, StringComparison.OrdinalIgnoreCase));
            return at < 0 ? int.MaxValue : at;
        }
        var pets = PetManager.AllAttackPets(client.Data, client.World.Character);
        _preferred.SetRanked([.. pets.OrderBy(Rank)], order, "No attack pets in the game data.");
    }

    private Cos? Selected() =>
        _pets.SelectedIndex >= 0 && _pets.SelectedIndex < _shown.Count ? _shown[_pets.SelectedIndex] : null;

    private void Unsummon()
    {
        if (_game() is not { } client || Selected() is not { } pet)
        {
            Say("Select a pet first.", false);
            return;
        }
        if (pet.UniqueId == client.World.Character?.RidingUniqueId)
        {
            Say("Get off the mount first.", false);
            return;
        }
        bool sent = client.UnsummonPet(pet.UniqueId);
        Say(sent ? $"Unsummoning {Name(pet)}." : "The bot is not connected.", sent);
    }

    private void Say(string text, bool ok)
    {
        _message.Text = text;
        _message.ForeColor = ok ? Good : Bad;
    }

    private static string Name(Cos pet) => pet.Name.Length > 0 ? pet.Name : pet.Info.Name;

    private static string Kind(Cos pet) =>
        pet.IsAttackPet ? "Attack pet" : pet.IsPickPet ? "Grab pet" : pet.IsHorse ? "Horse" : pet.IsTransport ? "Transport" : "Pet";

    public void RefreshView()
    {
        var client = _game();
        _shown = client is null ? [] : [.. client.World.MyPets.Values.Where(p => !p.IsGuildGuard).OrderBy(p => p.TypeId4)];
        var inv = CultureInfo.CurrentCulture;
        _rows = [.. _shown.Select(p =>
        {
            uint hpMax = p.HpMax > 0 ? p.HpMax : p.Info.Hp;
            int hp = hpMax > 0 ? (int)Math.Clamp(p.Hp * 100L / hpMax, 0, 100) : -1;
            string detail = p switch
            {
                AttackPet a => string.Create(inv, $"Attack pet  ·  Lv. {a.Level}  ·  HP {a.Hp:N0}  ·  HGP {a.Hgp / 100}%{(a.Settings == Game.AttackPetSettings.Disabled ? "  ·  passive" : "")}"),
                PickPet => $"Grab pet  ·  {p.Inventory.Count(i => i is not null)} items",
                _ => string.Create(inv, $"{Kind(p)}  ·  HP {p.Hp:N0}"),
            };
            if (client?.World.Character?.RidingUniqueId == p.UniqueId)
                detail += "  ·  riding";
            return new SocialUi.PortraitRow(p.Info.Icon.Length > 0 ? p.Info.Icon : null, Name(p), detail, hp, p is AttackPet ap ? Math.Clamp(ap.Hgp / 100, 0, 100) : -1);
        })];
        if (client is null && DemoRows is { } demo)
            _rows = demo;
        SocialUi.Fill(_pets, [.. _rows.Select(r => r.Name + "|" + r.Detail)]);
        ShowGrabPet(client);
        ShowPreferred();
        string status = _manager()?.Status ?? "";
        _status.Text = client is null ? "Not in game." : _shown.Count == 0 ? "No pet is summoned. " + status : status;
        _status.ForeColor = Muted;
    }
}
