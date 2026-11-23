using Microsoft.Extensions.Logging;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;

namespace NexBot.Sessions;

/// <summary>High-level game actions (the legacy <c>PacketBuilder</c> methods that took a client).</summary>
public sealed partial class SroClient
{
    #region Movement

    public bool MoveTo(Position position) => Send(ClientPackets.MoveTo(position));

    public bool MoveTo(ushort region, int x, int y, int z) => Send(ClientPackets.MoveTo(region, x, y, z));

    public bool MovePetTo(uint petUniqueId, Position position) => Send(ClientPackets.MoveTo(position, petUniqueId));

    #endregion

    #region Items

    /// <summary>
    /// Finds the first inventory item (from slot 13) with the given type IDs, optionally filtered by server name.
    /// </summary>
    public bool TryFindInventoryItem(byte typeId2, byte typeId3, byte typeId4, out byte slot, string serverNameContains = "")
    {
        slot = 0;
        var inventory = World.Character?.Inventory;
        if (inventory is null)
            return false;
        int index = inventory.FindIndex(
            i => i is not null && i.IsType(typeId2, typeId3, typeId4) &&
                 i.ServerName.Contains(serverNameContains, StringComparison.Ordinal),
            Character.FirstInventorySlot);
        if (index < 0)
            return false;
        slot = (byte)index;
        return true;
    }

    /// <summary>Uses an item on the character, or on a target such as a pet.</summary>
    public bool UseItem(Item item, byte slot, uint targetUniqueId = 0) =>
        Send(ClientPackets.UseItem(item, slot, targetUniqueId));

    /// <summary>
    /// Uses the item in an inventory slot if it is a known usable type (potion, pill, scroll ...).
    /// </summary>
    public bool UseItem(byte slot)
    {
        var inventory = World.Character?.Inventory;
        if (inventory is null || slot < Character.FirstInventorySlot || inventory[slot] is not { } item)
            return false;

        return IsUsable(item) && Send(ClientPackets.UseItem(slot, UsageFor(item)));
    }

    /// <summary>Item types that can be used straight from the inventory (potions, pills, scrolls, summons ...).</summary>
    public static bool IsUsable(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.TypeId2 switch
        {
            2 => true, // summon scrolls
            3 => item.TypeId3 switch
            {
                1 => item.TypeId4 is 1 or 2 or 3,                          // HP / vigor / MP potions
                2 => item.TypeId4 is 1 or 6,                               // universal / purification pills
                3 => item.TypeId4 is 1 or 2 or 6 or 7 or 9 or 10 or 11 or 12, // scrolls, vehicles, fortress items
                13 or 15 => true,                                          // buff / monster scrolls
                _ => false,
            },
            _ => false,
        };
    }

    public bool MoveItem(ItemMovementType type, byte fromSlot, byte toSlot, ushort quantity = 0) =>
        Send(ClientPackets.MoveItem(type, fromSlot, toSlot, quantity));

    private readonly SemaphoreSlim _moveGate = new(1, 1);

    private static bool IsAvatarItem(Item item) =>
        item is Equipable e && (e.IsAvatar || e.EquipableType == EquipableType.DevilSpirit);

    /// <summary>
    /// Moves an inventory item to another inventory slot (swap, merge or, with <paramref name="quantity"/>, split a
    /// stack; slots 0-12 equip) and waits for the server's answer. Moves run one at a time.
    /// </summary>
    public Task<ItemMoveResult> MoveInventoryItemAsync(byte from, byte to, ushort? quantity = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        MoveItemAsync(false, from, false, to, quantity, timeout, cancellationToken);

    /// <summary>
    /// Moves an item between the inventory and the avatar inventory (<paramref name="fromAvatar"/> /
    /// <paramref name="toAvatar"/>; avatar slots 0 hat, 1 dress, 2 accessory, 3 flag, 4 devil spirit), or inside the
    /// inventory, and waits for the server's answer.
    /// </summary>
    public async Task<ItemMoveResult> MoveItemAsync(bool fromAvatar, byte from, bool toAvatar, byte to, ushort? quantity = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsInGame)
            return new(false, "The character is not in game.");
        if (World.Character is not { } character)
            return new(false, "The character is not loaded yet.");
        if (fromAvatar && toAvatar)
            return new(false, "Avatar items can only be moved to and from the inventory.");
        var source = fromAvatar ? character.AvatarInventory : character.Inventory;
        var target = toAvatar ? character.AvatarInventory : character.Inventory;
        if (!fromAvatar && !toAvatar && from == to)
            return new(false, "Drop the item on a different slot.");
        if (from >= source.Capacity || to >= target.Capacity)
            return new(false, "That slot does not exist.");
        if (source[from] is not { } item)
            return new(false, "There is no item in that slot.");
        if (toAvatar && !IsAvatarItem(item))
            return new(false, "Only avatar items go in the avatar slots.");
        if (fromAvatar && target[to] is { } swapped && !IsAvatarItem(swapped))
            return new(false, "Drop the avatar on an empty slot or on another avatar item.");

        var type = (fromAvatar, toAvatar) switch
        {
            (true, false) => ItemMovementType.AvatarToInventory,
            (false, true) => ItemMovementType.InventoryToAvatar,
            _ => ItemMovementType.InventoryToInventory,
        };
        bool stackable = item.QuantityMax > 1 && type == ItemMovementType.InventoryToInventory;
        ushort amount = type == ItemMovementType.InventoryToInventory ? _nonStackMoveQuantity : (ushort)0;
        if (stackable)
        {
            amount = quantity ?? item.Quantity;
            if (amount == 0 || amount > item.Quantity)
                return new(false, $"Quantity must be between 1 and {item.Quantity}.");
            if (amount < item.Quantity && target[to] is { } occupant && occupant.Id != item.Id)
                return new(false, "Put a split stack on an empty slot or on the same item.");
        }

        await _moveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (result, sent) = await SendMoveAsync(type, from, to, amount, timeout ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

            // Bots send non-stackable inventory moves with quantity 0 (as for equipping); if the server refuses that,
            // try once with 1 and keep using what works.
            if (result is { Success: false } && !stackable && type == ItemMovementType.InventoryToInventory && amount == 0)
            {
                Logger.LogWarning("Move {Sent} refused with 0x{Error:X4}; retrying with quantity 1", sent, result.ErrorCode);
                (result, sent) = await SendMoveAsync(type, from, to, 1, timeout ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (result is { Success: true })
                {
                    _nonStackMoveQuantity = 1;
                    Logger.LogInformation("Non-stackable inventory moves are sent with quantity 1 from now on");
                }
            }

            if (result is null)
                return new(false, sent is null ? "The bot is not connected." : "The server did not answer the move.");
            if (result.Success)
                return new(true, "Moved.");
            Logger.LogWarning("Item move refused: sent 0x7034 [{Sent}], error 0x{Error:X4}", sent, result.ErrorCode);
            return new(false, ItemErrors.Format("move the item", result.ErrorCode), result.ErrorCode);
        }
        finally
        {
            _moveGate.Release();
        }
    }

    /// <summary>Quantity sent for non-stackable inventory moves (0 like the legacy bots; 1 if the server wants it).</summary>
    private ushort _nonStackMoveQuantity;

    /// <summary>Sends one 0x7034 and waits for its 0xB034. Result null: not sent (Sent null) or no answer.</summary>
    private Task<(ItemMovedEventArgs? Result, string? Sent)> SendMoveAsync(ItemMovementType type, byte from, byte to,
        ushort amount, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendMovePacketAsync(ClientPackets.MoveItem(type, from, to, amount), type, timeout, cancellationToken);

    /// <summary>Sends a 0x7034 of movement <paramref name="type"/> and waits for its 0xB034.</summary>
    internal async Task<(ItemMovedEventArgs? Result, string? Sent)> SendMovePacketAsync(Packet packet, ItemMovementType type,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        string sent = Convert.ToHexString(packet.Payload);
        var answer = new TaskCompletionSource<ItemMovedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMoved(object? sender, ItemMovedEventArgs e)
        {
            if (!e.Success || e.Type == type)
                answer.TrySetResult(e);
        }
        ItemMoved += OnMoved;
        try
        {
            if (!Send(packet))
                return (null, null);
            return (await answer.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false), sent);
        }
        catch (TimeoutException)
        {
            return (null, sent);
        }
        finally
        {
            ItemMoved -= OnMoved;
        }
    }

    /// <summary>
    /// Learns a skill, or raises it one level: <paramref name="skillId"/> is the ID of the level to learn (see
    /// <see cref="Map.SkillWindow"/>). Waits for the server's answer (0xB0A1).
    /// </summary>
    public async Task<SkillResult> LearnSkillAsync(uint skillId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsInGame || World.Character is null)
            return new(false, "The character is not in game.");
        if (Data.GetSkill(skillId) is not { } info)
            return new(false, $"Skill {skillId} is not in the game database.");

        var answer = new TaskCompletionSource<SkillLearnedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAnswer(object? sender, SkillLearnedEventArgs e) => answer.TrySetResult(e);
        SkillLearned += OnAnswer;
        try
        {
            if (!Send(ClientPackets.LearnSkill(skillId)))
                return new(false, "The bot is not connected.");
            var e = await answer.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (e.Success)
                return new(true, $"Learned {(info.Name.Length > 0 ? info.Name : info.ServerName)}.");
            Logger.LogWarning("Skill {Skill} refused with error 0x{Error:X4}", info.ServerName, e.ErrorCode);
            return new(false, $"The server refused the skill (error 0x{e.ErrorCode:X4}).", e.ErrorCode);
        }
        catch (TimeoutException)
        {
            return new(false, "The server did not answer.");
        }
        finally
        {
            SkillLearned -= OnAnswer;
        }
    }

    /// <summary>Raises a mastery one level and waits for the server's answer (0xB0A2).</summary>
    public async Task<SkillResult> RaiseMasteryAsync(uint masteryId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsInGame || World.Character is null)
            return new(false, "The character is not in game.");

        var answer = new TaskCompletionSource<MasteryRaisedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAnswer(object? sender, MasteryRaisedEventArgs e) => answer.TrySetResult(e);
        MasteryRaised += OnAnswer;
        try
        {
            if (!Send(ClientPackets.RaiseMastery(masteryId)))
                return new(false, "The bot is not connected.");
            var e = await answer.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (e.Success)
                return new(true, $"{Data.GetMastery(masteryId)?.Name ?? "Mastery"} is now level {e.Level}.");
            Logger.LogWarning("Mastery {Mastery} raise refused with error 0x{Error:X4}", masteryId, e.ErrorCode);
            return new(false, $"The server refused the mastery raise (error 0x{e.ErrorCode:X4}).", e.ErrorCode);
        }
        catch (TimeoutException)
        {
            return new(false, "The server did not answer.");
        }
        finally
        {
            MasteryRaised -= OnAnswer;
        }
    }

    /// <summary>Equips the item in <paramref name="slot"/>, or unequips it if it is already worn (slot &lt; 13).</summary>
    public bool EquipItem(byte slot, bool useAvatarInventory = false)
    {
        var inventory = World.Character?.Inventory;
        if (inventory?[slot] is not Equipable item)
            return false;

        if (slot < Character.FirstInventorySlot)
        {
            int free = inventory.FindEmptySlot(Character.FirstInventorySlot);
            return free != -1 && MoveItem(
                useAvatarInventory ? ItemMovementType.AvatarToInventory : ItemMovementType.InventoryToInventory,
                slot, (byte)free);
        }

        return EquipTarget(item, inventory) is { } t && MoveItem(t.Type, slot, t.Slot);
    }

    /// <summary>Where an item is worn: equipment slot (0-12) or avatar slot (0-4); null when it can't be worn.</summary>
    private static (ItemMovementType Type, byte Slot)? EquipTarget(Equipable item, SlotList<Item> inventory)
    {
        const ItemMovementType toInventory = ItemMovementType.InventoryToInventory;
        const ItemMovementType toAvatar = ItemMovementType.InventoryToAvatar;
        return item.EquipableType switch
        {
            EquipableType.Garment or EquipableType.Protector or EquipableType.Armor or
            EquipableType.Robe or EquipableType.LightArmor or EquipableType.HeavyArmor => (ArmorPart)item.TypeId4 switch
            {
                ArmorPart.Head => (toInventory, 0),
                ArmorPart.Chest => (toInventory, 1),
                ArmorPart.Shoulders => (toInventory, 2),
                ArmorPart.Gloves => (toInventory, 3),
                ArmorPart.Pants => (toInventory, 4),
                ArmorPart.Boots => (toInventory, 5),
                _ => null,
            },
            EquipableType.Weapon => (toInventory, 6),
            EquipableType.Shield => (toInventory, 7),
            EquipableType.Job => (toInventory, 8),
            EquipableType.AccessoryChinese or EquipableType.AccessoryEuropean => (AccessoryPart)item.TypeId4 switch
            {
                AccessoryPart.Earring => (toInventory, 9),
                AccessoryPart.Necklace => (toInventory, 10),
                AccessoryPart.Ring => (toInventory, (byte)(inventory[12] is null ? 12 : 11)),
                _ => null,
            },
            EquipableType.Avatar => (AvatarPart)item.TypeId4 switch
            {
                AvatarPart.Hat => (toAvatar, 0),
                AvatarPart.Dress => (toAvatar, 1),
                AvatarPart.Accessory => (toAvatar, 2),
                AvatarPart.Flag => (toAvatar, 3),
                _ => null,
            },
            EquipableType.DevilSpirit => (toAvatar, 4),
            _ => null,
        };
    }

    /// <summary>
    /// Right-click in the game: uses a consumable (potion, scroll ...), wears equipment / avatars from the inventory,
    /// or takes off a worn item (<paramref name="avatar"/> for the avatar slots). Waits for the server's answer.
    /// </summary>
    /// <param name="message">Text for a global chatting item (required for it, ignored otherwise).</param>
    public async Task<ItemMoveResult> UseItemAsync(byte slot, bool avatar = false, string? message = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsInGame)
            return new(false, "The character is not in game.");
        if (World.Character is not { } character)
            return new(false, "The character is not loaded yet.");
        var inventory = character.Inventory;
        var item = avatar ? character.AvatarInventory[slot] : inventory[slot];
        if (item is null)
            return new(false, "There is no item in that slot.");

        // Take off: worn equipment or avatar -> first free inventory slot
        if (avatar || slot < Character.FirstInventorySlot)
        {
            int free = inventory.FindEmptySlot(Character.FirstInventorySlot);
            return free == -1
                ? new(false, "The inventory is full.")
                : await MoveItemAsync(avatar, slot, false, (byte)free, null, timeout, cancellationToken).ConfigureAwait(false);
        }

        // Wear
        if (item is Equipable equipable)
        {
            if (EquipTarget(equipable, inventory) is not { } target)
                return new(false, $"{item.Name} can't be worn.");
            return await MoveItemAsync(false, slot, target.Type == ItemMovementType.InventoryToAvatar, target.Slot, null, timeout,
                cancellationToken).ConfigureAwait(false);
        }

        if (IsMountScroll(item))
            return await RideAsync(character, item, slot, timeout ?? TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        bool global = IsGlobalChatItem(item);
        if (global)
        {
            message = message?.Trim() ?? "";
            if (message.Length == 0)
                return new(false, "Write the message to send to all players.");
            if (message.Length > MaxGlobalMessageLength)
                return new(false, $"The message is too long (max {MaxGlobalMessageLength} characters).");
        }
        else if (!IsUsable(item))
            return new(false, $"{item.Name} can't be used from the dashboard.");

        await _moveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (e, sent) = await UseAndWaitAsync(item, slot,
                usage => global ? ClientPackets.GlobalChat(slot, usage, message!) : ClientPackets.UseItem(slot, usage),
                timeout ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!sent)
                return new(false, "The bot is not connected.");
            if (e is null)
                return new(false, "The server did not answer.");
            if (e.Success)
                return new(true, global ? "Message sent to all players." : $"Used {item.Name}.");
            Logger.Log(e.ErrorCode == ItemErrors.CooldownError ? LogLevel.Debug : LogLevel.Warning,
                "Item use refused: slot {Slot} ({Item}), error 0x{Error:X4}", slot, item.ServerName, e.ErrorCode);
            return new(false, ItemErrors.Format($"use {item.Name}", e.ErrorCode), e.ErrorCode);
        }
        finally
        {
            _moveGate.Release();
        }
    }

    /// <summary>
    /// The low bits of the usage type sent with 0x704C differ between bots: the cash flag (legacy SRClientless, the
    /// default) or the rental type (xBot). They only differ for cash items; the first refusal tries the other one and
    /// the one that works is kept.
    /// </summary>
    private bool _usageFromRentable;

    private ushort UsageFor(Item item) => _usageFromRentable ? item.UsageTypeFromRentable : item.UsageType;

    /// <summary>Items refused with both usage types: the server refuses them for another reason, so no second try.</summary>
    private readonly HashSet<string> _bothUsagesRefused = new(StringComparer.Ordinal);

    /// <summary>Sends an item use (built for a usage type) and waits for 0xB04C; retries once with the other usage type.</summary>
    private async Task<(ItemUsedEventArgs? Result, bool Sent)> UseAndWaitAsync(Item item, byte slot, Func<ushort, Packet> build,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ushort usage = UsageFor(item);
        var (result, sent) = await SendUseAsync(build(usage), slot, timeout, cancellationToken).ConfigureAwait(false);
        ushort alternate = _usageFromRentable ? item.UsageType : item.UsageTypeFromRentable;
        bool tryAlternate;
        lock (_bothUsagesRefused)
            tryAlternate = !_bothUsagesRefused.Contains(item.ServerName);
        if (result is { Success: false } && result.ErrorCode != ItemErrors.CooldownError && alternate != usage && tryAlternate)
        {
            Logger.LogDebug("Using {Item} with usage 0x{Usage:X4} refused (0x{Error:X4}); retrying with 0x{Alternate:X4}",
                item.ServerName, usage, result.ErrorCode, alternate);
            var (retry, _) = await SendUseAsync(build(alternate), slot, timeout, cancellationToken).ConfigureAwait(false);
            if (retry is { Success: true })
            {
                _usageFromRentable = !_usageFromRentable;
                Logger.LogInformation("Item usage types are sent with the {Kind} bits from now on", _usageFromRentable ? "rental" : "cash");
            }
            // Both refused: keep the first answer (its error is the server's real reason; the retry's is the usage).
            if (retry is { Success: true })
                result = retry;
            else if (retry is { Success: false })
                lock (_bothUsagesRefused)
                    _bothUsagesRefused.Add(item.ServerName);
        }
        return (result, sent);
    }

    private async Task<(ItemUsedEventArgs? Result, bool Sent)> SendUseAsync(Packet packet, byte slot, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<ItemUsedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUsed(object? sender, ItemUsedEventArgs e)
        {
            if (!e.Success || e.Slot == slot)
                answer.TrySetResult(e);
        }
        ItemUsed += OnUsed;
        try
        {
            if (!Send(packet))
                return (null, false);
            return (await answer.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false), true);
        }
        catch (TimeoutException)
        {
            return (null, true);
        }
        finally
        {
            ItemUsed -= OnUsed;
        }
    }

    /// <summary>Summons the first transport whose name contains <paramref name="namePart"/>.</summary>
    public bool SummonTransport(string namePart)
    {
        var inventory = World.Character?.Inventory;
        if (inventory is null)
            return false;
        int slot = inventory.FindIndex(i => i is not null && i.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase));
        return slot != -1 && SummonMount((byte)slot);
    }

    /// <summary>Inventory slot (from 13) of the first horse / camel / transport summon scroll, or -1.</summary>
    /// <param name="preferRide">Prefer riding animals (ITEM_COS_C_*) over transports (ITEM_COS_T_*).</param>
    public int FindMountItemSlot(bool preferRide = true)
    {
        var inventory = World.Character?.Inventory;
        if (inventory is null)
            return -1;
        int ride = inventory.FindIndex(i => IsMountScroll(i) && i!.ServerName.StartsWith("ITEM_COS_C_", StringComparison.Ordinal), Character.FirstInventorySlot);
        int transport = inventory.FindIndex(i => IsMountScroll(i) && i!.ServerName.StartsWith("ITEM_COS_T_", StringComparison.Ordinal), Character.FirstInventorySlot);
        return preferRide ? (ride >= 0 ? ride : transport) : (transport >= 0 ? transport : ride);
    }

    private static bool IsMountScroll(Item? item) =>
        item is not null && item.IsType(3, 3, 2) && item.ServerName.StartsWith("ITEM_COS_", StringComparison.Ordinal);

    /// <summary>
    /// Right-click on a horse / camel / transport scroll (Beetle ...): get off when riding, get on a mount that is
    /// already summoned, otherwise summon it and get on it once it appears.
    /// </summary>
    private async Task<ItemMoveResult> RideAsync(Character character, Item item, byte slot, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (character.IsRiding)
        {
            uint riding = character.RidingUniqueId;
            if (!DismountPet(riding))
                return new(false, "The bot is not connected.");
            return await WaitUntilAsync(() => !character.IsRiding, timeout, cancellationToken).ConfigureAwait(false)
                ? new(true, "Got off the mount.")
                : new(false, "The server did not confirm getting off.");
        }

        var pet = SummonedMount();
        if (pet is null)
        {
            await _moveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (used, sent) = await UseAndWaitAsync(item, slot, usage => ClientPackets.UseItem(slot, usage), timeout, cancellationToken)
                    .ConfigureAwait(false);
                if (!sent)
                    return new(false, "The bot is not connected.");
                // used == null: no answer; some servers answer a summon only with the pet spawn, so keep waiting below.
                if (used is { Success: false })
                {
                    Logger.LogWarning("Mount summon refused: slot {Slot} ({Item}), error 0x{Error:X4}", slot, item.ServerName, used.ErrorCode);
                    return new(false, ItemErrors.Format($"summon {item.Name}", used.ErrorCode), used.ErrorCode);
                }
            }
            finally
            {
                _moveGate.Release();
            }

            // A horse is often mounted right away; otherwise wait for the pet to appear.
            await WaitUntilAsync(() => character.IsRiding || SummonedMount() is not null, timeout, cancellationToken).ConfigureAwait(false);
            if (character.IsRiding)
                return new(true, $"Riding {item.Name}.");
            pet = SummonedMount();
            if (pet is null)
                return new(false, $"{item.Name} did not appear.");
        }

        if (!MountPet(pet.UniqueId))
            return new(false, "The bot is not connected.");
        return await WaitUntilAsync(() => character.IsRiding, timeout, cancellationToken).ConfigureAwait(false)
            ? new(true, $"Riding {pet.Name}.")
            : new(false, $"The server did not confirm getting on {pet.Name}.");
    }

    /// <summary>Our summoned horse / transport, if any.</summary>
    private Cos? SummonedMount() => World.MyPets.Values.FirstOrDefault(p => p.IsHorse || p.IsTransport);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Summons the mount in an inventory slot (horse, camel, transport).</summary>
    public bool SummonMount(byte slot) =>
        World.Character?.Inventory[slot] is { } item
            ? Send(ClientPackets.UseItem(slot, UsageFor(item)))
            : Send(ClientPackets.SummonTransport(slot));

    /// <summary>Gets on a summoned horse / transport.</summary>
    public bool MountPet(uint petUniqueId) => Send(ClientPackets.MountPet(petUniqueId, true));

    /// <summary>Gets off a horse / transport.</summary>
    public bool DismountPet(uint petUniqueId) => Send(ClientPackets.MountPet(petUniqueId, false));

    #endregion

    #region Entities / NPC

    public bool SelectEntity(uint uniqueId)
    {
        World.SelectedEntityId = uniqueId;
        return Send(ClientPackets.SelectEntity(uniqueId));
    }

    public bool TalkToNpc(uint uniqueId, byte talkOption) => Send(ClientPackets.TalkToNpc(uniqueId, talkOption));
    public bool CloseNpc(uint uniqueId) => Send(ClientPackets.CloseNpc(uniqueId));
    public bool BuyFromNpc(uint npcUniqueId, byte tab, byte slot, ushort quantity) => Send(ClientPackets.BuyFromNpc(npcUniqueId, tab, slot, quantity));
    public bool SellToNpc(uint npcUniqueId, uint petUniqueId, byte slot, ushort quantity) => Send(ClientPackets.SellToNpc(npcUniqueId, petUniqueId, slot, quantity));
    public bool UseTeleport(uint teleportUniqueId, uint destinationId) => Send(ClientPackets.UseTeleport(teleportUniqueId, destinationId));
    public bool Attack(uint targetUniqueId, uint skillId = 1) => Send(ClientPackets.Attack(targetUniqueId, skillId));

    /// <summary>Puts on a PvP cape (<see cref="PvpCape.None"/> removes it). The result arrives in <see cref="PvpCapeChanged"/>.</summary>
    public bool WearPvpCape(PvpCape cape)
    {
        RequestedCape = cape;
        return Send(ClientPackets.WearPvpCape(cape));
    }

    #endregion

    #region Chat / game master

    /// <summary>Sends a chat message (All, Party, Guild, GameMaster, Notice ...). Use <see cref="SendPrivateMessage"/> for whispers.</summary>
    public bool SendChat(ChatType type, string message) => Send(ClientPackets.Chat(type, message));

    /// <summary>Whispers a character.</summary>
    public bool SendPrivateMessage(string receiver, string message) => Send(ClientPackets.Chat(ChatType.Private, message, receiver));

    /// <summary>Sends a notice to the whole server (game master accounts only).</summary>
    public bool SendNotice(string message) => Send(ClientPackets.Chat(ChatType.Notice, message));

    /// <summary>Global chat item type IDs (ITEM_MALL_GLOBAL_CHATTING ...).</summary>
    private const byte GlobalTid2 = 3, GlobalTid3 = 3, GlobalTid4 = 5;

    /// <summary>Longest global chat message accepted from the dashboard.</summary>
    public const int MaxGlobalMessageLength = 100;

    /// <summary>Global chatting item ("Chat to all"); used with a message, see <see cref="UseItemAsync"/>.</summary>
    public static bool IsGlobalChatItem(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.IsType(GlobalTid2, GlobalTid3, GlobalTid4);
    }

    /// <summary>Inventory slot of a global chatting item, or -1.</summary>
    public int FindGlobalChatItemSlot()
    {
        var inventory = World.Character?.Inventory;
        if (inventory is null)
            return -1;
        return inventory.FindIndex(i => i is not null && i.IsType(GlobalTid2, GlobalTid3, GlobalTid4), Character.FirstInventorySlot);
    }

    /// <summary>
    /// Sends a global message with the first global chatting item in the inventory. Returns false when the
    /// character has none (or is not connected).
    /// </summary>
    public bool SendGlobal(string message)
    {
        int slot = FindGlobalChatItemSlot();
        if (slot < 0 || World.Character?.Inventory[slot] is not { } item)
            return false;
        return Send(ClientPackets.GlobalChat((byte)slot, item.UsageType, message));
    }

    /// <summary>Sends a game master command (GM accounts only), e.g. <see cref="GameMasterCommand.GoTown"/>.</summary>
    public bool SendGameMasterCommand(GameMasterCommand command) => Send(ClientPackets.GameMaster(command));

    /// <summary>Sends a game master command that targets a character (GM accounts only).</summary>
    public bool SendGameMasterCommand(GameMasterCommand command, string characterName) =>
        Send(ClientPackets.GameMaster(command, characterName));

    #endregion

    #region Party

    public bool RespondToPetition(bool accept, PlayerPetition type) => Send(ClientPackets.PetitionResponse(accept, type));
    public bool InviteToParty(uint targetUniqueId, PartySetup setup = PartySetup.All) => Send(ClientPackets.InviteToParty(targetUniqueId, setup));

    public bool CreatePartyMatch(string title, PartySetup setup = PartySetup.All, PartyPurpose purpose = PartyPurpose.Hunting,
        byte levelMin = 1, byte levelMax = 110) =>
        Send(ClientPackets.CreatePartyMatch(title, setup, purpose, levelMin, levelMax));

    public bool DeletePartyMatch(uint number) => Send(ClientPackets.DeletePartyMatch(number));
    public bool InviteToCurrentParty(uint targetUniqueId) => Send(ClientPackets.InviteToCurrentParty(targetUniqueId));
    public bool LeaveParty() => Send(ClientPackets.LeaveParty());
    public bool KickFromParty(uint memberId) => Send(ClientPackets.KickFromParty(memberId));
    public bool RequestPartyMatchList(byte page = 0) => Send(ClientPackets.RequestPartyMatchList(page));
    public bool InviteToGuild(uint targetUniqueId) => Send(ClientPackets.InviteToGuild(targetUniqueId));
    public bool InviteToAcademy(uint targetUniqueId) => Send(ClientPackets.InviteToAcademy(targetUniqueId));
    public bool InviteToUnion(uint targetUniqueId) => Send(ClientPackets.InviteToUnion(targetUniqueId));
    public bool Trace(uint targetUniqueId) => Send(ClientPackets.Trace(targetUniqueId));
    public bool DesignateRecall(uint npcUniqueId) => Send(ClientPackets.DesignateRecall(npcUniqueId));

    /// <summary>
    /// Invites a nearby player to a party: creates one with <paramref name="setup"/> when we have none, otherwise
    /// adds them to ours (the master, or anyone when the party allows it).
    /// </summary>
    public bool InvitePlayerToParty(uint targetUniqueId, PartySetup setup = PartySetup.All) =>
        World.Party is null ? InviteToParty(targetUniqueId, setup) : InviteToCurrentParty(targetUniqueId);
    public bool JoinPartyMatch(uint number) => Send(ClientPackets.JoinPartyMatch(number));
    public bool RespondToPartyMatchJoin(uint requestId, uint joinId, bool accept) => Send(ClientPackets.PartyMatchJoinResponse(requestId, joinId, accept));

    #endregion

    #region Character creation

    /// <summary>Creates a character with the default beginner equipment for the race and gender.</summary>
    public bool CreateCharacter(string name, bool male, CharacterRace race)
    {
        string g = male ? "M" : "W";
        var (modelName, prefix) = race == CharacterRace.Chinese
            ? (male ? "CHAR_CH_MAN_ADVENTURER" : "CHAR_CH_WOMAN_ADVENTURER", "ITEM_CH")
            : (male ? "CHAR_EU_MAN_NOBLE" : "CHAR_EU_WOMAN_NOBLE", "ITEM_EU");

        var model = Data.GetModel(modelName);
        var chest = Data.GetItem($"{prefix}_{g}_LIGHT_01_BA_A_DEF");
        var legs = Data.GetItem($"{prefix}_{g}_LIGHT_01_LA_A_DEF");
        var shoes = Data.GetItem($"{prefix}_{g}_LIGHT_01_FA_A_DEF");
        var weapon = Data.GetItem($"{prefix}_SWORD_01_A_DEF");

        if (model is null || chest is null || legs is null || shoes is null || weapon is null)
        {
            Logger.LogError("Cannot create character: starter model/items are missing from the game database");
            return false;
        }

        return Send(ClientPackets.CreateCharacter(name, model.Id, chest.Id, legs.Id, shoes.Id, weapon.Id));
    }

    #endregion
}
