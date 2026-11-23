using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Data;
using NexBot.Game;
using NexBot.Map;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>Dictionary-backed <see cref="IGameData"/> for tests (from POC-2).</summary>
internal sealed class InMemoryGameData : IGameData
{
    public Dictionary<uint, ModelInfo> Models { get; } = [];
    public Dictionary<uint, ItemInfo> Items { get; } = [];

    public ModelInfo GetModel(uint id) => Models.GetValueOrDefault(id);
    public ModelInfo GetModel(string serverName) => Models.Values.FirstOrDefault(m => m.ServerName == serverName);
    public ItemInfo GetItem(uint id) => Items.GetValueOrDefault(id);
    public ItemInfo GetItem(string serverName) => Items.Values.FirstOrDefault(i => i.ServerName == serverName);
    public Dictionary<uint, SkillInfo> Skills { get; } = [];
    public Dictionary<(uint, byte, byte, byte), ShopGoodInfo> Goods { get; } = [];
    public IReadOnlyList<ModelInfo> GetModelsByPrefix(string prefix) =>
        Models.Values.Where(m => m.ServerName.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(m => m.Id).ToList();
    public ShopGoodInfo GetShopGood(uint groupId, byte groupIndex, byte tabIndex, byte slot) => Goods.GetValueOrDefault((groupId, groupIndex, tabIndex, slot));
    public Dictionary<uint, MasteryInfo> Masteries { get; } = [];
    public Dictionary<uint, List<SkillTreeInfo>> Trees { get; } = [];
    public Dictionary<byte, LevelInfo> Levels { get; } = [];
    public SkillInfo GetSkill(uint id) => Skills.GetValueOrDefault(id);
    public SkillInfo GetSkill(string serverName) => Skills.Values.FirstOrDefault(s => s.ServerName == serverName);
    public MagicOptionInfo GetMagicOption(uint id) => null;
    public MasteryInfo GetMastery(uint id) => Masteries.GetValueOrDefault(id);
    public IReadOnlyList<MasteryInfo> GetMasteries() => [.. Masteries.Values.OrderBy(m => m.Id)];
    public IReadOnlyList<SkillTreeInfo> GetSkillTree(uint masteryId) => Trees.GetValueOrDefault(masteryId);
    public TeleportInfo GetTeleport(uint id) => null;
    public Dictionary<uint, List<TeleportLinkInfo>> TeleportLinks { get; } = [];
    public IReadOnlyList<ItemInfo> GetItemsOfType(byte tid2, byte tid3) => [.. Items.Values.Where(i => i.Tid2 == tid2 && i.Tid3 == tid3).OrderBy(i => i.Id)];
    public IReadOnlyList<TeleportLinkInfo> GetTeleportLinks(uint sourceTeleportId) =>
        TeleportLinks.TryGetValue(sourceTeleportId, out var links) ? links : [];
    public LevelInfo GetLevel(byte level) => Levels.GetValueOrDefault(level);
    public Dictionary<string, List<ShopSlotInfo>> Shops { get; } = [];
    public ShopItemInfo GetShopItem(string npcServerName, byte tab, byte slot) =>
        Shops.GetValueOrDefault(npcServerName)?.FirstOrDefault(s => s.Tab == tab && s.Slot == slot) is { } s ? new ShopItemInfo(s.ItemServerName, 0, 0, "") : null;
    public IReadOnlyList<ShopSlotInfo> GetShopItems(string npcServerName) => Shops.GetValueOrDefault(npcServerName) ?? [];
    public IReadOnlyList<ModelInfo> GetSpecialtyTraders() => [.. Models.Values.Where(m => Shops.ContainsKey(m.ServerName))];
    public string GetRegionName(ushort regionId) => regionId == 0x60A8 ? "Jangan" : null;
    public string GetUiText(string serverName) => null;
    public uint GetCommonAttackSkillId(WeaponType weapon) => 0;
}

/// <summary>The world tracker fed by the proxy, and the map snapshot built from it.</summary>
public class WorldTests
{
    /// <summary>Real 0x3013 captured from a vSRO 1.188 server (POC-2's CharacterDataTests: GM "Test", Lv 101).</summary>
    internal const string CapturedHex =
        "DAB6200473070000226565DC1B013F000000008400000000E8764817000000809698002C010000000000F5210000F5210000010000000000000000002D0A0100000000380E00000000000000000000002C00000000010002000400000000390E00000000000000000000002C000000000100020005000000003A0E00000000000000000000002C00000000010002000600000000306600000800000000000000008C00000000010002000700000000862A00000000000000000000002E00000000010002000D00000000421D000001000E00000000431D000001000F000000008E28000014001000000000300E00000000000000000000003B00000000010002001100000000306600000100000000000000008C00000000010002000500000101010000000102010000000103010000000111010000000112010000000113010000000114010000000200020100010000000202000000000008018D010000100018080101001600534E5F434F4E5F5145565F414C4C5F42415349435F30010000000000000000005F1B0100A8600080784437A938BE0020CF44FA74000100FA740000000000008041000048420000C8420004005465737400000001000000000000000000000000000000FF170280000000000002000000010704014AEA030064024AF0030064034AEE030064044AF803006400000000000000000100010000";

    private const uint MobRefId = 1954;

    internal static InMemoryGameData Data()
    {
        var data = new InMemoryGameData();
        data.Models[1907] = new ModelInfo(1907, "CHAR_CH_MAN_ADVENTURER", "Adventurer", 1, 0, 0, 200);
        data.Models[MobRefId] = new ModelInfo(MobRefId, "MOB_CH_MANGNYANG", "Mangnyang", 2, 1, 1, 52);
        foreach (uint id in new uint[] { 0x0E38, 0x0E39, 0x0E3A, 0x0E30, 0x6630, 0x2A86 })
            data.Items[id] = new ItemInfo(id, $"ITEM_EQUIP_{id:X}", "Equipment", 1, 1, 1, 0, 0, "", 1, 1);
        foreach (uint id in new uint[] { 0x1D42, 0x1D43, 0x288E })
            data.Items[id] = new ItemInfo(id, $"ITEM_ETC_{id:X}", "Potion", 3, 1, 1, 0, 0, "", 50, 1);
        return data;
    }

    private static Packet MobSpawn(uint uniqueId, ushort region, float x, float y) =>
        new Packet(Opcodes.Server.EntitySpawn)
            .WriteUInt32(MobRefId).WriteUInt32(uniqueId)
            .WriteUInt16(region).WriteSingle(x).WriteSingle(0f).WriteSingle(y)
            .WriteUInt16(0).WriteBool(false).WriteByte(1).WriteByte(0).WriteUInt16(1234)
            .WriteByte((byte)LifeState.Alive).WriteByte(0).WriteByte(0).WriteByte(0)
            .WriteSingle(16f).WriteSingle(50f).WriteSingle(100f)
            .WriteByte(0).WriteByte(0).WriteByte((byte)MobType.Unique).WriteByte(0);

    [Fact]
    public void Captured_character_and_spawns_end_up_on_the_map()
    {
        var sent = new List<Packet>();
        using var client = new SroClient(Data(), NullLogger.Instance, p => { sent.Add(p); return true; });

        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));

        Assert.True(client.IsInGame);
        Assert.Equal("Test", client.CharacterName);
        Assert.Empty(sent); // the world tracker never answers on its own (the client / BotSession does)

        var c = client.World.Character!;
        var me = c.EstimatePosition();
        // A unique 50 units east of the character, in the same region.
        client.HandlePacket(MobSpawn(900, me.Region, me.X / 10f * 10 + 500, me.Y / 10f * 10));

        var map = MapSnapshot.Take(client);
        Assert.NotNull(map);
        Assert.Equal("Test", map.Me.Name);
        Assert.Equal(0x60A8, map.Me.Region);
        Assert.Equal("Jangan", map.Me.RegionName);
        Assert.Equal(101, map.Status.Level);
        Assert.Null(map.Me.Layer);
        var unique = Assert.Single(map.Entities);
        Assert.Equal(MapEntityKind.Unique, unique.Kind);
        Assert.Equal("Mangnyang", unique.Name);
        Assert.InRange(unique.X - map.Me.X, 49, 51);

        // Despawn removes it again.
        client.HandlePacket(new Packet(Opcodes.Server.EntityDespawn).WriteUInt32(900));
        Assert.Empty(MapSnapshot.Take(client)!.Entities);
    }

    [Fact]
    public void Click_to_walk_sends_a_move_and_refuses_far_points()
    {
        var sent = new List<Packet>();
        using var client = new SroClient(Data(), NullLogger.Instance, p => { sent.Add(p); return true; });
        Assert.Equal("The character is not in game.", MapSnapshot.WalkTo(client, 0, 0));

        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
        var me = MapSnapshot.Take(client)!.Me;

        Assert.Null(MapSnapshot.WalkTo(client, me.X + 20, me.Y - 10));
        Assert.Equal(Opcodes.Client.Movement, Assert.Single(sent).Opcode);
        Assert.NotNull(MapSnapshot.WalkTo(client, me.X + 5000, me.Y));
        Assert.Single(sent);
    }

    [Fact]
    public void Character_sheet_shows_stats_equipment_and_bag()
    {
        using var client = new SroClient(Data(), NullLogger.Instance, _ => true);
        Assert.Null(CharacterSheet.Take(client));

        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        // Attack, defense, STR and INT come in 0x303D right after the spawn.
        client.HandlePacket(new Packet(Opcodes.Server.CharacterStatsUpdate)
            .WriteUInt32(100).WriteUInt32(150).WriteUInt32(200).WriteUInt32(260)
            .WriteUInt16(300).WriteUInt16(310).WriteUInt16(40).WriteUInt16(50)
            .WriteUInt32(9000).WriteUInt32(8000).WriteUInt16(120).WriteUInt16(130));

        var sheet = CharacterSheet.Take(client);
        Assert.NotNull(sheet);
        Assert.Equal("Test", sheet.Name);
        Assert.Equal(101, sheet.Level);
        Assert.True(sheet.IsGameMaster);
        Assert.True(sheet.InventoryCapacity > 0);
        Assert.Equal((120, 130), (sheet.Strength, sheet.Intellect));
        Assert.Equal((100u, 150u, 300), (sheet.PhysicalAttackMin, sheet.PhysicalAttackMax, (int)sheet.PhysicalDefense));
        Assert.Equal((9000u, 8000u), (sheet.HpMax, sheet.MpMax));

        // Worn items sit in slots 0-12 with their slot names; bag items from slot 13 on.
        Assert.NotEmpty(sheet.Equipment);
        Assert.All(sheet.Equipment, i => Assert.Equal(CharacterSheet.EquipmentSlots[i.Slot], i.SlotName));
        Assert.All(sheet.Items, i => Assert.True(i.Slot >= CharacterSheet.FirstBagSlot && i.SlotName is null));
        var potion = Assert.Single(sheet.Items, i => i.QuantityMax == 50 && i.Slot == sheet.Items.Where(x => x.QuantityMax == 50).Min(x => x.Slot));
        Assert.Equal("Item", potion.Kind);
        Assert.InRange(potion.Quantity, 1, 50);
    }

    [Fact]
    public void Bad_packets_are_skipped_without_throwing()
    {
        using var client = new SroClient(Data(), NullLogger.Instance, _ => true);
        client.HandlePacket(new Packet(Opcodes.Server.EntitySpawn).WriteUInt32(123456).WriteUInt32(1));
        client.HandlePacket(new Packet(Opcodes.Server.EntityMovement).WriteByte(1));
        Assert.Empty(client.World.Entities);
    }
}
