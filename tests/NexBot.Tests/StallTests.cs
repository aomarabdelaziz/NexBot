using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>Stall: planning what to stall, the packets, opening against a fake server, sales.</summary>
public class StallTests
{
    private static readonly ItemInfo Elixir = new(3795, "ITEM_ETC_ARCHEMY_REINFORCE_RECIPE_WEAPON_B", "Elixir (weapon)", 3, 3, 10, 0, 0, "", 50, 1);
    private static readonly ItemInfo Global = new(3851, "ITEM_ETC_GLOBAL_CHATTING", "Global chatting", 3, 3, 13, 0, 0, "", 50, 1);
    private static readonly ItemInfo Junk = new(9101, "ITEM_ETC_JUNK", "Junk", 3, 3, 9, 0, 0, "", 50, 1);

    private static (SroClient Client, ConcurrentQueue<Packet> Sent) Client(bool answer = true)
    {
        var data = WorldTests.Data();
        foreach (var i in new[] { Elixir, Global, Junk })
            data.Items[i.Id] = i;
        var sent = new ConcurrentQueue<Packet>();
        SroClient? client = null;
        client = new SroClient(data, NullLogger.Instance, p =>
        {
            sent.Enqueue(p);
            if (answer && p.Opcode is Opcodes.Client.StallCreate or Opcodes.Client.StallUpdate or Opcodes.Client.StallDestroy)
            {
                ushort reply = p.Opcode switch
                {
                    Opcodes.Client.StallCreate => Opcodes.Server.StallCreateResponse,
                    Opcodes.Client.StallDestroy => Opcodes.Server.StallDestroyResponse,
                    _ => Opcodes.Server.StallUpdateResponse,
                };
                client!.HandlePacket(new Packet(reply).WriteByte(1));
            }
            return true;
        });
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
        var inventory = client.World.Character!.Inventory;
        for (int i = Character.FirstInventorySlot; i < inventory.Capacity; i++)
            inventory[i] = null;
        return (client, sent);
    }

    private static void Put(SroClient client, int slot, ItemInfo info, ushort quantity)
    {
        var item = ItemFactory.Create(info);
        item.Quantity = quantity;
        client.World.Character!.Inventory[slot] = item;
    }

    [Fact]
    public void Plan_takes_priced_items_and_splits_filter_stacks()
    {
        var (client, _) = Client();
        using var _c = client;
        Put(client, 13, Elixir, 30);
        Put(client, 14, Junk, 5);     // no price
        Put(client, 15, Global, 7);
        var settings = new StallSettings();
        settings.Get(Elixir.ServerName).Price = 1_000_000;
        settings.Get(Elixir.ServerName).Quantity = 10; // three slots of 10
        settings.Get(Global.ServerName).Price = 250_000; // the whole stack

        var plan = StallManager.Plan(client, settings);

        Assert.Equal(4, plan.Count);
        // Stacks that fit as they are come first, then the splits.
        Assert.Equal((byte)15, plan[0].InventorySlot);
        Assert.Equal(7, plan[0].Quantity);
        Assert.Equal(250_000ul, plan[0].Price);
        Assert.Equal([(ushort?)10, 10, null], plan.Skip(1).Select(p => p.SplitOff));
        Assert.All(plan.Skip(1), p => Assert.Equal(10, p.Quantity));

        settings.Get(Global.ServerName).Enabled = false;
        Assert.Equal(3, StallManager.Plan(client, settings).Count);
    }

    [Fact]
    public void Stall_packets_match_the_client()
    {
        Assert.Equal("020D0E0700A086010000000000320000000000",
            Convert.ToHexString(ClientPackets.StallAddItem(0x0D, 0x0E, 7, 100_000).Payload));
        Assert.Equal("0501" + "0000", Convert.ToHexString(ClientPackets.StallState(true).Payload));
        Assert.Equal("030200" + "00", Convert.ToHexString(ClientPackets.StallRemoveItem(2).Payload));
        Assert.Equal(Opcodes.Client.StallCreate, ClientPackets.StallCreate("Shop").Opcode);
    }

    [Fact]
    public async Task Open_creates_fills_and_opens_the_stall()
    {
        var (client, sent) = Client();
        using var _c = client;
        Put(client, 13, Global, 7);
        Put(client, 16, Junk, 1);
        var settings = new StallSettings { Title = "Cheap globals", Note = "PM me" };
        settings.Get(Global.ServerName).Price = 250_000;
        using var manager = new StallManager(client, settings, startLoop: false) { StepDelay = TimeSpan.Zero };

        var result = await manager.OpenAsync();

        Assert.True(result.Ok, result.Message);
        Assert.True(client.Stall.Exists);
        Assert.True(client.Stall.IsOpen);
        var slot = Assert.Single(client.Stall.Slots);
        Assert.Equal((byte)13, slot.InventorySlot);
        Assert.Equal(250_000ul, slot.Price);
        Assert.Equal(
            [Opcodes.Client.StallCreate, Opcodes.Client.StallUpdate, Opcodes.Client.StallUpdate, Opcodes.Client.StallUpdate],
            sent.Select(p => p.Opcode));
        Assert.Equal(6, StallUpdateTypeOf(sent.ElementAt(1))); // note
        Assert.Equal(5, StallUpdateTypeOf(sent.ElementAt(3))); // open
    }

    private static byte StallUpdateTypeOf(Packet p) => p.Payload[0];

    [Fact]
    public async Task A_sale_removes_the_item_and_is_reported()
    {
        var (client, _) = Client();
        using var _c = client;
        Put(client, 13, Global, 7);
        var settings = new StallSettings();
        settings.Get(Global.ServerName).Price = 250_000;
        using var manager = new StallManager(client, settings, startLoop: false) { StepDelay = TimeSpan.Zero };
        Assert.True((await manager.OpenAsync()).Ok);
        StallSoldEventArgs? sold = null;
        client.StallSold += (_, e) => sold = e;

        client.HandlePacket(new Packet(Opcodes.Server.StallEntityAction).WriteByte(3).WriteByte(0).WriteAscii("Buyer"));

        Assert.NotNull(sold);
        Assert.Equal("Buyer", sold!.Buyer);
        Assert.Equal(250_000ul, sold.Slot.Price);
        Assert.Empty(client.Stall.Slots);
        Assert.Null(client.World.Character!.Inventory[13]);
    }

    [Fact]
    public async Task A_sold_out_stall_opened_by_hand_closes_when_nothing_is_left()
    {
        var (client, sent) = Client();
        using var _c = client;
        Put(client, 13, Global, 7);
        var settings = new StallSettings { AutoStall = false, RefillWhenSoldOut = true };
        settings.Get(Global.ServerName).Price = 250_000;
        using var manager = new StallManager(client, settings, startLoop: false) { StepDelay = TimeSpan.Zero };
        Assert.True((await manager.OpenAsync()).Ok);
        client.HandlePacket(new Packet(Opcodes.Server.StallEntityAction).WriteByte(3).WriteByte(0).WriteAscii("Buyer"));

        await manager.StepAsync(CancellationToken.None);

        Assert.False(client.Stall.Exists);
        Assert.Equal(Opcodes.Client.StallDestroy, sent.Last().Opcode);
    }

    [Fact]
    public async Task A_sold_out_stall_is_filled_again_with_what_is_left()
    {
        var (client, sent) = Client();
        using var _c = client;
        Put(client, 13, Global, 7);
        var settings = new StallSettings { AutoStall = false, RefillWhenSoldOut = true };
        settings.Get(Global.ServerName).Price = 250_000;
        settings.Get(Junk.ServerName).Price = 1_000;
        using var manager = new StallManager(client, settings, startLoop: false) { StepDelay = TimeSpan.Zero };
        Assert.True((await manager.OpenAsync()).Ok);
        client.HandlePacket(new Packet(Opcodes.Server.StallEntityAction).WriteByte(3).WriteByte(0).WriteAscii("Buyer"));
        Put(client, 16, Junk, 4); // more priced items in the bag
        sent.Clear();

        await manager.StepAsync(CancellationToken.None);

        Assert.True(client.Stall.Exists);
        Assert.True(client.Stall.IsOpen);
        Assert.Equal((byte)16, Assert.Single(client.Stall.Slots).InventorySlot);
        var order = sent.Select(p => p.Opcode).ToList();
        Assert.True(order.IndexOf(Opcodes.Client.StallDestroy) < order.IndexOf(Opcodes.Client.StallCreate), "closed, then opened again");
    }

    [Fact]
    public async Task A_refused_add_stops_after_the_first_item()
    {
        var (client, sent) = Client(answer: false);
        using var _c = client;
        Put(client, 13, Global, 7);   // fits a slot as it is
        Put(client, 14, Junk, 5);     // fits a slot as it is
        var settings = new StallSettings();
        settings.Get(Global.ServerName).Price = 250_000;
        settings.Get(Junk.ServerName).Price = 1_000;
        using var manager = new StallManager(client, settings, startLoop: false) { StepDelay = TimeSpan.Zero };
        _ = Task.Run(async () =>
        {
            // Accept the create, refuse every add (as the server did with 0x3C2A).
            int answered = 0;
            for (int i = 0; i < 400; i++)
            {
                await Task.Delay(10);
                if (sent.Count <= answered)
                    continue;
                var last = sent.ElementAt(answered++);
                client.HandlePacket(last.Opcode switch
                {
                    Opcodes.Client.StallCreate => new Packet(Opcodes.Server.StallCreateResponse).WriteByte(1),
                    Opcodes.Client.StallDestroy => new Packet(Opcodes.Server.StallDestroyResponse).WriteByte(1),
                    _ => new Packet(Opcodes.Server.StallUpdateResponse).WriteByte(2).WriteUInt16(0x3C2A),
                });
            }
        });

        var result = await manager.OpenAsync();

        Assert.False(result.Ok);
        Assert.Equal(0x3C2A, result.ErrorCode);
        Assert.Single(sent, p => p.Opcode == Opcodes.Client.StallUpdate); // one add tried, then it stopped
    }

    [Fact]
    public async Task Stacks_are_split_before_the_stall_is_created()
    {
        // The game refuses moving items while a stall is up (0x1834), so "Per slot" splits come first.
        var (client, sent) = Client(answer: false);
        using var _c = client;
        Put(client, 13, Global, 7);   // fits a slot as it is
        Put(client, 14, Elixir, 30);  // split into slots of 10
        var settings = new StallSettings();
        settings.Get(Global.ServerName).Price = 250_000;
        settings.Get(Elixir.ServerName).Price = 1_000_000;
        settings.Get(Elixir.ServerName).Quantity = 10;
        using var manager = new StallManager(client, settings, startLoop: false) { StepDelay = TimeSpan.Zero };
        _ = Task.Run(async () =>
        {
            int answered = 0;
            for (int i = 0; i < 400; i++)
            {
                await Task.Delay(10);
                if (sent.Count <= answered)
                    continue;
                var last = sent.ElementAt(answered++);
                client.HandlePacket(last.Opcode switch
                {
                    Opcodes.Client.ItemMovement => new Packet(Opcodes.Server.ItemMovementResponse).WriteByte(2).WriteUInt16(0x1834),
                    Opcodes.Client.StallCreate => new Packet(Opcodes.Server.StallCreateResponse).WriteByte(1),
                    Opcodes.Client.StallDestroy => new Packet(Opcodes.Server.StallDestroyResponse).WriteByte(1),
                    _ => new Packet(Opcodes.Server.StallUpdateResponse).WriteByte(1),
                });
            }
        });

        await manager.OpenAsync();

        var order = sent.Select(p => p.Opcode).ToList();
        int firstMove = order.IndexOf(Opcodes.Client.ItemMovement), create = order.IndexOf(Opcodes.Client.StallCreate);
        Assert.True(firstMove >= 0, "a split was tried");
        Assert.True(create > firstMove, "the stall is created after the splits");
        Assert.Equal(30, client.World.Character!.Inventory[14]!.Quantity); // refused splits leave the stack whole
    }

    [Fact]
    public void The_add_layout_is_learned_from_the_game_client()
    {
        var (client, _) = Client();
        using var _c = client;
        string? learned = null;
        client.StallLayoutLearned += (_, e) => learned = e.TrailerHex;

        // The game client adds an item with nothing after the price (a server without the flea market group).
        client.ObservePacketToServer(new Packet(Opcodes.Client.StallUpdate).WriteByte(2).WriteByte(0).WriteByte(13).WriteUInt16(1).WriteUInt64(5000), true);

        Assert.Equal("", learned);
        Assert.Empty(client.StallAddTrailer);
        Assert.Equal("02000D01008813000000000000",
            Convert.ToHexString(ClientPackets.StallAddItem(0, 13, 1, 5000, client.StallAddTrailer).Payload));
    }

    [Fact]
    public async Task Refused_create_is_reported_and_auto_stall_waits()
    {
        var (client, sent) = Client(answer: false);
        using var _c = client;
        Put(client, 13, Global, 7);
        var settings = new StallSettings { AutoStall = true };
        settings.Get(Global.ServerName).Price = 250_000;
        using var manager = new StallManager(client, settings, startLoop: false) { StepDelay = TimeSpan.Zero };

        var step = manager.StepAsync(CancellationToken.None);
        while (!sent.Any(p => p.Opcode == Opcodes.Client.StallCreate))
            await Task.Delay(10);
        client.HandlePacket(new Packet(Opcodes.Server.StallCreateResponse).WriteByte(2).WriteUInt16(0x3C0A)); // not allowed here
        await step;

        Assert.False(client.Stall.Exists);
        await manager.StepAsync(CancellationToken.None); // waits RetryDelay: nothing sent
        Assert.Single(sent, p => p.Opcode == Opcodes.Client.StallCreate);
    }
}
