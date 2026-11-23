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

/// <summary>Pick Filter page: what to pick up, store, sell and drop, and the gold.</summary>
public class PickFilterTests
{
    private static readonly ItemInfo Sword = new(10, "ITEM_CH_SWORD_08_A", "Sword", 1, 6, 2, 0, 0, "", 1, 70);
    private static readonly ItemInfo RareSword = new(11, "ITEM_CH_SWORD_08_A_RARE", "Sword (SoS)", 1, 6, 2, 0, 0, "", 1, 70);
    private static readonly ItemInfo Gold = new(12, "ITEM_ETC_GOLD_01", "Gold", 3, 5, 0, 0, 0, "", 1, 0);
    private static readonly ItemInfo Arrow = new(13, "ITEM_ETC_AMMO_ARROW_01", "Arrow", 3, 4, 1, 0, 0, "", 250, 0);
    private static readonly ItemInfo Bolt = new(14, "ITEM_ETC_AMMO_BOLT_01", "Bolt", 3, 4, 2, 0, 0, "", 250, 0);
    private static readonly ItemInfo Stone = new(15, "ITEM_ETC_ARCHEMY_ATTRSTONE_STR_08", "Str stone", 3, 11, 1, 0, 0, "", 50, 0);

    private static Drop DropOf(ItemInfo info, uint uid, Position at, uint owner = 0, byte rarity = 0) =>
        new(info) { UniqueId = uid, Position = at, OwnerJoinId = owner, Rarity = rarity };

    private static Character InGame()
    {
        var client = new SroClient(WorldTests.Data(), NullLogger.Instance, _ => true);
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        var c = client.World.Character!;
        for (int i = 0; i < c.Inventory.Capacity; i++)
            c.Inventory[i] = null;
        return c;
    }

    [Fact]
    public void Drops_are_picked_by_the_filter_whose_they_are_and_the_options()
    {
        var c = InGame();
        var at = c.Position;
        var s = new PickFilterSettings();
        s.Set(Sword.ServerName, PickAction.Pick | PickAction.Sell);
        s.Set(Gold.ServerName, PickAction.Pick);
        var mine = DropOf(Sword, 1, at, c.JoinId);
        var party = DropOf(Sword, 2, at, 777);
        var free = DropOf(Gold, 3, at);
        var others = DropOf(Sword, 4, at, 999);
        var notListed = DropOf(Stone, 5, at, c.JoinId);

        var wanted = PickManager.Wanted(s, c, [mine, party, free, others, notListed], [777]).Select(d => d.UniqueId).ToList();
        Assert.Equal([1u, 2u], wanted.Order());
        s.PickOtherPlayersItems = true;
        s.PickPartyItems = false;
        Assert.Equal([1u, 3u], PickManager.Wanted(s, c, [mine, party, free, others], [777]).Select(d => d.UniqueId).Order());

        s.OnlyPickRareOrBlue = true; // equipment only: gold is still picked
        Assert.Equal([3u], PickManager.Wanted(s, c, [mine, free], []).Select(d => d.UniqueId));
        Assert.Single(PickManager.Wanted(s, c, [DropOf(Sword, 6, at, c.JoinId, rarity: 1)], []));

        s.DontPickItems = true;
        Assert.Empty(PickManager.Wanted(s, c, [mine, free], []));
    }

    [Fact]
    public void Arrows_or_bolts_are_picked_by_the_race_up_to_the_amount()
    {
        var s = new PickFilterSettings { PickArrows = true, ArrowAmount = 300 };
        Assert.True(PickFilter.ShouldPick(s, DropOf(Arrow, 1, default), european: false, ammoInBag: 250));
        Assert.False(PickFilter.ShouldPick(s, DropOf(Bolt, 1, default), european: false, ammoInBag: 0));
        Assert.True(PickFilter.ShouldPick(s, DropOf(Bolt, 1, default), european: true, ammoInBag: 0));
        Assert.False(PickFilter.ShouldPick(s, DropOf(Arrow, 1, default), european: false, ammoInBag: 300));
    }

    [Fact]
    public void Bag_items_are_stored_sold_or_dropped_by_the_filter()
    {
        var s = new PickFilterSettings();
        var sword = (Equipable)ItemFactory.Create(Sword);
        var rare = (Equipable)ItemFactory.Create(RareSword);
        s.Set(Sword.ServerName, PickAction.Store | PickAction.Sell);
        s.Set(RareSword.ServerName, PickAction.Sell);
        Assert.True(PickFilter.ShouldStore(s, sword));
        Assert.False(PickFilter.ShouldSell(s, sword)); // stored first
        Assert.False(PickFilter.ShouldSell(s, rare)); // rare: only with "allow selling of all item types"
        s.AllowSellingAllTypes = true;
        Assert.True(PickFilter.ShouldSell(s, rare));
        rare.Plus = 5;
        s.DontSellPlusAtLeast = 5;
        Assert.False(PickFilter.ShouldSell(s, rare));

        s.OnlyStoreRareOrBlue = true; // a white sword is not stored: it is sold
        Assert.False(PickFilter.ShouldStore(s, sword));
        Assert.True(PickFilter.ShouldSell(s, sword));
        sword.MagicOptions = [new MagicOption(1, new MagicOptionInfo(1, "MATTR_STR", "Str", 7)) { Value = 3 }];
        Assert.True(PickFilter.ShouldStore(s, sword));
        s.OnlyStoreWithBlues = true;
        s.BlueAttributes = ["Int"];
        Assert.False(PickFilter.ShouldStore(s, sword));
        s.BlueAttributes = ["str"];
        Assert.True(PickFilter.ShouldStore(s, sword));

        var stone = ItemFactory.Create(Stone);
        s.Set(Stone.ServerName, PickAction.Drop);
        Assert.True(PickFilter.ShouldDrop(s, stone));
        s.Set(Stone.ServerName, PickAction.Drop | PickAction.Sell);
        Assert.False(PickFilter.ShouldDrop(s, stone)); // sold instead
    }

    [Fact]
    public void The_town_plans_use_the_filter_with_the_name_lists()
    {
        var c = InGame();
        c.Inventory[13] = ItemFactory.Create(Sword);
        c.Inventory[14] = ItemFactory.Create(Stone);
        var town = new TownSettings();
        var filter = new PickFilterSettings();
        filter.Set(Sword.ServerName, PickAction.Store);
        filter.Set(Stone.ServerName, PickAction.Sell);
        Assert.Equal([13], TownRunner.StorePlan(c, town, filter));
        Assert.Equal([14], TownRunner.SellPlan(c, town, filter).Select(x => x.Slot));
        Assert.Empty(TownRunner.DropPlan(c, town, filter));
        town.Store = ["str stone"]; // the storage list wins over selling
        Assert.Empty(TownRunner.SellPlan(c, town, filter));
        Assert.Equal([14], TownRunner.StorePlan(c, town)); // without the filter: only the lists
    }

    [Fact]
    public void Gold_over_the_keep_amount_is_stored_and_taken_back_when_short()
    {
        var s = new PickFilterSettings { KeepGold = 1_000_000 };
        Assert.Equal(0, PickFilter.GoldToStore(s, 5_000_000, 0));
        s.StoreGold = true;
        Assert.Equal(4_000_000, PickFilter.GoldToStore(s, 5_000_000, 0));
        s.StoreGoldMax = 10_000_000;
        Assert.Equal(1_000_000, PickFilter.GoldToStore(s, 5_000_000, 9_000_000));
        Assert.Equal(0, PickFilter.GoldToStore(s, 5_000_000, 10_000_000));
        Assert.Equal(0, PickFilter.GoldToStore(s, 200_000, 3_000_000));
        s.TakeGoldFromStorage = true;
        Assert.Equal(-800_000, PickFilter.GoldToStore(s, 200_000, 3_000_000));
        Assert.Equal(-100_000, PickFilter.GoldToStore(s, 200_000, 100_000));
    }

    [Fact]
    public void Room_in_the_bag_and_pick_packets()
    {
        var bag = new SlotList<Item>(3);
        bag[0] = ItemFactory.Create(Sword);
        bag[1] = ItemFactory.Create(Arrow);
        bag[1]!.Quantity = 100;
        bag[2] = ItemFactory.Create(Stone);
        Assert.True(PickManager.HasRoom(bag, 0, DropOf(Arrow, 1, default))); // the arrow stack is not full
        Assert.True(PickManager.HasRoom(bag, 0, DropOf(Gold, 1, default)));
        Assert.False(PickManager.HasRoom(bag, 0, DropOf(Sword, 1, default)));

        Assert.Equal("01020139300000", Convert.ToHexString(ClientPackets.PickUp(12345).Payload));
        Assert.Equal("393000000839300000", Convert.ToHexString(ClientPackets.PetPickUp(12345, 12345).Payload));
        Assert.Equal("0C40420F000000000039300000", Convert.ToHexString(ClientPackets.StorageGold(12345, true, 1_000_000).Payload));
        Assert.Equal("0B", Convert.ToHexString(ClientPackets.StorageGold(1, false, 1).Payload)[..2]);
    }

    [Fact]
    public void Items_of_the_same_name_are_told_apart_and_skipped_drops_say_why()
    {
        Assert.Equal("Gold Ring (European, Seal of Sun)", PickFilter.Label("Gold Ring", "ITEM_EU_RING_03_C_RARE"));
        Assert.Equal("Gold Ring (event, Chinese, Seal of Star)", PickFilter.Label("Gold Ring", "ITEM_EVENT_CH_RING_03_A_RARE"));
        Assert.Equal("Gold", PickFilter.Label("Gold", "ITEM_ETC_GOLD_01"));

        var s = new PickFilterSettings();
        var ring = DropOf(RareSword, 1, default);
        Assert.Contains("Pick is not ticked", PickFilter.WhyNot(s, ring, false, 0, 5, []));
        s.Set(RareSword.ServerName, PickAction.Pick);
        Assert.Contains("Pick other player's items", PickFilter.WhyNot(s, ring, false, 0, 5, []));
        s.PickOtherPlayersItems = true;
        Assert.Null(PickFilter.WhyNot(s, ring, false, 0, 5, []));
        Assert.Contains("another player", PickFilter.WhyNot(s, DropOf(RareSword, 2, default, owner: 9), false, 0, 5, []));
    }

    [Fact]
    public void Settings_keep_items_by_server_name_whatever_the_case()
    {
        var s = new PickFilterSettings();
        s.Set("item_ch_sword_08_a", PickAction.Pick);
        Assert.Equal(PickAction.Pick, s.Get("ITEM_CH_SWORD_08_A"));
        s.Set("ITEM_CH_SWORD_08_A", PickAction.None);
        Assert.Empty(s.Items);
        Assert.Contains(PickFilter.Categories, c => c.Name == "Weapons");
    }

    [Fact]
    public void Gold_picked_up_by_the_character_or_the_grab_pet_is_read()
    {
        var client = new SroClient(WorldTests.Data(), NullLogger.Instance, _ => true);
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        var picked = new List<(ulong Gold, bool ByPet)>();
        client.GoldPicked += (_, e) => picked.Add((e.Gold, e.ByPet));
        // 0xB034 [01][06 ground to bag][FE gold][uint 1500]; [01][1C pet to bag][uint pet][FE][uint 250]
        client.HandlePacket(new Packet(0xB034, Convert.FromHexString("0106FEDC050000")));
        client.HandlePacket(new Packet(0xB034, Convert.FromHexString("011C39300000FEFA000000")));
        Assert.Equal([(1500ul, false), (250ul, true)], picked);
    }
}
