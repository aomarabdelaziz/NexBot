using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;

namespace NexBot.Sessions;

/// <summary>Trading: NPC shops (buy / sell, also into and out of a transport), riding, the job suit.</summary>
public sealed partial class SroClient
{
    private static readonly TimeSpan ShopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Our summoned trade transport (or other transport), if any.</summary>
    public Cos? Transport => World.MyPets.Values.FirstOrDefault(p => p.IsTransport);

    /// <summary>Our summoned horse or transport, if any.</summary>
    public Cos? Mount => SummonedMount();

    /// <summary>Selects an NPC and opens it with a talk option (12 specialty goods, 1 store), like a click in the game.</summary>
    public async Task OpenNpcAsync(uint npcUniqueId, byte talkOption, CancellationToken cancellationToken = default)
    {
        SelectEntity(npcUniqueId);
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        TalkToNpc(npcUniqueId, talkOption);
        await Task.Delay(600, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Buys one purchase from the open NPC shop, into the transport or the inventory, and waits for the answer.</summary>
    public async Task<ItemMoveResult> BuyAsync(uint npcUniqueId, bool intoTransport, byte tab, byte slot, ushort quantity,
        CancellationToken cancellationToken = default)
    {
        if (!IsInGame || World.Character is null)
            return new(false, "The character is not in game.");
        if (intoTransport && Transport is null)
            return new(false, "No transport is summoned.");
        var (type, packet) = intoTransport
            ? (ItemMovementType.ShopToTransport, ClientPackets.BuyFromNpc(npcUniqueId, tab, slot, quantity))
            : (ItemMovementType.ShopToInventory, ClientPackets.BuyToInventory(npcUniqueId, tab, slot, quantity));
        return await ShopMoveAsync(packet, type, "buy", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sells one transport slot to the open NPC shop and waits for the answer.</summary>
    public async Task<ItemMoveResult> SellFromTransportAsync(uint npcUniqueId, byte slot, ushort quantity,
        CancellationToken cancellationToken = default)
    {
        if (Transport is not { } transport)
            return new(false, "No transport is summoned.");
        return await ShopMoveAsync(ClientPackets.SellToNpc(npcUniqueId, transport.UniqueId, slot, quantity),
            ItemMovementType.TransportToShop, "sell", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sells an inventory slot to the open NPC shop and waits for the answer.</summary>
    public async Task<ItemMoveResult> SellFromInventoryAsync(uint npcUniqueId, byte slot, ushort quantity,
        CancellationToken cancellationToken = default) =>
        await ShopMoveAsync(ClientPackets.SellFromInventory(npcUniqueId, slot, quantity), ItemMovementType.InventoryToShop, "sell",
            cancellationToken).ConfigureAwait(false);

    /// <summary>Drops an inventory item on the ground and waits for the answer.</summary>
    public async Task<ItemMoveResult> DropItemAsync(byte slot, CancellationToken cancellationToken = default) =>
        await ShopMoveAsync(ClientPackets.DropItem(slot), ItemMovementType.InventoryToGround, "drop", cancellationToken).ConfigureAwait(false);

    /// <summary>Puts an inventory item into a storage slot (the Storage-keeper open) and waits for the answer.</summary>
    public async Task<ItemMoveResult> StoreItemAsync(uint npcUniqueId, byte inventorySlot, byte storageSlot,
        CancellationToken cancellationToken = default) =>
        await ShopMoveAsync(ClientPackets.StoreItem(npcUniqueId, inventorySlot, storageSlot), ItemMovementType.InventoryToStorage, "store",
            cancellationToken).ConfigureAwait(false);

    /// <summary>Puts gold into the storage (or takes it out) at an open Storage-keeper and waits for the answer.</summary>
    public async Task<ItemMoveResult> StorageGoldAsync(uint npcUniqueId, bool deposit, ulong amount, CancellationToken cancellationToken = default) =>
        await ShopMoveAsync(ClientPackets.StorageGold(npcUniqueId, deposit, amount),
            deposit ? ItemMovementType.InventoryGoldToStorage : ItemMovementType.StorageGoldToInventory, deposit ? "store gold" : "take gold",
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens the storage at a Storage-keeper as the game does: selects it, asks for the storage's contents (every time:
    /// the server opens the storage with them) and waits for them, then talks with the storage option and waits for
    /// the server to accept it. Null when it opened, else why not.
    /// </summary>
    public async Task<string?> OpenStorageAsync(uint npcUniqueId, CancellationToken cancellationToken = default)
    {
        SelectEntity(npcUniqueId);
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        int loads = World.StorageLoads;
        Send(ClientPackets.StorageDataRequest(npcUniqueId));
        if (!await WaitUntilAsync(() => World.StorageLoads != loads, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
            return "the storage's contents did not come";
        int talks = World.LastTalk.Count;
        TalkToNpc(npcUniqueId, StorageTalk);
        if (!await WaitUntilAsync(() => World.LastTalk.Count != talks, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
            return "the Storage-keeper did not answer";
        if (!World.LastTalk.Ok || World.LastTalk.Option != StorageTalk)
            return "the Storage-keeper refused to open the storage";
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>The Storage-keeper's talk option for the storage.</summary>
    public const byte StorageTalk = 3;

    private async Task<ItemMoveResult> ShopMoveAsync(Packet packet, ItemMovementType type, string what, CancellationToken cancellationToken)
    {
        await _moveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (result, sent) = await SendMovePacketAsync(packet, type, ShopTimeout, cancellationToken).ConfigureAwait(false);
            if (result is null)
                return new(false, sent is null ? "The bot is not connected." : $"The server did not answer the {what}.");
            if (result.Success)
                return new(true, what switch { "buy" => "Bought.", "sell" => "Sold.", "drop" => "Dropped.", "take gold" => "Taken.", _ => "Stored." });
            Logger.LogWarning("Shop {What} refused: sent 0x7034 [{Sent}], error 0x{Error:X4}", what, sent, result.ErrorCode);
            return new(false, ItemErrors.Format(what, result.ErrorCode), result.ErrorCode);
        }
        finally
        {
            _moveGate.Release();
        }
    }

    /// <summary>
    /// Gets on a mount: the one already summoned, else summons the scroll in <paramref name="scrollSlot"/> (-1: none)
    /// and gets on it. Nothing to do when already riding.
    /// </summary>
    public async Task<ItemMoveResult> RideMountAsync(int scrollSlot, CancellationToken cancellationToken = default)
    {
        if (World.Character is not { } character || !IsInGame)
            return new(false, "The character is not in game.");
        if (character.IsRiding)
            return new(true, "Already riding.");
        if (SummonedMount() is { } pet)
        {
            if (!MountPet(pet.UniqueId))
                return new(false, "The bot is not connected.");
            return await WaitUntilAsync(() => character.IsRiding, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false)
                ? new(true, $"Riding {pet.Name}.")
                : new(false, $"The server did not confirm getting on {pet.Name}.");
        }
        if (scrollSlot < Character.FirstInventorySlot || character.Inventory[scrollSlot] is not { } scroll || !IsMountScroll(scroll))
            return new(false, "No mount scroll to summon.");
        return await RideAsync(character, scroll, (byte)scrollSlot, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets off the mount (nothing to do when not riding).</summary>
    public async Task<ItemMoveResult> DismountAsync(CancellationToken cancellationToken = default)
    {
        if (World.Character is not { } character)
            return new(false, "The character is not in game.");
        if (!character.IsRiding)
            return new(true, "Not riding.");
        if (!DismountPet(character.RidingUniqueId))
            return new(false, "The bot is not connected.");
        return await WaitUntilAsync(() => !character.IsRiding, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false)
            ? new(true, "Got off the mount.")
            : new(false, "The server did not confirm getting off.");
    }

    /// <summary>Wears the job suit in an inventory slot (nothing to do when a job suit is already worn).</summary>
    public async Task<ItemMoveResult> WearJobSuitAsync(int slot, CancellationToken cancellationToken = default)
    {
        if (World.Character is not { } character)
            return new(false, "The character is not in game.");
        if (character.Inventory[8] is Equipable { IsJob: true } worn)
            return new(true, $"Wearing {worn.Name}.");
        if (character.Inventory[slot] is not Equipable { IsJob: true })
            return new(false, "No job suit in that slot.");
        return await UseItemAsync((byte)slot, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
