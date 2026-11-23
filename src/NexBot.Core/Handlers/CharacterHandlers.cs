using Microsoft.Extensions.Logging;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;

namespace NexBot.Handlers;

/// <summary>Character data (0x34A5/0x3013/0x34A6), experience and inventory.</summary>
internal static class CharacterHandlers
{
    /// <summary>0x34A5: character data follows. Also sent after each teleport.</summary>
    public static void DataBegin(SroClient client, Packet p)
    {
        client.World.ClearSpawns();
        client.CharacterDataBuffer = new Packet(Opcodes.Server.CharacterData);
    }

    /// <summary>0x3013: a chunk of character data.</summary>
    public static void Data(SroClient client, Packet p) =>
        client.CharacterDataBuffer?.WriteBytes(p.Payload);

    /// <summary>0x34A6: all character data received; parse it.</summary>
    public static void DataEnd(SroClient client, Packet p)
    {
        var buffer = client.CharacterDataBuffer;
        client.CharacterDataBuffer = null;
        if (buffer is null)
            return;

        Character character;
        try
        {
            character = ParseCharacter(client.Data, buffer);
        }
        catch (Exception ex) when (ex is PacketException or Game.Items.UnknownGameObjectException)
        {
            // Layouts differ slightly between server builds: dump the data so the parser can be adjusted.
            client.Logger.LogWarning(
                "Could not parse character data ({Message}). Failed at offset {Offset} of {Length}. " +
                "The character stays online, but inventory/skills/exp tracking are unavailable. Raw data: {Hex}",
                ex.Message, buffer.Position, buffer.Length, Convert.ToHexString(buffer.Payload));
            return;
        }
        client.World.Character = character;
        client.Logger.LogInformation("Character data loaded: {Name} Lv {Level}, {Gold:N0} gold, riding={Riding}, job mode={Job}",
            character.Name, character.Level, character.Gold, character.IsRiding, character.HasJobMode());
        client.RaiseCharacterLoaded(character);
    }

    internal static Character ParseCharacter(IGameData data, Packet p)
    {
        p.ReadUInt32(); // server time stamp
        var c = EntityFactory.CreateCharacter(data, p.ReadUInt32());
        c.Scale = p.ReadByte();
        c.Level = p.ReadByte();
        c.ExpMax = data.GetExpMax(c.Level);
        c.LevelMax = p.ReadByte();
        c.Exp = p.ReadUInt64();
        c.SpExp = p.ReadUInt32();
        c.Gold = p.ReadUInt64();
        c.Sp = p.ReadUInt32();
        c.StatPoints = p.ReadUInt16();
        c.BerserkPoints = p.ReadByte();
        c.GatheredExpPoint = p.ReadUInt32();
        // Remaining (current) HP/MP. The maximums arrive in 0x303D right after; until then use these.
        c.Hp = c.HpMax = p.ReadUInt32();
        c.Mp = c.MpMax = p.ReadUInt32();
        c.ExpIcon = (ExpIcon)p.ReadByte();
        c.PkDaily = p.ReadByte();
        c.PkTotal = p.ReadUInt16();
        c.PkPenalty = p.ReadUInt32();
        c.BerserkLevel = p.ReadByte();
        c.PvpCape = (PvpCape)p.ReadByte();

        c.Inventory = p.ReadInventory(data);
        c.AvatarInventory = p.ReadInventory(data);

        // Masteries: lists use 1 = "another entry follows", 2 = "end of list" (so ReadBool would be wrong).
        p.ReadByte();
        while (p.ReadByte() == 1)
        {
            uint id = p.ReadUInt32();
            c.Masteries[id] = new Mastery(id, data.GetMastery(id)) { Level = p.ReadByte() };
        }

        // Skills (same 1/2 list terminator)
        p.ReadByte();
        while (p.ReadByte() == 1)
        {
            uint id = p.ReadUInt32();
            bool enabled = p.ReadBool();
            if (data.GetSkill(id) is { } info)
                c.Skills[id] = new Skill(info) { Enabled = enabled };
        }

        // Completed quests
        ushort completed = p.ReadUInt16();
        for (int i = 0; i < completed; i++)
        {
            var quest = new Quest(p.ReadUInt32());
            c.QuestsCompleted[quest.Id] = quest;
        }

        // Active quests
        byte active = p.ReadByte();
        for (int i = 0; i < active; i++)
        {
            var quest = new Quest(p.ReadUInt32())
            {
                Achievements = p.ReadByte(),
                IsAutoShareRequired = p.ReadBool(),
                QuestType = p.ReadByte(),
            };
            if (quest.QuestType == 28)
                quest.TimeRemain = p.ReadUInt32();
            quest.State = p.ReadByte();
            if (quest.QuestType != 8)
            {
                var objectives = new SlotList<QuestObjective>(p.ReadByte());
                for (int k = 0; k < objectives.Capacity; k++)
                {
                    objectives[k] = new QuestObjective(p.ReadByte())
                    {
                        IsEnabled = p.ReadBool(),
                        Name = p.ReadAscii(),
                        TaskIds = p.ReadUInt32Array(p.ReadByte()),
                    };
                }
                quest.Objectives = objectives;
            }
            if (quest.QuestType == 88)
                quest.NpcIds = p.ReadUInt32Array(p.ReadByte());
            c.Quests[quest.Id] = quest;
        }

        // Collection books
        p.ReadByte();
        uint books = p.ReadUInt32();
        for (uint i = 0; i < books; i++)
        {
            var book = new CollectionBook(p.ReadUInt32()) { Started = new SroTimestamp(p.ReadUInt32()), Pages = p.ReadUInt32() };
            c.CollectionBooks[book.Id] = book;
        }

        // Position, movement, state
        p.ReadModelMovementAndState(c, out byte unknown);
        c.UnknownByte4 = unknown;
        c.Buffs = p.ReadBuffs(data, c.UniqueId);

        // Identity
        c.Name = p.ReadAscii();
        c.JobName = p.ReadAscii();
        c.JobType = (JobType)p.ReadByte();
        c.JobLevel = p.ReadByte();
        c.JobExpMax = data.GetJobExpMax(c.JobLevel, c.JobType);
        c.JobExp = p.ReadUInt32();
        c.JobContribution = p.ReadUInt32();
        c.JobReward = p.ReadUInt32();
        c.PvpState = (PvpState)p.ReadByte();
        bool isRiding = p.ReadBool();
        c.InCombat = p.ReadBool();
        if (isRiding)
            c.RidingUniqueId = p.ReadUInt32();
        c.CaptureTheFlag = (CaptureTheFlag)p.ReadByte();
        c.GuideFlag = p.ReadUInt64();
        c.JoinId = p.ReadUInt32();
        c.IsGameMaster = p.ReadBool();
        // Remaining bytes are GUI settings (hotkeys, auto potion, blocked whispers) and vary between servers.
        return c;
    }

    /// <summary>0x3056: experience gained (or lost on death).</summary>
    /// <summary>0x303D: the character's stats (sent after spawning and whenever gear, buffs or stats change).</summary>
    public static void StatsUpdate(SroClient client, Packet p)
    {
        uint phyAtkMin = p.ReadUInt32(), phyAtkMax = p.ReadUInt32();
        uint magAtkMin = p.ReadUInt32(), magAtkMax = p.ReadUInt32();
        ushort phyDef = p.ReadUInt16(), magDef = p.ReadUInt16();
        ushort hitRate = p.ReadUInt16(), parryRate = p.ReadUInt16();
        uint hpMax = p.ReadUInt32(), mpMax = p.ReadUInt32();
        ushort str = p.ReadUInt16(), intellect = p.ReadUInt16();

        var c = client.World.Character;
        if (c is null)
            return;

        c.PhysicalAttackMin = phyAtkMin;
        c.PhysicalAttackMax = phyAtkMax;
        c.MagicalAttackMin = magAtkMin;
        c.MagicalAttackMax = magAtkMax;
        c.PhysicalDefense = phyDef;
        c.MagicalDefense = magDef;
        c.HitRate = hitRate;
        c.ParryRate = parryRate;
        c.Strength = str;
        c.Intellect = intellect;
        c.HpMax = hpMax;
        c.MpMax = mpMax;
        if (c.Hp > hpMax) c.Hp = hpMax;
        if (c.Mp > mpMax) c.Mp = mpMax;
    }

    /// <summary>0xB050 / 0xB051: the answer to +1 STR / +1 INT. [1] = done, otherwise [2][error].</summary>
    public static void StatAdded(SroClient client, Packet p, bool strength)
    {
        var c = client.World.Character;
        if (c is null || p.Remaining < 1)
            return;
        if (p.ReadByte() == 1)
        {
            if (c.StatPoints > 0)
                c.StatPoints--;
            return;
        }
        ushort error = p.ReadErrorCode();
        c.StatPoints = 0; // refused: most likely no free points left
        client.Logger.LogWarning("+1 {Stat} was refused by the server (error 0x{Error:X4})", strength ? "STR" : "INT", error);
    }

    public static void ExperienceUpdate(SroClient client, Packet p)
    {
        p.ReadUInt32(); // source unique ID (for the exp graphics)
        long exp = p.ReadInt64();
        p.ReadInt64(); // SP exp

        var c = client.World.Character;
        if (c is null)
            return;

        if (exp < 0)
        {
            ulong loss = (ulong)(-exp);
            c.Exp = loss > c.Exp ? 0 : c.Exp - loss;
            return;
        }

        c.Exp += (ulong)exp;
        byte level = c.Level;
        ulong max = client.Data.GetExpMax(level);
        while (max > 0 && c.Exp >= max && level < byte.MaxValue)
        {
            c.Exp -= max;
            level++;
            max = client.Data.GetExpMax(level);
        }

        if (level > c.Level)
        {
            c.LevelUpsCounted += level - c.Level;
            SetLevel(client, c, level, max);
        }
    }

    /// <summary>
    /// 0x3054: an entity leveled up (the level-up effect): unique ID. For our character this is the reliable sign: the
    /// level is raised here too when the EXP count missed it (GM level-ups, quest rewards, EXP the bot did not see).
    /// </summary>
    public static void LevelUp(SroClient client, Packet p)
    {
        uint uniqueId = p.ReadUInt32();
        if (client.World.Character is not { } c || uniqueId != c.UniqueId)
            return;
        if (c.LevelUpsCounted > 0)
        {
            c.LevelUpsCounted--; // already counted from the EXP
            return;
        }
        if (c.Level == byte.MaxValue)
            return;
        byte level = (byte)(c.Level + 1);
        c.Exp = 0;
        SetLevel(client, c, level, client.Data.GetExpMax(level));
    }

    private static void SetLevel(SroClient client, Character c, byte level, ulong expMax)
    {
        // Every level gives 3 free stat points; the server does not send the new total here.
        c.StatPoints = (ushort)Math.Min(ushort.MaxValue, c.StatPoints + 3 * Math.Max(0, level - c.Level));
        c.Level = level;
        c.ExpMax = expMax;
        string text = client.Data.FormatUiText("UIIT_MSG_STRGERR_LEVEL", level);
        client.Logger.LogInformation("{Message}", text.Length > 0 ? text : $"Level up! Now level {level}");
        client.RaiseLevelUp(level);
    }

    /// <summary>The slot of a pick-up answer that means gold: [FE][uint amount].</summary>
    private const byte GoldSlot = 0xFE;

    /// <summary>0xB034: result of an item movement.</summary>
    public static void ItemMovement(SroClient client, Packet p)
    {
        if (!p.ReadSuccess())
        {
            client.RaiseItemMoved(false, null, p.ReadErrorCode());
            return;
        }

        var type = (ItemMovementType)p.ReadByte();
        switch (type)
        {
            case ItemMovementType.InventoryToInventory:
                InventoryToInventory(client, p);
                break;
            case ItemMovementType.InventoryToAvatar:
            case ItemMovementType.AvatarToInventory:
                AvatarSwap(client, p, type == ItemMovementType.InventoryToAvatar);
                break;
            case ItemMovementType.GroundToInventory:
            {
                byte slot = p.ReadByte();
                if (slot == GoldSlot)
                {
                    // Gold: [FE][uint amount] (the bag's gold comes with 0x304E).
                    if (p.Remaining >= 4)
                        client.RaiseGoldPicked(p.ReadUInt32(), false);
                    break;
                }
                var item = p.ReadItem(client.Data);
                SetInventorySlot(client, slot, item);
                if (item is not null)
                    client.RaiseItemPicked(item);
                break;
            }
            case ItemMovementType.QuestToInventory:
            {
                byte slot = p.ReadByte();
                p.ReadByte();
                SetInventorySlot(client, slot, p.ReadItem(client.Data));
                break;
            }
            case ItemMovementType.GroundToPetToInventory:
            {
                // The grab pet picked it up into the character's bag: [uint pet][slot][item], gold [uint pet][FE][uint amount].
                p.ReadUInt32(); // pet unique ID
                byte slot = p.ReadByte();
                if (slot == GoldSlot)
                {
                    if (p.Remaining >= 4)
                        client.RaiseGoldPicked(p.ReadUInt32(), true);
                    break;
                }
                var item = p.ReadItem(client.Data);
                SetInventorySlot(client, slot, item);
                if (item is not null)
                    client.RaiseItemPicked(item, byPet: true);
                break;
            }
            case ItemMovementType.GroundToPet:
            {
                // The grab pet picked it up into its own bag: [uint pet][slot][item].
                uint petId = p.ReadUInt32();
                byte slot = p.ReadByte();
                var item = p.ReadItem(client.Data);
                if (client.World.MyPets.TryGetValue(petId, out var pet) && slot < pet.Inventory.Capacity)
                    pet.Inventory[slot] = item;
                if (item is not null)
                    client.RaiseItemPicked(item, byPet: true, intoPet: true);
                break;
            }
            case ItemMovementType.InventoryToStorage:
                InventoryToStorage(client, p);
                break;
            case ItemMovementType.InventoryGoldToStorage:
            case ItemMovementType.StorageGoldToInventory:
                // [ulong amount]; the bag's gold comes with 0x304E.
                if (p.Remaining >= 8)
                {
                    ulong amount = p.ReadUInt64();
                    var w = client.World;
                    w.StorageGold = type == ItemMovementType.InventoryGoldToStorage ? w.StorageGold + amount : w.StorageGold - Math.Min(amount, w.StorageGold);
                }
                break;
            case ItemMovementType.InventoryToGround:
            case ItemMovementType.InventoryToQuest:
                SetInventorySlot(client, p.ReadByte(), null);
                break;
            case ItemMovementType.ShopToInventory:
                ShopToInventory(client, p);
                break;
            case ItemMovementType.InventoryToShop:
            {
                byte slot = p.ReadByte();
                ushort sold = p.ReadUInt16();
                ReduceInventorySlot(client, slot, sold);
                break;
            }
            case ItemMovementType.PetToInventory:
            case ItemMovementType.InventoryToPet:
            {
                uint petId = p.ReadUInt32();
                byte first = p.ReadByte(), second = p.ReadByte();
                if (client.World.Character is not { } c || !client.World.MyPets.TryGetValue(petId, out var pet))
                    break;
                if (type == ItemMovementType.PetToInventory)
                {
                    c.Inventory[second] = pet.Inventory[first];
                    pet.Inventory[first] = null;
                }
                else
                {
                    pet.Inventory[second] = c.Inventory[first];
                    c.Inventory[first] = null;
                }
                break;
            }
            case ItemMovementType.ShopToTransport:
                ShopToTransport(client, p);
                break;
            case ItemMovementType.ItemMallToInventory:
                ItemMallToInventory(client, p);
                break;
            case ItemMovementType.TransportToShop:
                TransportToShop(client, p);
                break;
        }
        client.RaiseItemMoved(true, type, 0);
    }

    /// <summary>
    /// 0xB034 type 24: bought in the item mall: shop group ID, tab group index, tab index, slot index, slot count, the
    /// inventory slots, amount. The packet has no item ID: the item comes from the shop data in the game database.
    /// </summary>
    internal static void ItemMallToInventory(SroClient client, Packet p)
    {
        ushort groupId = p.ReadUInt16();
        byte groupIndex = p.ReadByte(), tabIndex = p.ReadByte(), slotIndex = p.ReadByte();
        byte count = p.ReadByte();
        byte[] slots = p.ReadBytes(count);
        ushort quantity = p.Remaining >= 2 ? p.ReadUInt16() : count;

        var good = client.Data.GetShopGood(groupId, groupIndex, tabIndex, slotIndex);
        var info = good is null ? null : client.Data.GetItem(good.Item);
        if (good is null || info is null)
        {
            client.Logger.LogWarning(
                "Bought an item in the item mall (shop {Group}, {Index}/{Tab}/{Slot}) into slot(s) {Slots}, but the game database does not know it. "
                + "Build the database again (Server, Build database), or log in again to see it.",
                groupId, groupIndex, tabIndex, slotIndex, string.Join(", ", slots));
            return;
        }
        int amount = count != quantity ? quantity : good.Data;
        if (amount <= 0)
            amount = 1;
        foreach (byte slot in slots)
        {
            var item = ItemFactory.Create(info);
            item.Quantity = (ushort)(item.QuantityMax > 1 ? Math.Clamp(amount, 1, item.QuantityMax) : 1);
            if (item is Equipable equipable && good.Plus > 0)
                equipable.Plus = good.Plus;
            SetInventorySlot(client, slot, item);
        }
        client.Logger.LogInformation("Item mall: bought {Item} x{Amount} (slot {Slots})", info.Name.Length > 0 ? info.Name : info.ServerName,
            amount, string.Join(", ", slots));
    }

    /// <summary>0xB034 type 2: inventory slot, storage slot; onto the same item it stacks.</summary>
    private static void InventoryToStorage(SroClient client, Packet p)
    {
        byte from = p.ReadByte();
        byte to = p.Remaining >= 1 ? p.ReadByte() : byte.MaxValue;
        var item = client.World.Character is { } c && from < c.Inventory.Capacity ? c.Inventory[from] : null;
        SetInventorySlot(client, from, null);
        if (item is null || client.World.Storage is not { } storage || to >= storage.Capacity)
            return;
        if (storage[to] is { } there && there.Id == item.Id && there.QuantityMax > 1)
            there.Quantity = (ushort)Math.Min(there.QuantityMax, there.Quantity + item.Quantity);
        else
            storage[to] = item;
    }

    private static void SetInventorySlot(SroClient client, byte slot, Item? item)
    {
        if (client.World.Character is { } c)
            c.Inventory[slot] = item;
    }

    /// <summary>Removes <paramref name="amount"/> from a stack (the whole item when nothing is left).</summary>
    private static void ReduceInventorySlot(SroClient client, byte slot, ushort amount)
    {
        if (client.World.Character?.Inventory is not { } inventory || inventory[slot] is not { } item)
            return;
        if (item.QuantityMax <= 1 || amount >= item.Quantity)
            inventory[slot] = null;
        else
            item.Quantity -= amount;
    }

    private static void ShopToInventory(SroClient client, Packet p)
    {
        byte tab = p.ReadByte(), tabSlot = p.ReadByte(), packages = p.ReadByte();
        if (packages != 1)
            return;
        byte slot = p.ReadByte();
        ushort quantity = p.ReadUInt16();
        if (client.World.GetEntity(client.World.SelectedEntityId) is not { } npc ||
            ItemFactory.CreateFromShop(client.Data, npc.ServerName, tab, tabSlot) is not { } item)
            return;
        item.Quantity = quantity;
        SetInventorySlot(client, slot, item);
    }

    /// <summary>0xB04C: an item was used; the slot keeps the new quantity (0 = used up).</summary>
    public static void ItemUse(SroClient client, Packet p)
    {
        if (!p.ReadSuccess())
        {
            client.RaiseItemUsed(false, 0, 0, p.ReadErrorCode());
            return;
        }
        byte slot = p.ReadByte();
        ushort quantity = p.ReadUInt16();
        SetQuantity(client, slot, quantity);
        client.RaiseItemUsed(true, slot, quantity, 0);
    }

    /// <summary>0x3040: an inventory item changed (type 8 = quantity, e.g. arrows or potions used by the server).</summary>
    public static void ItemUpdate(SroClient client, Packet p)
    {
        byte slot = p.ReadByte();
        if (p.ReadByte() == 8)
            SetQuantity(client, slot, p.ReadUInt16());
    }

    private static void SetQuantity(SroClient client, byte slot, ushort quantity)
    {
        if (client.World.Character?.Inventory is not { } inventory || inventory[slot] is not { } item)
            return;
        if (quantity == 0)
            inventory[slot] = null;
        else
            item.Quantity = quantity;
    }

    /// <summary>0x3052: durability of a worn or carried item.</summary>
    public static void ItemDurabilityUpdate(SroClient client, Packet p)
    {
        byte slot = p.ReadByte();
        uint durability = p.ReadUInt32();
        if (client.World.Character?.Inventory[slot] is Equipable item)
            item.Durability = durability;
    }

    /// <summary>0x3092: the inventory was enlarged.</summary>
    public static void InventoryCapacityUpdate(SroClient client, Packet p)
    {
        if (p.ReadSuccess() && client.World.Character is { } c)
            c.Inventory.Resize(p.ReadByte());
    }

    /// <summary>Inventory &lt;-&gt; avatar inventory (wear / take off an avatar): the two slots swap.</summary>
    private static void AvatarSwap(SroClient client, Packet p, bool toAvatar)
    {
        byte first = p.ReadByte(), second = p.ReadByte();
        var (inventorySlot, avatarSlot) = toAvatar ? (first, second) : (second, first);
        if (client.World.Character is not { } c || inventorySlot >= c.Inventory.Capacity || avatarSlot >= c.AvatarInventory.Capacity)
            return;
        (c.Inventory[inventorySlot], c.AvatarInventory[avatarSlot]) = (c.AvatarInventory[avatarSlot], c.Inventory[inventorySlot]);
    }

    private static void InventoryToInventory(SroClient client, Packet p)
    {
        var inventory = client.World.Character?.Inventory;
        bool again;
        do
        {
            byte from = p.ReadByte();
            byte to = p.ReadByte();
            ushort moved = p.ReadUInt16();
            again = p.ReadBool();
            if (inventory is null || inventory[from] is not { } source)
                continue;

            var target = inventory[to];
            if (target is null)
            {
                if (source.QuantityMax == 1 || source.Quantity == moved)
                {
                    inventory[to] = source;
                    inventory[from] = null;
                }
                else
                {
                    var split = source.Clone();
                    split.Quantity = moved;
                    source.Quantity -= moved;
                    inventory[to] = split;
                }
            }
            else if (target.Id != source.Id || target.Quantity == target.QuantityMax || moved == target.QuantityMax)
            {
                inventory[to] = source;
                inventory[from] = target;
            }
            else
            {
                target.Quantity += moved;
                if (source.Quantity == moved)
                    inventory[from] = null;
                else
                    source.Quantity -= moved;
            }
        } while (again);
    }

    private static void ShopToTransport(SroClient client, Packet p)
    {
        uint petUniqueId = p.ReadUInt32();
        byte tab = p.ReadByte();
        byte tabSlot = p.ReadByte();
        byte packages = p.ReadByte();

        if (!client.World.MyPets.TryGetValue(petUniqueId, out var pet))
            return;
        if (client.World.GetEntity(client.World.SelectedEntityId) is not { } npc)
            return;

        var item = ItemFactory.CreateFromShop(client.Data, npc.ServerName, tab, tabSlot);
        if (item is null || packages != 1)
            return;

        byte slot = p.ReadByte();
        item.Quantity = p.ReadUInt16();
        p.ReadUInt32();
        pet.Inventory[slot] = item;
    }

    private static void TransportToShop(SroClient client, Packet p)
    {
        uint petUniqueId = p.ReadUInt32();
        byte slot = p.ReadByte();
        ushort sold = p.ReadUInt16();
        p.ReadUInt32(); // npc unique id
        p.ReadByte();

        if (!client.World.MyPets.TryGetValue(petUniqueId, out var pet) || pet.Inventory[slot] is not { } item)
            return;
        if (item.Quantity <= sold)
            pet.Inventory[slot] = null;
        else
            item.Quantity -= sold;
    }
}
