using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;

namespace NexBot.Sessions;

/// <summary>
/// Exchanging with another player, as the game's exchange window: ask (or accept), put items and gold in, Confirm,
/// then approve once both sides confirmed. The window's state is <see cref="WorldState.Exchange"/>.
/// </summary>
public sealed partial class SroClient
{
    /// <summary>Asks a nearby player to exchange; the window opens when they accept (0x3085).</summary>
    public bool RequestExchange(uint playerUniqueId)
    {
        World.ExchangeError = null;
        World.LastExchange = null;
        return Send(ClientPackets.ExchangeRequest(playerUniqueId));
    }

    /// <summary>Answers the exchange request a player sent us.</summary>
    public bool AnswerExchangeRequest(bool accept)
    {
        World.ExchangeRequest = null;
        World.ExchangeError = null;
        return RespondToPetition(accept, PlayerPetition.ExchangeRequest);
    }

    /// <summary>Puts a bag item (the whole stack) into the exchange window and waits for the answer.</summary>
    public async Task<ItemMoveResult> ExchangeAddItemAsync(byte inventorySlot, CancellationToken cancellationToken = default)
    {
        if (World.Exchange is not { } exchange)
            return new(false, "No exchange is open.");
        if (exchange.MyConfirmed)
            return new(false, "You already confirmed: the window is locked.");
        if (exchange.MyItems.ContainsKey(inventorySlot))
            return new(false, "That item is already in the window.");
        if (exchange.MyItems.Count >= ExchangeState.Slots)
            return new(false, "The exchange window is full (12 items).");
        if (World.Character?.Inventory[inventorySlot] is not { } item)
            return new(false, "That bag slot is empty.");
        var result = await ExchangeMoveAsync(ClientPackets.ExchangeAddItem(inventorySlot), ItemMovementType.InventoryToExchange,
            "put in the exchange", cancellationToken).ConfigureAwait(false);
        // The window's list (0x308C) normally follows; until it does, show the item.
        if (result.Ok && World.Exchange is { } open && !open.MyItems.ContainsKey(inventorySlot))
        {
            open.MyItems = new Dictionary<byte, Item>(open.MyItems) { [inventorySlot] = item };
            open.Version++;
        }
        return result.Ok ? new(true, $"{item.Name} is in the exchange.") : result;
    }

    /// <summary>Takes an item back out of the exchange window (its place in the window, from 0).</summary>
    public async Task<ItemMoveResult> ExchangeRemoveItemAsync(int windowSlot, CancellationToken cancellationToken = default)
    {
        if (World.Exchange is not { } exchange)
            return new(false, "No exchange is open.");
        if (exchange.MyConfirmed)
            return new(false, "You already confirmed: the window is locked.");
        if (windowSlot < 0 || windowSlot >= exchange.MyItems.Count)
            return new(false, "Nothing there.");
        byte bagSlot = exchange.MyItems.Keys.ElementAt(windowSlot);
        var result = await ExchangeMoveAsync(ClientPackets.ExchangeRemoveItem((byte)windowSlot), ItemMovementType.ExchangeToInventory,
            "take out of the exchange", cancellationToken).ConfigureAwait(false);
        if (result.Ok && World.Exchange is { } open && open.MyItems.ContainsKey(bagSlot))
        {
            var mine = new Dictionary<byte, Item>(open.MyItems);
            mine.Remove(bagSlot);
            open.MyItems = mine;
            open.Version++;
        }
        return result.Ok ? new(true, "Taken out of the exchange.") : result;
    }

    /// <summary>Sets the gold we offer in the exchange window.</summary>
    public async Task<ItemMoveResult> ExchangeGoldAsync(ulong gold, CancellationToken cancellationToken = default)
    {
        if (World.Exchange is not { } exchange)
            return new(false, "No exchange is open.");
        if (exchange.MyConfirmed)
            return new(false, "You already confirmed: the window is locked.");
        if (World.Character is { } c && gold > c.Gold)
            return new(false, $"You have only {c.Gold:N0} gold.");
        var result = await ExchangeMoveAsync(ClientPackets.ExchangeGold(gold), ItemMovementType.InventoryGoldToExchange,
            "put gold in the exchange", cancellationToken).ConfigureAwait(false);
        if (result.Ok && World.Exchange is { } open)
        {
            open.MyGold = gold;
            open.Version++;
        }
        return result.Ok ? new(true, $"{gold:N0} gold is in the exchange.") : result;
    }

    /// <summary>Confirm (locks our side). After both sides confirmed, <see cref="ExchangeApprove"/> finishes it.</summary>
    public bool ExchangeConfirm() => World.Exchange is not null && Send(ClientPackets.ExchangeConfirm());

    public bool ExchangeApprove() => World.Exchange is not null && Send(ClientPackets.ExchangeApprove());

    public bool ExchangeCancel() => World.Exchange is not null && Send(ClientPackets.ExchangeCancel());

    private async Task<ItemMoveResult> ExchangeMoveAsync(Packet packet, ItemMovementType type, string what, CancellationToken cancellationToken)
    {
        await _moveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (result, sent) = await SendMovePacketAsync(packet, type, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (result is null)
                return new(false, sent is null ? "The bot is not connected." : $"The server did not answer ({what}).");
            if (result.Success)
                return new(true, "Done.");
            Logger.LogWarning("Exchange: {What} refused: sent 0x7034 [{Sent}], error 0x{Error:X4}", what, sent, result.ErrorCode);
            return new(false, ItemErrors.Format(what, result.ErrorCode), result.ErrorCode);
        }
        finally
        {
            _moveGate.Release();
        }
    }
}
