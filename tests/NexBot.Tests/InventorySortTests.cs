using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Automation;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>Auto sort: stacking the same items up to the maximum stack, then ordering the bag.</summary>
public class InventorySortTests
{
    private static readonly ItemInfo Global = new(3795, "ITEM_MALL_GLOBAL_CHATTING", "Global Chatting", 3, 3, 5, 1, 0, "", 50, 1);
    private static readonly ItemInfo HpPotion = new(5, "ITEM_ETC_HP_POTION_01", "HP Recovery Herb", 3, 1, 1, 0, 0, "", 250, 1);
    private static readonly ItemInfo Sword = new(3632, "ITEM_CH_SWORD_01_A", "Iron Sword", 1, 6, 2, 0, 0, "", 1, 1);

    private static Item Make(ItemInfo info, ushort quantity = 1)
    {
        var item = ItemFactory.Create(info);
        item.Quantity = quantity;
        return item;
    }

    /// <summary>A client whose fake server accepts every 0x7034 like the real one (moved amount echoed back).</summary>
    private static SroClient ClientWithBag(params (int Slot, Item Item)[] items)
    {
        SroClient live = null;
        var client = new SroClient(WorldTests.Data(), NullLogger.Instance, p =>
        {
            if (p.Opcode != Opcodes.Client.ItemMovement)
                return true;
            byte type = p.ReadByte(), from = p.ReadByte(), to = p.ReadByte();
            ushort amount = p.ReadUInt16();
            if (amount == 0)
                amount = live.World.Character!.Inventory[from]?.Quantity ?? 1;
            _ = Task.Run(() => live.HandlePacket(new Packet(Opcodes.Server.ItemMovementResponse)
                .WriteByte(1).WriteByte(type).WriteByte(from).WriteByte(to).WriteUInt16(amount).WriteByte(0)));
            return true;
        });
        live = client;
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
        var bag = new SlotList<Item>(13 + 32);
        foreach (var (slot, item) in items)
            bag[slot] = item;
        client.World.Character!.Inventory = bag;
        return client;
    }

    private static string Describe(SlotList<Item> bag, int from, int count) =>
        string.Join(" | ", Enumerable.Range(from, count).Select(s => bag[s] is { } i ? $"{i.Name} x{i.Quantity}" : "-"));

    [Fact]
    public void Planner_merges_into_the_first_stack_up_to_the_maximum()
    {
        var bag = new SlotList<Item>(45);
        bag[13] = Make(Global, 39);
        bag[20] = Make(Global, 20);
        var step = InventorySorter.NextMerge(bag);
        Assert.Equal(new SortStep(20, 13, 11, true), step); // only 11 fit (50 max)

        bag[13].Quantity = 50; // full now: nothing left to merge with
        bag[20].Quantity = 9;
        Assert.Null(InventorySorter.NextMerge(bag));
    }

    [Fact]
    public async Task Auto_sort_stacks_then_orders_the_bag()
    {
        using var client = ClientWithBag(
            (13, Make(Global, 39)),
            (14, Make(HpPotion, 100)),
            (16, Make(Global, 11)),
            (17, Make(Sword)),
            (18, Make(HpPotion, 50)),
            (25, Make(Global, 30)));

        var result = await InventorySorter.RunAsync(client, pause: TimeSpan.Zero);

        Assert.True(result.Ok, result.Message);
        var bag = client.World.Character!.Inventory;
        // Equipment first, then potions, then the chat items: 39 + 11 = 50 in one slot, 30 next to it.
        Assert.Equal("Iron Sword x1 | HP Recovery Herb x150 | Global Chatting x50 | Global Chatting x30 | - | -", Describe(bag, 13, 6));
        Assert.Equal(4, bag.Skip(13).Count(i => i is not null)); // 6 stacks became 4
        Assert.Equal(2, result.Merged);

        // A second run has nothing to do.
        var again = await InventorySorter.RunAsync(client, pause: TimeSpan.Zero);
        Assert.Equal("The inventory is already sorted.", again.Message);
    }
}
