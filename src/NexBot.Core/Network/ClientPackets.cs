using NexBot.Game;
using NexBot.Game.Items;
using NexBot.Protocol;

namespace NexBot.Network;

/// <summary>
/// Builders for client to server packets. Each method returns a ready <see cref="Packet"/> and has no side
/// effects, so they are easy to test and can be sent through any connection.
/// </summary>
public static class ClientPackets
{
    #region Gateway

    public static Packet PatchRequest(byte locale, uint version) =>
        new Packet(Opcodes.Client.PatchRequest, encrypted: true)
            .WriteByte(locale)
            .WriteAscii("SR_Client")
            .WriteUInt32(version);

    public static Packet ShardListRequest() => new(Opcodes.Client.ShardListRequest);

    public static Packet LoginRequest(byte locale, string username, string password, ushort shardId) =>
        new Packet(Opcodes.Client.LoginRequest)
            .WriteByte(locale)
            .WriteAscii(username)
            .WriteAscii(password)
            .WriteUInt16(shardId);

    public static Packet CaptchaAnswer(string code) =>
        new Packet(Opcodes.Client.CaptchaAnswer).WriteAscii(code);

    #endregion

    #region Agent login / character selection

    /// <summary>Agent server login with the session ID obtained from the gateway.</summary>
    public static Packet AuthRequest(uint sessionId, string username, string password, byte locale) =>
        new Packet(Opcodes.Client.AuthRequest, encrypted: true)
            .WriteUInt32(sessionId)
            .WriteAscii(username)
            .WriteAscii(password)
            .WriteByte(locale)
            .WriteUInt32(0)  // MAC address (low)
            .WriteUInt16(0); // MAC address (high)

    public static Packet RequestCharacterList() =>
        new Packet(Opcodes.Client.CharacterSelectionAction).WriteByte((byte)CharacterSelectionAction.List);

    public static Packet CheckCharacterName(string name) =>
        new Packet(Opcodes.Client.CharacterSelectionAction)
            .WriteByte((byte)CharacterSelectionAction.CheckName)
            .WriteAscii(name);

    public static Packet CreateCharacter(string name, uint model, uint chest, uint legs, uint shoes, uint weapon) =>
        new Packet(Opcodes.Client.CharacterSelectionAction)
            .WriteByte((byte)CharacterSelectionAction.Create)
            .WriteAscii(name)
            .WriteUInt32(model)
            .WriteByte(0) // scale
            .WriteUInt32(chest)
            .WriteUInt32(legs)
            .WriteUInt32(shoes)
            .WriteUInt32(weapon);

    public static Packet SelectCharacter(string name) =>
        new Packet(Opcodes.Client.CharacterSelectionJoin).WriteAscii(name);

    public static Packet ConfirmSpawn() => new(Opcodes.Client.CharacterConfirmSpawn);

    public static Packet TeleportReady() => new(Opcodes.Client.TeleportReadyResponse);

    // ---- stall (layouts from the vSRO 1.188 client) ----
    private static Packet StallUpdate(StallUpdateType type) => new Packet(Opcodes.Client.StallUpdate).WriteByte((byte)type);

    public static Packet StallCreate(string title) => new Packet(Opcodes.Client.StallCreate).WriteAscii(title);
    public static Packet StallDestroy() => new(Opcodes.Client.StallDestroy);
    public static Packet StallNote(string note) => StallUpdate(StallUpdateType.Note).WriteAscii(note);
    public static Packet StallTitle(string title) => StallUpdate(StallUpdateType.Title).WriteAscii(title);
    public static Packet StallState(bool open) => StallUpdate(StallUpdateType.State).WriteBool(open).WriteUInt16(0);

    /// <summary>Bytes after the price in the add-item packet when nothing was learned (flea market group 0x32, then 0).</summary>
    public static readonly byte[] DefaultStallAddTrailer = [0x32, 0, 0, 0, 0, 0];

    public static Packet StallAddItem(byte stallSlot, byte inventorySlot, ushort quantity, ulong price) =>
        StallAddItem(stallSlot, inventorySlot, quantity, price, DefaultStallAddTrailer);

    /// <param name="trailer">Bytes after the price; servers differ, so the bot copies the game client's (see SroClient.StallAddTrailer).</param>
    public static Packet StallAddItem(byte stallSlot, byte inventorySlot, ushort quantity, ulong price, ReadOnlySpan<byte> trailer) =>
        StallUpdate(StallUpdateType.ItemAdded).WriteByte(stallSlot).WriteByte(inventorySlot).WriteUInt16(quantity)
            .WriteUInt64(price).WriteBytes(trailer);

    public static Packet StallUpdateItem(byte stallSlot, ushort quantity, ulong price) =>
        StallUpdate(StallUpdateType.ItemUpdate).WriteByte(stallSlot).WriteUInt16(quantity).WriteUInt64(price).WriteUInt16(0);

    public static Packet StallRemoveItem(byte stallSlot) =>
        StallUpdate(StallUpdateType.ItemRemoved).WriteByte(stallSlot).WriteUInt16(0);

    /// <summary>An alchemy fuse: [2 fuse][type][slot count][slots…] (item first, then elixir / stone, then powder / catalyst).</summary>
    public static Packet AlchemyFuse(ushort opcode, byte type, IReadOnlyList<byte> slots)
    {
        var p = new Packet(opcode, encrypted: true).WriteByte(2).WriteByte(type).WriteByte((byte)slots.Count);
        foreach (byte slot in slots)
            p.WriteByte(slot);
        return p;
    }

    /// <summary>Makes the selected teleporter NPC our return point (0x7059).</summary>
    public static Packet DesignateRecall(uint npcUniqueId) =>
        new Packet(Opcodes.Client.TeleportRecall).WriteUInt32(npcUniqueId);

    public static Packet CharacterPin(string pin) =>
        new Packet(Opcodes.Client.CharacterPin).WriteByte(1).WriteAscii(pin);

    #endregion

    #region Movement

    /// <summary>Moves the character, or a pet when <paramref name="petUniqueId"/> is not 0.</summary>
    public static Packet MoveTo(ushort region, int x, int y, int z, uint petUniqueId = 0)
    {
        Packet p;
        if (petUniqueId == 0)
        {
            p = new Packet(Opcodes.Client.Movement);
        }
        else
        {
            p = new Packet(Opcodes.Client.PetAction);
            p.WriteUInt32(petUniqueId);
            p.WriteByte((byte)CosAction.Movement);
        }

        p.WriteByte(1); // has destination
        p.WriteUInt16(region);
        if (Position.IsDungeonRegion(region))
        {
            p.WriteInt32(x).WriteInt32(z).WriteInt32(y);
        }
        else
        {
            p.WriteInt16((short)x).WriteInt16((short)z).WriteInt16((short)y);
        }
        return p;
    }

    public static Packet MoveTo(Position position, uint petUniqueId = 0) =>
        MoveTo(position.Region, position.X, position.Y, position.Z, petUniqueId);

    #endregion

    #region Items

    public static Packet UseItem(byte slot, ushort usageType, uint targetUniqueId = 0)
    {
        var p = new Packet(Opcodes.Client.ItemUse, encrypted: true)
            .WriteByte(slot)
            .WriteUInt16(usageType);
        if (targetUniqueId != 0)
            p.WriteUInt32(targetUniqueId);
        return p;
    }

    /// <summary>Uses an item on another bag item (a pet revive item on the dead pet's scroll): [slot][usage][target slot].</summary>
    public static Packet UseItemOnSlot(byte slot, ushort usageType, byte targetSlot) =>
        new Packet(Opcodes.Client.ItemUse, encrypted: true).WriteByte(slot).WriteUInt16(usageType).WriteByte(targetSlot);

    /// <summary>Sends a summoned pet back into its scroll (0x7116 [pet]).</summary>
    public static Packet UnsummonPet(uint petUniqueId) => new Packet(Opcodes.Client.PetUnsummon).WriteUInt32(petUniqueId);

    /// <summary>The attack pet's mode (0x7420 [pet][1][uint mode]): offensive, or 0 to stay passive.</summary>
    public static Packet AttackPetMode(uint petUniqueId, AttackPetSettings mode) =>
        new Packet(Opcodes.Client.PetSettingsChange).WriteUInt32(petUniqueId).WriteByte(1).WriteUInt32((uint)mode);

    public static Packet UseItem(Item item, byte slot, uint targetUniqueId = 0)
    {
        ArgumentNullException.ThrowIfNull(item);
        return UseItem(slot, item.UsageType, targetUniqueId);
    }

    /// <summary>
    /// A reverse return scroll: [slot][usage][mode], mode 2 = where the last return scroll was used, 3 = where the
    /// character last died (format not confirmed on vSRO 1.188).
    /// </summary>
    public static Packet UseReverseReturn(Item item, byte slot, byte mode)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new Packet(Opcodes.Client.ItemUse, encrypted: true).WriteByte(slot).WriteUInt16(item.UsageType).WriteByte(mode);
    }

    /// <summary>0x70A1: learns a skill or raises it to the level of <paramref name="skillId"/> (the next level's ID).</summary>
    public static Packet LearnSkill(uint skillId) =>
        new Packet(Opcodes.Client.MasterySkillLevelUp).WriteUInt32(skillId);

    /// <summary>0x70A2: raises a mastery by one level.</summary>
    public static Packet RaiseMastery(uint masteryId) =>
        new Packet(Opcodes.Client.MasteryLevelUp).WriteUInt32(masteryId).WriteByte(1);

    public static Packet MoveItem(ItemMovementType type, byte fromSlot, byte toSlot, ushort quantity = 0) =>
        new Packet(Opcodes.Client.ItemMovement)
            .WriteByte((byte)type)
            .WriteByte(fromSlot)
            .WriteByte(toSlot)
            .WriteUInt16(quantity);

    /// <summary>Summons a transport from a scroll (usage type 0x11ED).</summary>
    public static Packet SummonTransport(byte slot) =>
        new Packet(Opcodes.Client.ItemUse, encrypted: true)
            .WriteByte(slot)
            .WriteByte(0xED)
            .WriteByte(0x11);

    /// <summary>Gets on (or off) a summoned horse / transport.</summary>
    public static Packet MountPet(uint petUniqueId, bool mount = true) =>
        new Packet(Opcodes.Client.PetMount).WriteBool(mount).WriteUInt32(petUniqueId);

    #endregion

    #region Entities / NPC

    public static Packet SelectEntity(uint uniqueId) =>
        new Packet(Opcodes.Client.EntitySelection).WriteUInt32(uniqueId);

    public static Packet TalkToNpc(uint uniqueId, byte talkOption) =>
        new Packet(Opcodes.Client.EntityTalk).WriteUInt32(uniqueId).WriteByte(talkOption);

    public static Packet CloseNpc(uint uniqueId) =>
        new Packet(Opcodes.Client.NpcClose).WriteUInt32(uniqueId);

    public static Packet BuyFromNpc(uint npcUniqueId, byte tab, byte slot, ushort quantity) =>
        new Packet(Opcodes.Client.ItemMovement)
            .WriteByte(0x13) // shop -> transport
            .WriteUInt32(0)
            .WriteByte(tab)
            .WriteByte(slot)
            .WriteUInt16(quantity)
            .WriteUInt32(npcUniqueId);

    public static Packet SellToNpc(uint npcUniqueId, uint petUniqueId, byte slot, ushort quantity) =>
        new Packet(Opcodes.Client.ItemMovement)
            .WriteByte(0x14) // transport -> shop
            .WriteUInt32(petUniqueId)
            .WriteByte(slot)
            .WriteUInt16(quantity)
            .WriteUInt32(npcUniqueId);

    /// <summary>Buys from an NPC shop into the inventory (0x7034 type 8).</summary>
    public static Packet BuyToInventory(uint npcUniqueId, byte tab, byte slot, ushort quantity) =>
        new Packet(Opcodes.Client.ItemMovement)
            .WriteByte((byte)ItemMovementType.ShopToInventory)
            .WriteByte(tab)
            .WriteByte(slot)
            .WriteUInt16(quantity)
            .WriteUInt32(npcUniqueId);

    /// <summary>Sells an inventory item to an NPC shop (0x7034 type 9).</summary>
    public static Packet SellFromInventory(uint npcUniqueId, byte slot, ushort quantity) =>
        new Packet(Opcodes.Client.ItemMovement)
            .WriteByte((byte)ItemMovementType.InventoryToShop)
            .WriteByte(slot)
            .WriteUInt16(quantity)
            .WriteUInt32(npcUniqueId);

    /// <summary>Drops an inventory item on the ground (0x7034 type 7).</summary>
    public static Packet DropItem(byte slot) =>
        new Packet(Opcodes.Client.ItemMovement).WriteByte((byte)ItemMovementType.InventoryToGround).WriteByte(slot);

    /// <summary>Puts an inventory item into the storage, at a Storage-keeper (0x7034 type 2).</summary>
    public static Packet StoreItem(uint npcUniqueId, byte inventorySlot, byte storageSlot) =>
        new Packet(Opcodes.Client.ItemMovement)
            .WriteByte((byte)ItemMovementType.InventoryToStorage)
            .WriteByte(inventorySlot)
            .WriteByte(storageSlot)
            .WriteUInt32(npcUniqueId);

    /// <summary>Moves gold between the bag and the storage at a Storage-keeper (0x7034 type 12 in, type 11 out): [type][ulong][uint npc].</summary>
    public static Packet StorageGold(uint npcUniqueId, bool deposit, ulong amount) =>
        new Packet(Opcodes.Client.ItemMovement)
            .WriteByte((byte)(deposit ? ItemMovementType.InventoryGoldToStorage : ItemMovementType.StorageGoldToInventory))
            .WriteUInt64(amount)
            .WriteUInt32(npcUniqueId);

    /// <summary>Asks for the storage's contents (0x703C; answered by 0x3047, 0x3049 ..., 0x3048), once per login.</summary>
    public static Packet StorageDataRequest(uint npcUniqueId) =>
        new Packet(Opcodes.Client.StorageDataRequest).WriteUInt32(npcUniqueId).WriteByte(0);

    public static Packet UseTeleport(uint teleportUniqueId, uint destinationId) =>
        new Packet(Opcodes.Client.TeleportUse)
            .WriteUInt32(teleportUniqueId)
            .WriteByte(2)
            .WriteUInt32(destinationId);

    #endregion

    #region Combat

    /// <summary>Attacks a target with a skill. Skill ID 1 = common attack.</summary>
    public static Packet Attack(uint targetUniqueId, uint skillId)
    {
        var p = new Packet(Opcodes.Client.Action).WriteByte(1);
        if (skillId == 1)
            p.WriteByte((byte)CharacterAction.CommonAttack);
        else
            p.WriteByte((byte)CharacterAction.SkillCast).WriteUInt32(skillId);
        p.WriteByte(1); // has target
        p.WriteUInt32(targetUniqueId);
        return p;
    }

    /// <summary>
    /// Casts a skill (0x7074 [1][4][uint skill][0] without a target, [1][uint target] with one): buffs and imbues are
    /// cast without a target, or on the character itself when the skill needs one.
    /// </summary>
    public static Packet Cast(uint skillId, uint targetUniqueId = 0)
    {
        var p = new Packet(Opcodes.Client.Action).WriteByte(1).WriteByte((byte)CharacterAction.SkillCast).WriteUInt32(skillId);
        if (targetUniqueId == 0)
            p.WriteByte(0);
        else
            p.WriteByte(1).WriteUInt32(targetUniqueId);
        return p;
    }

    /// <summary>
    /// Casts a skill on a place (Ghost Walk, Teleport): 0x7074 [1][4][uint skill][2][ushort region][float x][float z][float y],
    /// the region's local coordinates as in a move.
    /// </summary>
    public static Packet CastAt(uint skillId, Position at) =>
        new Packet(Opcodes.Client.Action).WriteByte(1).WriteByte((byte)CharacterAction.SkillCast).WriteUInt32(skillId)
            .WriteByte(2).WriteUInt16(at.Region).WriteSingle(at.X).WriteSingle(at.Z).WriteSingle(at.Y);

    /// <summary>Picks up an item on the ground (0x7074 [1][2 pick up][1][uint item]): the character walks there by itself.</summary>
    public static Packet PickUp(uint itemUniqueId) =>
        new Packet(Opcodes.Client.Action).WriteByte(1).WriteByte((byte)CharacterAction.ItemPickUp).WriteByte(1).WriteUInt32(itemUniqueId);

    /// <summary>The grab pet picks up an item (0x70C5 [uint pet][8][uint item]).</summary>
    public static Packet PetPickUp(uint petUniqueId, uint itemUniqueId) =>
        new Packet(Opcodes.Client.PetAction).WriteUInt32(petUniqueId).WriteByte((byte)CosAction.ItemPickUp).WriteUInt32(itemUniqueId);

    /// <summary>Puts on a PvP cape (None removes it).</summary>
    public static Packet WearPvpCape(PvpCape cape) =>
        new Packet(Opcodes.Client.PvpCape).WriteByte((byte)cape);

    #endregion

    #region Chat / game master

    /// <summary>
    /// Chat message (0x7025). <paramref name="receiver"/> is required for <see cref="ChatType.Private"/> and ignored
    /// otherwise. <see cref="ChatType.Notice"/> and <see cref="ChatType.GameMaster"/> need a GM account.
    /// </summary>
    public static Packet Chat(ChatType type, string message, string? receiver = null, byte chatIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(message);
        var p = new Packet(Opcodes.Client.Chat).WriteByte((byte)type).WriteByte(chatIndex);
        if (type == ChatType.Private)
        {
            ArgumentException.ThrowIfNullOrEmpty(receiver);
            p.WriteAscii(receiver);
        }
        return p.WriteAscii(message);
    }

    /// <summary>Global chat: uses a global chatting item (0x704C with the item's usage type) with a message.</summary>
    public static Packet GlobalChat(byte slot, ushort usageType, string message) =>
        new Packet(Opcodes.Client.ItemUse, encrypted: true)
            .WriteByte(slot)
            .WriteUInt16(usageType)
            .WriteAscii(message);

    /// <summary>A game master command without arguments (0x7010), e.g. <see cref="GameMasterCommand.GoTown"/>.</summary>
    public static Packet GameMaster(GameMasterCommand command) =>
        new Packet(Opcodes.Client.GameMasterCommand).WriteUInt16((ushort)command);

    /// <summary>GM: spawns monsters next to the GM (0x7010 command 6: model ID, count, type 0 normal / 1 champion / 4 giant ...).</summary>
    public static Packet GmCreateMob(uint modelId, byte count, byte type) =>
        new Packet(Opcodes.Client.GameMasterCommand).WriteUInt16((ushort)GameMasterCommand.CreateMob)
            .WriteUInt32(modelId).WriteByte(count).WriteByte(type);

    /// <summary>GM: kills a monster (0x7010 command 20: unique ID).</summary>
    public static Packet GmKillMob(uint uniqueId) =>
        new Packet(Opcodes.Client.GameMasterCommand).WriteUInt16((ushort)GameMasterCommand.KillMob).WriteUInt32(uniqueId).WriteByte(1);

    /// <summary>GM: kills the monsters around the GM (0x7010 command 12, the console's /zoe).</summary>
    public static Packet GmZoe() => GameMaster(GameMasterCommand.Zoe);

    /// <summary>GM: creates an item (0x7010 command 7: item ID, plus level or amount).</summary>
    public static Packet GmMakeItem(uint itemId, byte plusOrAmount) =>
        new Packet(Opcodes.Client.GameMasterCommand).WriteUInt16((ushort)GameMasterCommand.MakeItem).WriteUInt32(itemId).WriteByte(plusOrAmount);

    /// <summary>A game master command that targets a character (<see cref="GameMasterCommand.ToTown"/>, RecallUser ...).</summary>
    public static Packet GameMaster(GameMasterCommand command, string characterName) =>
        new Packet(Opcodes.Client.GameMasterCommand).WriteUInt16((ushort)command).WriteAscii(characterName);

    #endregion

    #region Party / petitions

    public static Packet PetitionResponse(bool accept, PlayerPetition type)
    {
        var p = new Packet(Opcodes.Client.PlayerPetitionResponse);
        if (accept)
            return p.WriteByte(1).WriteByte(1);
        return type is PlayerPetition.PartyInvitation or PlayerPetition.PartyCreation
            ? p.WriteByte(2).WriteByte(0x0C).WriteByte(0x2C)
            : p.WriteByte(1).WriteByte(0);
    }

    public static Packet InviteToParty(uint targetUniqueId, PartySetup setup = PartySetup.All) =>
        new Packet(Opcodes.Client.PartyCreation).WriteUInt32(targetUniqueId).WriteByte((byte)setup);

    /// <summary>Invites a player into the party we are already in (0x7062).</summary>
    public static Packet InviteToCurrentParty(uint targetUniqueId) =>
        new Packet(Opcodes.Client.PartyInvitation).WriteUInt32(targetUniqueId);

    /// <summary>Leaves the party (0x7061).</summary>
    public static Packet LeaveParty() => new(Opcodes.Client.PartyLeave);

    /// <summary>Adds one stat point to STR (0x7050) or INT (0x7051). No payload.</summary>
    public static Packet AddStat(bool strength) => new(strength ? Opcodes.Client.AddStr : Opcodes.Client.AddInt);

    /// <summary>Repairs every damaged item at the open blacksmith / armor shop (0x703E [npc][2]).</summary>
    public static Packet RepairAll(uint npcUniqueId) => new Packet(Opcodes.Client.ItemRepair).WriteUInt32(npcUniqueId).WriteByte(2);

    /// <summary>Repairs one inventory slot at the open blacksmith / armor shop (0x703E [npc][1][slot]).</summary>
    public static Packet RepairItem(uint npcUniqueId, byte slot) => new Packet(Opcodes.Client.ItemRepair).WriteUInt32(npcUniqueId).WriteByte(1).WriteByte(slot);

    /// <summary>Revives a dead character at its return point in town (0x3053, type 1; type 2 is "here", with an item).</summary>
    public static Packet ReviveInTown() => new Packet(Opcodes.Client.AutoResurrection).WriteByte(1);

    /// <summary>Revives where the character died with a resurrection scroll (0x3053, type 2).</summary>
    public static Packet ReviveHere() => new Packet(Opcodes.Client.AutoResurrection).WriteByte(2);

    /// <summary>Turns berserk on when the gauge is full (0x70A7).</summary>
    public static Packet Berserk() => new Packet(Opcodes.Client.Berserk).WriteByte(1);

    /// <summary>Kicks a member out of our party (0x7063, master only).</summary>
    public static Packet KickFromParty(uint memberId) =>
        new Packet(Opcodes.Client.PartyKick).WriteUInt32(memberId);

    /// <summary>Asks for a page of the party matching list (0x706C).</summary>
    public static Packet RequestPartyMatchList(byte page = 0) =>
        new Packet(Opcodes.Client.PartyMatchList).WriteByte(page);

    /// <summary>Invites a player into our guild (0x70F3).</summary>
    public static Packet InviteToGuild(uint targetUniqueId) =>
        new Packet(Opcodes.Client.GuildInvitation).WriteUInt32(targetUniqueId);

    /// <summary>
    /// Invites another guild's master to join our union (0x70FB [target]). Only a guild master can send it, and the
    /// player must be the master of a guild that is in no union.
    /// </summary>
    public static Packet InviteToUnion(uint targetUniqueId) =>
        new Packet(Opcodes.Client.UnionInvitation).WriteUInt32(targetUniqueId);

    /// <summary>Asks a player to exchange (0x7081 [target]).</summary>
    public static Packet ExchangeRequest(uint targetUniqueId) =>
        new Packet(Opcodes.Client.ExchangeInvitation).WriteUInt32(targetUniqueId);

    /// <summary>Exchange: lock our side (0x7082).</summary>
    public static Packet ExchangeConfirm() => new(Opcodes.Client.ExchangeConfirm);

    /// <summary>Exchange: approve once both confirmed (0x7083).</summary>
    public static Packet ExchangeApprove() => new(Opcodes.Client.ExchangeApprove);

    /// <summary>Exchange: cancel and close the window (0x7084).</summary>
    public static Packet ExchangeCancel() => new(Opcodes.Client.ExchangeExit);

    /// <summary>Exchange: put a bag item into the window (0x7034 [4][slot]).</summary>
    public static Packet ExchangeAddItem(byte inventorySlot) =>
        new Packet(Opcodes.Client.ItemMovement).WriteByte((byte)ItemMovementType.InventoryToExchange).WriteByte(inventorySlot);

    /// <summary>Exchange: take an item back out of the window (0x7034 [5][window slot]).</summary>
    public static Packet ExchangeRemoveItem(byte exchangeSlot) =>
        new Packet(Opcodes.Client.ItemMovement).WriteByte((byte)ItemMovementType.ExchangeToInventory).WriteByte(exchangeSlot);

    /// <summary>Exchange: the gold we offer (0x7034 [13][ulong gold]).</summary>
    public static Packet ExchangeGold(ulong gold) =>
        new Packet(Opcodes.Client.ItemMovement).WriteByte((byte)ItemMovementType.InventoryGoldToExchange).WriteUInt64(gold);

    /// <summary>Invites a player into our academy (0x7472).</summary>
    public static Packet InviteToAcademy(uint targetUniqueId) =>
        new Packet(Opcodes.Client.AcademyInvitation).WriteUInt32(targetUniqueId);

    /// <summary>Follows a player with the game's trace (0x7074 action 3).</summary>
    public static Packet Trace(uint targetUniqueId) =>
        new Packet(Opcodes.Client.Action).WriteByte(1).WriteByte((byte)CharacterAction.Trace).WriteByte(1).WriteUInt32(targetUniqueId);

    public static Packet CreatePartyMatch(string title, PartySetup setup = PartySetup.All,
        PartyPurpose purpose = PartyPurpose.Hunting, byte levelMin = 1, byte levelMax = 110) =>
        new Packet(Opcodes.Client.PartyMatchCreation)
            .WriteUInt32(0)
            .WriteUInt32(0)
            .WriteByte((byte)setup)
            .WriteByte((byte)purpose)
            .WriteByte(levelMin)
            .WriteByte(levelMax)
            .WriteAscii(title);

    public static Packet DeletePartyMatch(uint number) =>
        new Packet(Opcodes.Client.PartyMatchDelete).WriteUInt32(number);

    public static Packet JoinPartyMatch(uint number) =>
        new Packet(Opcodes.Client.PartyMatchJoin).WriteUInt32(number);

    public static Packet PartyMatchJoinResponse(uint requestId, uint joinId, bool accept) =>
        new Packet(Opcodes.Client.PartyMatchJoinResponse)
            .WriteUInt32(requestId)
            .WriteUInt32(joinId)
            .WriteBool(accept);

    #endregion
}
