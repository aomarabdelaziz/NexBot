using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>Town loop: what to buy and sell at an NPC.</summary>
public class TownTests
{
    private static readonly ItemInfo HpPotion = new(4, "ITEM_ETC_HP_POTION_05", "HP recovery potion (XL)", 3, 1, 1, 0, 0, "", 50, 1);
    private static readonly ItemInfo Arrow = new(62, "ITEM_ETC_AMMO_ARROW_01", "Arrow", 3, 4, 1, 0, 0, "", 250, 1);
    private static readonly ItemInfo Junk = new(900, "ITEM_ETC_TRASH_BONE", "Wolf bone", 3, 9, 0, 0, 0, "", 50, 1);

    private static (SroClient Client, InMemoryGameData Data) Client()
    {
        var data = WorldTests.Data();
        foreach (var i in new[] { HpPotion, Arrow, Junk })
            data.Items[i.Id] = i;
        data.Shops["NPC_CH_POTION"] = [new ShopSlotInfo(0, 3, HpPotion.ServerName)];
        data.Shops["NPC_CH_SMITH"] = [new ShopSlotInfo(1, 0, Arrow.ServerName)];
        var client = new SroClient(data, NullLogger.Instance, _ => true);
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        var c = client.World.Character!;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            c.Inventory[i] = null;
        return (client, data);
    }

    private static void Put(SroClient client, int slot, ItemInfo info, ushort quantity)
    {
        var item = ItemFactory.Create(info);
        item.Quantity = quantity;
        client.World.Character!.Inventory[slot] = item;
    }

    [Fact]
    public void Buy_plan_buys_the_missing_amount_at_the_npc_that_sells_it()
    {
        var (client, data) = Client();
        using var _c = client;
        var c = client.World.Character!;
        Put(client, 13, HpPotion, 30);
        var s = new TownSettings { Buy = [new() { Item = HpPotion.ServerName, Quantity = 200 }, new() { Item = Arrow.ServerName, Quantity = 1000 }] };

        var plan = Assert.Single(TownRunner.BuyPlan(c, s, data, "NPC_CH_POTION"));
        Assert.Equal(170, plan.Need);
        Assert.Equal(50, plan.Stack);
        Assert.Equal((byte)3, plan.Slot.Slot);
        Assert.Equal(1000, Assert.Single(TownRunner.BuyPlan(c, s, data, "NPC_CH_SMITH")).Need);

        s.Buy[0].Enabled = false;
        Assert.Empty(TownRunner.BuyPlan(c, s, data, "NPC_CH_POTION"));
    }

    [Fact]
    public void Sell_plan_sells_the_list_and_only_excess_consumables()
    {
        var (client, _) = Client();
        using var _c = client;
        var c = client.World.Character!;
        Put(client, 13, HpPotion, 50);
        Put(client, 14, HpPotion, 50);
        Put(client, 15, Junk, 7);
        var s = new TownSettings { Buy = [new() { Item = HpPotion.ServerName, Quantity = 80 }], Sell = ["bone"] };

        Assert.Equal([(15, (ushort)7)], TownRunner.SellPlan(c, s));
        s.SellExcess = true;
        Assert.Equal([(13, (ushort)20), (15, (ushort)7)], TownRunner.SellPlan(c, s));
        s.Sell = ["potion"]; // on the buy list: never sold by the sell list
        Assert.Equal([(13, (ushort)20)], TownRunner.SellPlan(c, s));
    }

    [Fact]
    public void Drop_and_store_plans_take_the_listed_items_but_never_the_buy_list()
    {
        var (client, _) = Client();
        using var _c = client;
        var c = client.World.Character!;
        Put(client, 13, HpPotion, 50);
        Put(client, 15, Junk, 7);
        Put(client, 16, Arrow, 100);
        var s = new TownSettings { Buy = [new() { Item = HpPotion.ServerName, Quantity = 80 }], Drop = ["bone", "potion"], Store = ["arrow", " "] };

        Assert.Equal([15], TownRunner.DropPlan(c, s));
        Assert.Equal([16], TownRunner.StorePlan(c, s));
        Assert.True(TownRunner.IsStorageNpc("NPC_CH_WAREHOUSE_M"));
        Assert.False(TownRunner.IsStorageNpc("NPC_CH_POTION"));
    }

    [Fact]
    public void A_stored_item_goes_onto_its_stack_when_it_fits_else_into_the_first_empty_slot()
    {
        var storage = new SlotList<Item>(4);
        Item Of(ItemInfo info, ushort q) { var i = ItemFactory.Create(info); i.Quantity = q; return i; }
        storage[0] = Of(Junk, 10);
        storage[2] = Of(HpPotion, 40);

        Assert.Equal(2, TownRunner.StorageSlotFor(storage, Of(HpPotion, 10)));
        Assert.Equal(1, TownRunner.StorageSlotFor(storage, Of(HpPotion, 11))); // would overflow the stack
        storage[1] = Of(Arrow, 1);
        storage[3] = Of(Arrow, 1);
        Assert.Null(TownRunner.StorageSlotFor(storage, Of(HpPotion, 11)));
    }

    [Fact]
    public void Storage_contents_are_read_and_a_deposit_moves_the_item_into_it()
    {
        var (client, _) = Client();
        using var _c = client;
        Put(client, 13, HpPotion, 30);
        client.HandlePacket(new Packet(Opcodes.Server.StorageDataBegin, [0x10, 0x27, 0, 0, 0, 0, 0, 0]));
        // capacity 150, one item: slot 0, not rented, HP potion x20
        client.HandlePacket(new Packet(Opcodes.Server.StorageData, [150, 1, 0, 0, 0, 0, 0, 4, 0, 0, 0, 20, 0]));
        client.HandlePacket(new Packet(Opcodes.Server.StorageDataEnd));
        var storage = client.World.Storage!;
        Assert.Equal(10000UL, client.World.StorageGold);
        Assert.Equal(150, storage.Capacity);
        Assert.Equal((ushort)20, storage[0]!.Quantity);

        client.HandlePacket(new Packet(Opcodes.Server.ItemMovementResponse, [1, 2, 13, 0]));
        Assert.Null(client.World.Character!.Inventory[13]);
        Assert.Equal((ushort)50, storage[0]!.Quantity);
        Assert.Equal("020D0005000000", Convert.ToHexString(ClientPackets.StoreItem(5, 13, 0).Payload));
        Assert.Equal("070D", Convert.ToHexString(ClientPackets.DropItem(13).Payload));
    }

    [Fact]
    public void Exchange_window_tracks_both_sides_and_swaps_the_items_when_done()
    {
        var (client, _) = Client();
        using var _c = client;
        var c = client.World.Character!;
        Put(client, 13, HpPotion, 30);
        c.Gold = 1000;
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeStarted, BitConverter.GetBytes(777u)));
        var exchange = client.World.Exchange!;
        Assert.Equal(777u, exchange.PartnerId);

        // ours: bag slot 13, HP potion x30; theirs: HP potion x5
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeItemsUpdate,
            [.. BitConverter.GetBytes(c.UniqueId), 1, 13, 0, 0, 0, 0, 4, 0, 0, 0, 30, 0]));
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeItemsUpdate, [.. BitConverter.GetBytes(777u), 1, 0, 0, 0, 0, 0, 4, 0, 0, 0, 5, 0]));
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeGoldUpdate, [2, .. BitConverter.GetBytes(500UL)]));
        client.HandlePacket(new Packet(Opcodes.Server.ExchangePlayerConfirmed));
        Assert.Equal([(byte)13], exchange.MyItems.Keys);
        Assert.Single(exchange.PartnerItems);
        Assert.Equal(500UL, exchange.PartnerGold);
        Assert.True(exchange.PartnerConfirmed);

        client.HandlePacket(new Packet(Opcodes.Server.ExchangeCompleted));
        Assert.Null(client.World.Exchange);
        Assert.Equal((ushort)5, c.Inventory[13]!.Quantity);
        Assert.Equal(1500UL, c.Gold);

        Assert.Equal("040D", Convert.ToHexString(ClientPackets.ExchangeAddItem(13).Payload));
        Assert.Equal("0500", Convert.ToHexString(ClientPackets.ExchangeRemoveItem(0).Payload));
        Assert.Equal("0D6400000000000000", Convert.ToHexString(ClientPackets.ExchangeGold(100).Payload));
        Assert.Equal("09030000", Convert.ToHexString(ClientPackets.ExchangeRequest(777).Payload));
    }

    [Fact]
    public void Another_players_gear_goes_into_the_slots_its_type_belongs_to()
    {
        static Item Of(byte tid3, byte tid4) => ItemFactory.Create(new ItemInfo(1, "ITEM_CH_X", "X", 1, tid3, tid4, 0, 0, "", 1, 1));
        var taken = new List<int>();
        Assert.Equal(0, NexBot.Map.CharacterSheet.WornSlotOf(Of(3, 1), taken)); // armor head
        Assert.Equal(5, NexBot.Map.CharacterSheet.WornSlotOf(Of(11, 6), taken)); // heavy armor boots
        Assert.Equal(6, NexBot.Map.CharacterSheet.WornSlotOf(Of(6, 2), taken)); // weapon
        Assert.Equal(7, NexBot.Map.CharacterSheet.WornSlotOf(Of(4, 1), taken)); // shield
        Assert.Equal(9, NexBot.Map.CharacterSheet.WornSlotOf(Of(5, 1), taken)); // earring
        Assert.Equal(11, NexBot.Map.CharacterSheet.WornSlotOf(Of(5, 3), taken)); // first ring
        taken.Add(11);
        Assert.Equal(12, NexBot.Map.CharacterSheet.WornSlotOf(Of(5, 3), taken)); // second ring
        Assert.Equal(-1, NexBot.Map.CharacterSheet.WornSlotOf(Of(13, 1), taken)); // avatar hat is not worn gear
        Assert.Equal(0, NexBot.Map.CharacterSheet.AvatarSlotOf(Of(13, 1)));
        Assert.Equal(4, NexBot.Map.CharacterSheet.AvatarSlotOf(Of(14, 0)));
    }

    [Fact]
    public void Exchange_requests_are_accepted_by_the_auto_accept_rules()
    {
        var s = new ExchangeSettings();
        Assert.NotNull(SocialManager.ExchangeRefusal(s, false, true)); // off by default
        s.AutoAccept = true;
        Assert.Null(SocialManager.ExchangeRefusal(s, false, true));
        Assert.NotNull(SocialManager.ExchangeRefusal(s, true, true)); // botting
        Assert.NotNull(SocialManager.ExchangeRefusal(s, false, false)); // outside town
        s.AcceptWhileBotting = true;
        s.TownOnly = false;
        Assert.Null(SocialManager.ExchangeRefusal(s, true, false));
    }

    [Fact]
    public void Our_exchange_request_opens_the_window_when_they_accept_as_captured()
    {
        var (client, _) = Client();
        using var _c = client;
        var c = client.World.Character!;
        Put(client, 0x24, HpPotion, 962);
        // Captured on the user's server: the asking side gets 0xB081 [01][player], no 0x3085.
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeInvitationResponse, Convert.FromHexString("0199C80000")));
        var exchange = client.World.Exchange!;
        Assert.Equal(0xC899u, exchange.PartnerId);
        byte[] me = BitConverter.GetBytes(c.UniqueId);
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeItemsUpdate, [.. me, .. Convert.FromHexString("01240000000004000000C203")]));
        Assert.Equal((ushort)962, exchange.MyItems[0x24].Quantity);
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeItemsUpdate, Convert.FromHexString("99C800000200000000000400000001000100000000040000000100")));
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeGoldUpdate, Convert.FromHexString("0220A1070000000000")));
        client.HandlePacket(new Packet(Opcodes.Server.ExchangePlayerConfirmed));
        Assert.Equal(2, exchange.PartnerItems.Count);
        Assert.Equal(500_000UL, exchange.PartnerGold);
        Assert.True(exchange.PartnerConfirmed);
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeExitResponse, Convert.FromHexString("021B18"))); // refused cancel
        Assert.NotNull(client.World.Exchange);
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeCanceled, Convert.FromHexString("2C18")));
        Assert.Null(client.World.Exchange);
    }

    [Fact]
    public void An_exchange_request_waits_for_an_answer_and_a_cancel_closes_the_window()
    {
        var (client, _) = Client();
        using var _c = client;
        client.HandlePacket(new Packet(Opcodes.Server.PlayerPetitionRequest, [1, .. BitConverter.GetBytes(777u)]));
        Assert.Equal(777u, client.World.ExchangeRequest!.Value.Id);
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeStarted, BitConverter.GetBytes(777u)));
        Assert.Null(client.World.ExchangeRequest);
        Assert.NotNull(client.World.Exchange);
        client.HandlePacket(new Packet(Opcodes.Server.ExchangeCanceled));
        Assert.Null(client.World.Exchange);
    }

    [Fact]
    public void Parts_of_names_start_a_word_and_the_storage_list_wins_over_selling_and_dropping()
    {
        var (client, data) = Client();
        using var _c = client;
        var c = client.World.Character!;
        var chat = new ItemInfo(901, "ITEM_MALL_GLOBAL_CHATTING", "Global chatting", 3, 13, 1, 0, 0, "", 10, 1);
        var hat = new ItemInfo(902, "ITEM_CH_M_CARNIVAL_HAT", "Carnival Hat (M)", 1, 13, 1, 0, 0, "", 1, 1);
        Put(client, 13, chat, 9);
        Put(client, 14, hat, 1);
        Put(client, 15, Junk, 7);
        var s = new TownSettings { Sell = ["Hat", "bo"] };

        Assert.Equal([(14, (ushort)1), (15, (ushort)7)], TownRunner.SellPlan(c, s));
        s.Store = ["Global", "bone"];
        s.Drop = ["wolf"];
        Assert.Equal([(14, (ushort)1)], TownRunner.SellPlan(c, s));
        Assert.Empty(TownRunner.DropPlan(c, s));
        Assert.Equal([13, 15], TownRunner.StorePlan(c, s));
    }

    [Fact]
    public void A_return_scroll_used_in_the_game_is_noticed()
    {
        var (client, _) = Client();
        using var _c = client;
        var scroll = new ItemInfo(61, "ITEM_ETC_SCROLL_RETURN_02", "Return Scroll", 3, 3, 1, 0, 0, "", 50, 1);
        Put(client, 20, scroll, 5);
        Put(client, 21, Junk, 1);
        int returns = 0;
        client.ReturnedToTownByPlayer += (_, _) => returns++;

        client.ObservePacketToServer(ClientPackets.UseItem(21, 0x0000), fromGameClient: true);
        client.ObservePacketToServer(ClientPackets.UseItem(20, 0x09EC), fromGameClient: false); // the bot's own
        Assert.Equal(0, returns);
        client.ObservePacketToServer(ClientPackets.UseItem(20, 0x09EC), fromGameClient: true);
        client.ObservePacketToServer(ClientPackets.ReviveInTown(), fromGameClient: true);
        Assert.Equal(2, returns);
    }

    [Fact]
    public void Repair_packets_and_repair_npcs()
    {
        Assert.True(TownRunner.IsRepairNpc("NPC_CH_SMITH"));
        Assert.True(TownRunner.IsRepairNpc("NPC_WC_ARMOR"));
        Assert.False(TownRunner.IsRepairNpc("NPC_CH_POTION"));
        var all = ClientPackets.RepairAll(77);
        Assert.Equal(Opcodes.Client.ItemRepair, all.Opcode);
        Assert.Equal("4D00000002", Convert.ToHexString(all.ToArray()));
    }
}
