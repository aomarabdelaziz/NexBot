using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Data;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>
/// The game client is told the result of the bot's own shopping in forms it takes without an open shop (it crashed on
/// the server's 0xB034 type 8 answer).
/// </summary>
public class ShopReplySyncTests
{
    private static readonly ItemInfo Kit = new(2148, "ITEM_ETC_COS_HP_POTION_03", "Recovery Kit (x-large)", 3, 1, 4, 0, 0, "", 50, 1);

    private static SroClient Client(out int slot)
    {
        var client = new SroClient(WorldTests.Data(), NullLogger.Instance, _ => true);
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        var inventory = client.World.Character!.Inventory;
        slot = Enumerable.Range(13, inventory.Capacity - 13).First(i => inventory[i] is null);
        return client;
    }

    private static Item KitOf(ushort quantity)
    {
        var item = ItemFactory.Create(Kit);
        item.Quantity = quantity;
        return item;
    }

    private static Packet Answer(params byte[] payload) => new(Opcodes.Server.ItemMovementResponse, payload);

    [Fact]
    public void A_new_stack_bought_is_shown_as_picked_up()
    {
        using var client = Client(out int slot);
        var sync = ShopReplySync.Before(client, Answer(1, 8, 3, 6, 1, (byte)slot, 50, 0, 0, 0, 0, 0))!;
        client.World.Character!.Inventory[slot] = KitOf(50); // what the bot applies

        var packet = Assert.Single(sync.After(client));

        Assert.Equal(Opcodes.Server.ItemMovementResponse, packet.Opcode);
        Assert.Equal(new byte[] { 1, 6, (byte)slot, 0, 0, 0, 0, 0x64, 0x08, 0, 0, 50, 0 }, packet.Payload.ToArray());
    }

    [Fact]
    public void Bought_onto_a_stack_is_shown_as_its_new_quantity()
    {
        using var client = Client(out int slot);
        client.World.Character!.Inventory[slot] = KitOf(20);
        var sync = ShopReplySync.Before(client, Answer(1, 8, 3, 6, 1, (byte)slot, 30, 0, 0, 0, 0, 0))!;
        client.World.Character.Inventory[slot] = KitOf(50);

        var packet = Assert.Single(sync.After(client));

        Assert.Equal(Opcodes.Server.ItemUpdate, packet.Opcode);
        Assert.Equal(new byte[] { (byte)slot, 8, 50, 0 }, packet.Payload.ToArray());
    }

    [Fact]
    public void A_stack_sold_out_is_shown_as_dropped_and_part_of_one_as_its_new_quantity()
    {
        using var client = Client(out int slot);
        var inventory = client.World.Character!.Inventory;
        inventory[slot] = KitOf(20);
        var all = ShopReplySync.Before(client, Answer(1, 9, (byte)slot, 20, 0, 0xCD, 0, 0, 0, 0))!;
        inventory[slot] = null;
        Assert.Equal(new byte[] { 1, 7, (byte)slot }, Assert.Single(all.After(client)).Payload.ToArray());

        inventory[slot] = KitOf(20);
        var part = ShopReplySync.Before(client, Answer(1, 9, (byte)slot, 5, 0, 0xCD, 0, 0, 0, 0))!;
        inventory[slot] = KitOf(15);
        Assert.Equal(new byte[] { (byte)slot, 8, 15, 0 }, Assert.Single(part.After(client)).Payload.ToArray());
    }

    [Fact]
    public void A_stack_put_into_the_storage_is_shown_as_dropped()
    {
        using var client = Client(out int slot);
        client.World.Character!.Inventory[slot] = KitOf(50);
        var sync = ShopReplySync.Before(client, Answer(1, 2, (byte)slot, 0))!;
        client.World.Character.Inventory[slot] = null;

        var packet = Assert.Single(sync.After(client));
        Assert.Equal(new byte[] { 1, 7, (byte)slot }, packet.Payload.ToArray());
    }

    [Fact]
    public void A_refused_buy_shows_the_client_nothing()
    {
        using var client = Client(out _);
        Assert.Null(ShopReplySync.Before(client, Answer(2, 0x18, 0x18)));
    }
}
