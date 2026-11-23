using NexBot.Game;
using NexBot.Game.Items;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Network;

/// <summary>
/// Keeps the game client's inventory right when the bot shops by itself. The client cannot take the server's shop
/// answers (0xB034 type 8 "bought", type 9 "sold") for a shop it never opened: it crashes. Those answers are kept from
/// it, and it is told the result in forms it takes without a shop: "picked up into this slot" (0xB034 type 6, for a
/// new stack), "this stack is now N" (0x3040 type 8) and "dropped from this slot" (0xB034 type 7, sold out or put into
/// the storage: type 2 is kept from it too, as the game never opened the storage window).
/// </summary>
internal sealed class ShopReplySync
{
    private readonly byte _type;
    private readonly List<(byte Slot, uint ItemId, ushort Quantity)> _before = [];

    private ShopReplySync(byte type) => _type = type;

    /// <summary>Notes the slots a successful buy / sell answer changes, before the bot applies it; null for others.</summary>
    public static ShopReplySync? Before(SroClient game, Packet answer)
    {
        var p = answer.Payload;
        if (p.Length < 3 || p[0] != 1 || game.World.Character?.Inventory is not { } inventory)
            return null;
        var sync = new ShopReplySync(p[1]);
        switch ((ItemMovementType)p[1])
        {
            case ItemMovementType.ShopToInventory when p.Length >= 5:
            {
                int count = p[4];
                for (int i = 0; i < count && 5 + i < p.Length; i++)
                    sync.Note(inventory, p[5 + i]);
                return sync;
            }
            case ItemMovementType.InventoryToShop:
            case ItemMovementType.InventoryToStorage:
                sync.Note(inventory, p[2]);
                return sync;
            default:
                return null;
        }
    }

    private void Note(SlotList<Item> inventory, byte slot)
    {
        var item = slot < inventory.Capacity ? inventory[slot] : null;
        _before.Add((slot, item?.Id ?? 0, item?.Quantity ?? 0));
    }

    /// <summary>What to send the client instead, after the bot applied the answer.</summary>
    public List<Packet> After(SroClient game)
    {
        var packets = new List<Packet>();
        if (game.World.Character?.Inventory is not { } inventory)
            return packets;
        foreach (var (slot, beforeId, _) in _before)
        {
            var now = slot < inventory.Capacity ? inventory[slot] : null;
            if (_type is (byte)ItemMovementType.InventoryToShop or (byte)ItemMovementType.InventoryToStorage)
            {
                packets.Add(now is null
                    ? new Packet(Opcodes.Server.ItemMovementResponse).WriteByte(1).WriteByte((byte)ItemMovementType.InventoryToGround).WriteByte(slot)
                    : QuantityUpdate(slot, now.Quantity));
                continue;
            }
            if (now is null)
                continue;
            if (beforeId == now.Id)
                packets.Add(QuantityUpdate(slot, now.Quantity));
            else if (now is EtcItem { IsAlchemy: false } etc && etc.Rentable.Type == RentableType.None && !(etc.TypeId3 == 14 && etc.TypeId4 == 2))
                packets.Add(new Packet(Opcodes.Server.ItemMovementResponse)
                    .WriteByte(1).WriteByte((byte)ItemMovementType.GroundToInventory).WriteByte(slot)
                    .WriteUInt32(0) // not rented
                    .WriteUInt32(etc.Id)
                    .WriteUInt16(etc.Quantity));
            // Other goods (equipment bought from an NPC) are not shown to the client until it relogs.
        }
        return packets;
    }

    private static Packet QuantityUpdate(byte slot, ushort quantity) =>
        new Packet(Opcodes.Server.ItemUpdate).WriteByte(slot).WriteByte(8).WriteUInt16(quantity);
}
