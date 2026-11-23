using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Handlers;

/// <summary>
/// The exchange window between two players (vSRO): 0x3085 opened, 0x308C the items of one side, 0x3089 the other
/// side's gold, 0x3086 the other side confirmed, 0x3087 done, 0x3088 cancelled; 0xB081..0xB084 answer our requests.
/// </summary>
internal static class ExchangeHandlers
{
    /// <summary>0x3085: the exchange window opened with a player [uint player].</summary>
    public static void Started(SroClient client, Packet p) =>
        Open(client, p.Remaining >= 4 ? p.ReadUInt32() : client.World.ExchangeRequest?.Id ?? 0);

    /// <summary>The window opened with <paramref name="partner"/> (kept when it is already open with them).</summary>
    private static void Open(SroClient client, uint partner)
    {
        if (client.World.Exchange is { } open && open.PartnerId == partner)
            return;
        string name = client.World.GetEntity(partner) is Player player ? player.Name
            : client.World.ExchangeRequest is { } request && request.Id == partner ? request.Name : $"player {partner}";
        client.World.ExchangeRequest = null;
        client.World.Exchange = new ExchangeState(partner, name);
        client.Logger.LogInformation("Exchange: started with {Name}", name);
    }

    /// <summary>
    /// 0xB081: answer to our exchange request: 1 and the player (they accepted: the window is open; the asking side
    /// gets no 0x3085), or 2 and an error.
    /// </summary>
    public static void RequestAnswer(SroClient client, Packet p)
    {
        if (p.Remaining >= 1 && p.ReadByte() == 1)
        {
            if (p.Remaining >= 4)
                Open(client, p.ReadUInt32());
        }
        else
        {
            ushort error = p.Remaining >= 2 ? p.ReadUInt16() : (ushort)0;
            client.World.ExchangeError = $"The exchange request was refused (error 0x{error:X4}): the player may be too far, busy, or said no.";
            client.Logger.LogWarning("Exchange: request refused, error 0x{Error:X4}", error);
        }
    }

    /// <summary>0x308C: the items one side has in the window [uint owner][byte count] then (slot, item) ...</summary>
    public static void Items(SroClient client, Packet p)
    {
        if (client.World.Exchange is not { } exchange)
            return;
        uint owner = p.ReadUInt32();
        byte count = p.ReadByte();
        var items = new List<(byte Slot, Item Item)>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                byte slot = p.ReadByte();
                items.Add((slot, p.ReadItem(client.Data)));
            }
        }
        catch (Exception ex) when (ex is PacketException or UnknownGameObjectException)
        {
            client.Logger.LogWarning("Exchange: the items could not be read ({Message}): {Payload}", ex.Message, Convert.ToHexString(p.Payload));
            return;
        }
        if (owner == client.World.PlayerUniqueId || owner == client.World.Character?.UniqueId)
        {
            // The slots are the bag slots the items came from.
            var mine = new Dictionary<byte, Item>();
            foreach (var (slot, item) in items)
                mine[slot] = item;
            exchange.MyItems = mine;
        }
        else
        {
            exchange.PartnerItems = [.. items.Select(x => x.Item)];
        }
        exchange.Version++;
    }

    /// <summary>0x3089: the other side's gold [byte 2][ulong gold].</summary>
    public static void Gold(SroClient client, Packet p)
    {
        if (client.World.Exchange is not { } exchange)
            return;
        if (p.Remaining >= 9)
            p.ReadByte();
        if (p.Remaining >= 8)
        {
            exchange.PartnerGold = p.ReadUInt64();
            exchange.Version++;
        }
    }

    /// <summary>0x3086: the other side pressed Confirm.</summary>
    public static void PartnerConfirmed(SroClient client, Packet p)
    {
        if (client.World.Exchange is not { } exchange)
            return;
        exchange.PartnerConfirmed = true;
        exchange.Version++;
    }

    /// <summary>0xB082: answer to our Confirm.</summary>
    public static void ConfirmAnswer(SroClient client, Packet p)
    {
        if (client.World.Exchange is not { } exchange)
            return;
        if (p.Remaining >= 1 && p.ReadByte() == 1)
            exchange.MyConfirmed = true;
        else
            client.World.ExchangeError = "The server did not accept Confirm.";
        exchange.Version++;
    }

    /// <summary>0xB083: answer to our approval.</summary>
    public static void ApproveAnswer(SroClient client, Packet p)
    {
        if (client.World.Exchange is not { } exchange)
            return;
        if (p.Remaining >= 1 && p.ReadByte() == 1)
            exchange.MyApproved = true;
        else
            client.World.ExchangeError = "The server did not accept the approval.";
        exchange.Version++;
    }

    /// <summary>
    /// 0x3087: both approved and the server swapped the items: ours leave the bag, theirs go into the first free
    /// slots (as the game does), and the gold changes hands.
    /// </summary>
    public static void Completed(SroClient client, Packet p)
    {
        var exchange = client.World.Exchange;
        client.World.Exchange = null;
        if (exchange is null || client.World.Character is not { } c)
            return;
        foreach (byte slot in exchange.MyItems.Keys)
            if (slot < c.Inventory.Capacity)
                c.Inventory[slot] = null;
        foreach (var item in exchange.PartnerItems)
        {
            int free = -1;
            for (int s = Character.FirstInventorySlot; s < c.Inventory.Capacity; s++)
            {
                if (c.Inventory[s] is null)
                {
                    free = s;
                    break;
                }
            }
            if (free < 0)
                break;
            c.Inventory[free] = item;
        }
        c.Gold = c.Gold - Math.Min(c.Gold, exchange.MyGold) + exchange.PartnerGold;
        client.World.LastExchange = $"Exchange with {exchange.PartnerName} done: gave {Describe(exchange.MyItems.Values, exchange.MyGold)}, got {Describe(exchange.PartnerItems, exchange.PartnerGold)}.";
        client.Logger.LogInformation("{Message}", client.World.LastExchange);
    }

    /// <summary>0xB084: answer to our cancel: 1 (closed), or 2 and an error (nothing to cancel).</summary>
    public static void CancelAnswer(SroClient client, Packet p)
    {
        if (p.Remaining >= 1 && p.ReadByte() == 1)
            Canceled(client, p);
    }

    /// <summary>0x3088: the other side or the server cancelled [ushort reason].</summary>
    public static void Canceled(SroClient client, Packet p)
    {
        if (client.World.Exchange is not { } exchange)
            return;
        client.World.Exchange = null;
        client.World.LastExchange = $"The exchange with {exchange.PartnerName} was cancelled.";
        client.Logger.LogInformation("{Message}", client.World.LastExchange);
    }

    internal static string Describe(IEnumerable<Item> items, ulong gold)
    {
        var parts = items.Select(i => i.Quantity > 1 ? $"{i.Quantity} x {i.Name}" : i.Name).ToList();
        if (gold > 0)
            parts.Add($"{gold:N0} gold");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }
}
