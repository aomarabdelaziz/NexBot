using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Game.Items;
using NexBot.Network;
using NexBot.Protocol;
using NexBot.Sessions;
using Xunit;

namespace NexBot.Tests;

/// <summary>Protection: potions, return reasons, stat points.</summary>
public class ProtectionTests
{
    private static readonly ItemInfo HpPotion = new(4, "ITEM_ETC_HP_POTION_05", "HP recovery potion (XL)", 3, 1, 1, 0, 0, "", 50, 1);
    private static readonly ItemInfo MpPotion = new(11, "ITEM_ETC_MP_POTION_05", "MP recovery potion (XL)", 3, 1, 2, 0, 0, "", 50, 1);
    private static readonly ItemInfo Universal = new(56, "ITEM_ETC_CURE_ALL_05", "Universal pill (XL)", 3, 2, 6, 0, 0, "", 50, 1);
    private static readonly ItemInfo Arrow = new(62, "ITEM_ETC_AMMO_ARROW_01", "Arrow", 3, 4, 1, 0, 0, "", 250, 1);
    private static readonly ItemInfo Bow = new(3700, "ITEM_CH_BOW_08_A", "Bow", 1, 6, 6, 0, 0, "", 1, 64);

    private static (SroClient Client, ConcurrentQueue<Packet> Sent) Client()
    {
        var data = WorldTests.Data();
        foreach (var i in new[] { HpPotion, MpPotion, Universal, Arrow, Bow })
            data.Items[i.Id] = i;
        var sent = new ConcurrentQueue<Packet>();
        var client = new SroClient(data, NullLogger.Instance, p =>
        {
            sent.Enqueue(p);
            return true;
        });
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataBegin));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterData, Convert.FromHexString(WorldTests.CapturedHex)));
        client.HandlePacket(new Packet(Opcodes.Server.CharacterDataEnd));
        client.HandlePacket(new Packet(Opcodes.Server.CelestialPosition).WriteUInt32(72543));
        var c = client.World.Character!;
        for (int i = Character.FirstInventorySlot; i < c.Inventory.Capacity; i++)
            c.Inventory[i] = null;
        c.HpMax = 1000;
        c.Hp = 1000;
        c.MpMax = 1000;
        c.Mp = 1000;
        c.BadStatus = BadStatus.None;
        return (client, sent);
    }

    private static void Put(SroClient client, int slot, ItemInfo info, ushort quantity = 1)
    {
        var item = ItemFactory.Create(info);
        item.Quantity = quantity;
        client.World.Character!.Inventory[slot] = item;
    }

    [Fact]
    public void Potion_is_chosen_by_percent_and_pill_by_bad_status()
    {
        var (client, _) = Client();
        using var _c = client;
        var c = client.World.Character!;
        var s = new ProtectionSettings();
        Put(client, 13, HpPotion, 50);
        Put(client, 14, MpPotion, 50);
        Put(client, 15, Universal, 5);

        Assert.Null(ProtectionManager.PotionNeeded(c, s));
        c.Hp = 300;
        Assert.Equal(Supply.HpPotion, ProtectionManager.PotionNeeded(c, s));
        c.Hp = 1000;
        c.Mp = 100;
        Assert.Equal(Supply.MpPotion, ProtectionManager.PotionNeeded(c, s));
        c.BadStatus = BadStatus.Burn;
        Assert.Equal(Supply.UniversalPill, ProtectionManager.PotionNeeded(c, s));
        s.MpPotion.On = false;
        s.UniversalPill = false;
        Assert.Null(ProtectionManager.PotionNeeded(c, s));
    }

    [Fact]
    public void Return_reasons_follow_supplies_and_switches()
    {
        var (client, _) = Client();
        using var _c = client;
        var c = client.World.Character!;
        var s = new ProtectionSettings { MpPotionsLeft = new(false, 5), InventoryFull = new(false, 2), WeaponDurability = new(false, 4) };
        Put(client, 13, HpPotion, 8);
        Assert.Contains("HP potions", ProtectionManager.SupplyReason(c, s));
        s.HpPotionsLeft.On = false;
        Assert.Null(ProtectionManager.SupplyReason(c, s));

        // A bow with no arrows: back to town; with arrows: fine.
        Put(client, ProtectionCatalog.WeaponSlot, Bow);
        Assert.Equal(Supply.Arrow, ProtectionCatalog.AmmoFor(c));
        Assert.Equal("out of arrows", ProtectionManager.SupplyReason(c, s));
        Put(client, 20, Arrow, 200);
        Assert.Null(ProtectionManager.SupplyReason(c, s));
    }

    [Fact]
    public void Stat_points_keep_the_chosen_split()
    {
        int str = 0, @int = 0;
        for (int i = 0; i < 30; i++)
        {
            if (ProtectionManager.NextStatIsStr(2, str, @int))
                str++;
            else
                @int++;
        }
        Assert.Equal(20, str);
        Assert.Equal(10, @int);
        Assert.True(ProtectionManager.NextStatIsStr(3, 5, 0));
        Assert.False(ProtectionManager.NextStatIsStr(0, 0, 5));
        Assert.Equal(Opcodes.Client.AddStr, ClientPackets.AddStat(true).Opcode);
        Assert.Equal(Opcodes.Client.AddInt, ClientPackets.AddStat(false).Opcode);
    }

    [Fact]
    public async Task Low_hp_uses_a_potion_and_auto_stats_spend_points()
    {
        var (client, sent) = Client();
        using var _c = client;
        var c = client.World.Character!;
        Put(client, 13, HpPotion, 50);
        c.Hp = 200;
        c.StatPoints = 3;
        var s = new ProtectionSettings { AutoStats = true, StrPerLevel = 3 };
        using var protection = new ProtectionManager(client, s, new TrainingAreaSettings(), () => false, startLoop: false);

        await protection.StepAsync(CancellationToken.None);

        Assert.Contains(sent, p => p.Opcode == Opcodes.Client.ItemUse);
        Assert.Contains(sent, p => p.Opcode == Opcodes.Client.AddStr);
    }

    [Fact]
    public void Level_up_message_raises_the_level_once_even_after_the_exp_counted_it()
    {
        var (client, _) = Client();
        using var _c = client;
        var c = client.World.Character!;
        c.Exp = 0;
        byte start = c.Level;
        var data = (InMemoryGameData)client.Data;
        for (int l = start; l < start + 4; l++)
            data.Levels[(byte)l] = new LevelInfo((byte)l, 100, 0, 0, 0, 0);
        Packet LevelUp() => new(Opcodes.Server.EntityLevelUp, new Packet(Opcodes.Server.EntityLevelUp).WriteUInt32(c.UniqueId).ToArray());

        // EXP counted the level, then the level-up message comes: still one level.
        var exp = new Packet(Opcodes.Server.CharacterExperienceUpdate);
        exp.WriteUInt32(0).WriteUInt64(100).WriteUInt64(0);
        client.HandlePacket(new Packet(exp.Opcode, exp.ToArray()));
        client.HandlePacket(LevelUp());
        Assert.Equal(start + 1, c.Level);

        // Level-ups without EXP (GM, quests): each message is one level.
        client.HandlePacket(LevelUp());
        client.HandlePacket(LevelUp());
        Assert.Equal(start + 3, c.Level);
    }

    [Fact]
    public void Level_up_gives_3_stat_points_and_the_server_answer_spends_them()
    {
        var (client, _) = Client();
        using var _c = client;
        var c = client.World.Character!;
        c.StatPoints = 0;
        c.Exp = 0;
        var data = (InMemoryGameData)client.Data;
        data.Levels[c.Level] = new LevelInfo(c.Level, 100, 0, 0, 0, 0);
        data.Levels[(byte)(c.Level + 1)] = new LevelInfo((byte)(c.Level + 1), 1000, 0, 0, 0, 0);

        var exp = new Packet(Opcodes.Server.CharacterExperienceUpdate);
        exp.WriteUInt32(0).WriteUInt64(150).WriteUInt64(0);
        client.HandlePacket(new Packet(exp.Opcode, exp.ToArray()));
        Assert.Equal(3, c.StatPoints);

        client.HandlePacket(new Packet(Opcodes.Server.CharacterAddStrResponse, [1]));
        Assert.Equal(2, c.StatPoints);
        client.HandlePacket(new Packet(Opcodes.Server.CharacterAddIntResponse, [2, 0x01, 0x30]));
        Assert.Equal(0, c.StatPoints);
    }

    [Fact]
    public async Task Return_checks_run_outside_town_without_a_script()
    {
        var scroll = new ItemInfo(61, "ITEM_ETC_SCROLL_RETURN_01", "Return Scroll", 3, 3, 1, 0, 0, "", 50, 1);
        var (client, sent) = Client();
        using var _c = client;
        ((InMemoryGameData)client.Data).Items[scroll.Id] = scroll;
        Put(client, 13, scroll, 5);
        Put(client, 14, HpPotion, 50);
        var s = new ProtectionSettings { OutOfSpeedScrolls = true, ReturnOutsideTown = false };
        s.InventoryFull.On = false;
        s.WeaponDurability.On = false;
        using var protection = new ProtectionManager(client, s, new TrainingAreaSettings(), () => false, startLoop: false)
            { OutsideTownDelay = TimeSpan.Zero };

        await protection.StepAsync(CancellationToken.None);
        Assert.DoesNotContain(sent, p => p.Opcode == Opcodes.Client.ItemUse);

        s.ReturnOutsideTown = true;
        Assert.False(protection.InTown());
        Assert.Contains(protection.Explain(), l => l.StartsWith("[RETURN]") && l.Contains("speed scrolls"));
        await protection.StepAsync(CancellationToken.None);
        Assert.Contains(sent, p => p.Opcode == Opcodes.Client.ItemUse);
    }

    [Fact]
    public async Task A_return_asked_for_the_dead_pet_is_done_even_when_not_botting()
    {
        var scroll = new ItemInfo(61, "ITEM_ETC_SCROLL_RETURN_01", "Return Scroll", 3, 3, 1, 0, 0, "", 50, 1);
        var (client, sent) = Client();
        using var _c = client;
        ((InMemoryGameData)client.Data).Items[scroll.Id] = scroll;
        Put(client, 13, scroll, 5);
        var s = new ProtectionSettings { ReturnOutsideTown = false }; // the return checks are off: not botting
        s.InventoryFull.On = false;
        s.WeaponDurability.On = false;
        using var protection = new ProtectionManager(client, s, new TrainingAreaSettings(), () => false, startLoop: false);

        await protection.StepAsync(CancellationToken.None);
        Assert.DoesNotContain(sent, p => p.Opcode == Opcodes.Client.ItemUse);
        protection.RequestReturn("the attack pet died");
        await protection.StepAsync(CancellationToken.None);
        Assert.Contains(sent, p => p.Opcode == Opcodes.Client.ItemUse);
    }

    [Fact]
    public void Own_attacks_use_up_the_equipped_arrows_then_out_of_arrows_returns()
    {
        var (client, _) = Client();
        using var _c = client;
        var c = client.World.Character!;
        Put(client, ProtectionCatalog.WeaponSlot, Bow, 1);
        Put(client, ProtectionCatalog.ShieldSlot, Arrow, 2);
        var s = new ProtectionSettings { OutOfArrows = true };
        s.InventoryFull.On = false;
        s.WeaponDurability.On = false;
        s.HpPotionsLeft.On = false;
        s.MpPotionsLeft.On = false;
        Assert.Null(ProtectionManager.SupplyReason(c, s));

        for (int shot = 0; shot < 2; shot++)
        {
            var p = new Packet(Opcodes.Server.EntitySkillStart);
            p.WriteByte(1).WriteByte((byte)SkillCastType.Buff).WriteByte(0).WriteUInt32(1).WriteUInt32(c.UniqueId).WriteUInt32((uint)(100 + shot)).WriteUInt32(0);
            client.HandlePacket(new Packet(p.Opcode, p.ToArray()));
        }
        Assert.Null(c.Inventory[ProtectionCatalog.ShieldSlot]);
        Assert.Equal("out of arrows", ProtectionManager.SupplyReason(c, s));
    }

    [Fact]
    public void Any_return_scroll_is_found_and_an_instant_one_is_used_first()
    {
        var normal = new ItemInfo(61, "ITEM_ETC_SCROLL_RETURN_01", "Return Scroll", 3, 3, 1, 0, 0, "", 50, 1);
        var instant = new ItemInfo(3769, "ITEM_MALL_RETURN_SCROLL_HIGH_SPEED", "Instant Return Scroll", 3, 3, 1, 1, 0, "", 50, 1);
        var order = new ItemInfo(9233, "ITEM_QNO_WC_SMITH_3_02", "Order of Instant Return", 3, 3, 1, 0, 0, "", 1, 1);
        var den = new ItemInfo(2128, "ITEM_ETC_SCROLL_RETURN_THIEFDEN_01", "Bandit Den Return Scroll", 3, 3, 1, 0, 0, "", 50, 1);
        var (client, _) = Client();
        using var _c = client;
        var c = client.World.Character!;
        Put(client, 13, den, 1);
        Assert.Equal(-1, ProtectionCatalog.ReturnScrollSlot(c));
        Put(client, 14, normal, 5);
        Assert.Equal(14, ProtectionCatalog.ReturnScrollSlot(c));
        Put(client, 15, instant, 5);
        Assert.Equal(15, ProtectionCatalog.ReturnScrollSlot(c));
        c.Inventory[14] = null;
        c.Inventory[15] = null;
        Put(client, 16, order, 1);
        Assert.Equal(16, ProtectionCatalog.ReturnScrollSlot(c));
    }

    [Fact]
    public void Buffs_are_added_and_removed_by_the_server_packets()
    {
        var (client, _) = Client();
        using var _c = client;
        var data = (InMemoryGameData)client.Data;
        var speed = new SkillInfo(9001, "SKILL_ETC_SPEED_SCROLL", "Speed scroll", 77, "", 0, 1_800_000, 0, 0, false, 1, "", "speed.ddj", default, default);
        data.Skills[speed.Id] = speed;
        var c = client.World.Character!;
        int before = c.Buffs.Count;

        client.HandlePacket(new Packet(Opcodes.Server.EntitySkillBuffAdded, new Packet(Opcodes.Server.EntitySkillBuffAdded)
            .WriteUInt32(c.UniqueId).WriteUInt32(speed.Id).WriteUInt32(555).ToArray()));
        Assert.Equal(before + 1, c.Buffs.Count);
        var buff = c.Buffs[77];
        Assert.Equal(555u, buff.UniqueId);
        Assert.True(buff.Remaining > TimeSpan.FromMinutes(29));
        var sheet = NexBot.Map.CharacterSheet.Take(client)!;
        Assert.Contains(sheet.Buffs!, b => b.Name == "Speed scroll" && b.Icon == "speed.ddj");

        // The game's card lines: the mall 20% damage scroll's params.
        var scroll = speed with { Params = "3|1667396966|1685418593|1800000|6582901|20|20|0|0" };
        Assert.Equal(["Physical damage 20% Increase", "Magical damage 20% Increase"], BuffEffects.Describe(scroll));
        Assert.Equal(["Max HP 500 Increase"], BuffEffects.Describe(speed with { Params = "3|6844521|500|0|0" }));

        client.HandlePacket(new Packet(Opcodes.Server.EntitySkillBuffRemoved, new Packet(Opcodes.Server.EntitySkillBuffRemoved)
            .WriteByte(1).WriteUInt32(555).ToArray()));
        Assert.Equal(before, c.Buffs.Count);
    }

    [Fact]
    public void Item_mall_purchase_puts_the_item_in_the_bag()
    {
        var (client, _) = Client();
        using var _c = client;
        var data = (InMemoryGameData)client.Data;
        var chat = new ItemInfo(3795, "ITEM_MALL_GLOBAL_CHATTING", "Global chatting", 3, 3, 5, 1, 0, "", 50, 1);
        data.Items[chat.Id] = chat;
        data.Goods[(977, 0, 3, 0)] = new ShopGoodInfo(chat.ServerName, 0, 11);
        var c = client.World.Character!;

        // 0xB034: success, type 24, shop group 977, tab group 0, tab 3, slot 0, one inventory slot (20), amount 1.
        var p = new Packet(Opcodes.Server.ItemMovementResponse);
        p.WriteByte(1).WriteByte(24).WriteUInt16(977).WriteByte(0).WriteByte(3).WriteByte(0).WriteByte(1).WriteByte(20).WriteUInt16(1);
        client.HandlePacket(new Packet(p.Opcode, p.ToArray()));

        Assert.Equal("ITEM_MALL_GLOBAL_CHATTING", c.Inventory[20]?.ServerName);
        Assert.Equal(11, c.Inventory[20]!.Quantity); // the package's amount
    }

    [Fact]
    public void Portrait_is_the_models_place_in_its_race_and_gender()
    {
        var data = new InMemoryGameData();
        foreach (var (id, code) in new[] { (1907u, "CHAR_CH_MAN_ADVENTURER"), (1908u, "CHAR_CH_MAN_BOGY"), (1920u, "CHAR_CH_WOMAN_ADVENTURER"), (1921u, "CHAR_CH_WOMAN_ASSASSIN") })
            data.Models[id] = new ModelInfo(id, code, code, 1, 0, 0, 100);
        Assert.Equal(@"interface\character\char_ch_man2.ddj", NexBot.Map.CharacterSheet.PortraitOf(data, "CHAR_CH_MAN_BOGY", 1908));
        Assert.Equal(@"interface\character\char_ch_woman1.ddj", NexBot.Map.CharacterSheet.PortraitOf(data, "CHAR_CH_WOMAN_ADVENTURER", 1920));
        Assert.Null(NexBot.Map.CharacterSheet.PortraitOf(data, "MOB_CH_TIGER", 5));
    }

    [Fact]
    public void Berserk_monster_types()
    {
        Assert.True(ProtectionManager.IsBerserkType(MobType.Giant, "Giant"));
        Assert.True(ProtectionManager.IsBerserkType(MobType.PartyChampion, "Party"));
        Assert.True(ProtectionManager.IsBerserkType(MobType.PartyChampion, "Champion"));
        Assert.True(ProtectionManager.IsBerserkType(MobType.Unique2, "Unique"));
        Assert.False(ProtectionManager.IsBerserkType(MobType.General, "Giant"));
    }

    [Fact]
    public void A_scroll_is_not_used_while_a_buff_like_it_is_on()
    {
        static NexBot.Data.SkillInfo Skill(uint id, string name, uint group, string effect) =>
            new(id, name, name, group, name, 0, 3600000, 0, 0, false, 0, $"3|1667396966|1685418593|3600000|{effect}|100|0", "", default, default);
        var abs = Skill(3110, "SKILL_MALL_DAMAGE_ABS_20P_SCROLL_01", 311, "1868849522");
        var speed = Skill(31201, "SKILL_ETC_E090930_SPEED_UP_SUPER_SCROLL_01", 363, "1752396901");
        var mallSpeed = Skill(5411, "SKILL_MALL_MOVE_SPEED_UP_100_01", 374, "1752396901");
        var skills = new[] { abs, speed, mallSpeed }.ToDictionary(x => x.ServerName);

        Assert.Equal(abs, ProtectionCatalog.ScrollSkill(n => skills.GetValueOrDefault(n), "ITEM_MALL_DAMAGE_ABS_20P_SCROLL"));
        Assert.Equal(speed, ProtectionCatalog.ScrollSkill(n => skills.GetValueOrDefault(n), "ITEM_ETC_E090930_SPEED_UP_SUPER_SCROLL"));
        Assert.Null(ProtectionCatalog.ScrollSkill(n => skills.GetValueOrDefault(n), "ITEM_ETC_UNKNOWN"));

        Assert.Null(ProtectionCatalog.SameBuff([], speed));
        Assert.NotNull(ProtectionCatalog.SameBuff([new NexBot.Game.Buff(speed)], speed)); // the same buff
        Assert.NotNull(ProtectionCatalog.SameBuff([new NexBot.Game.Buff(mallSpeed)], speed)); // another speed buff
        Assert.Null(ProtectionCatalog.SameBuff([new NexBot.Game.Buff(abs)], speed)); // not alike
    }

    private static Mob MobOf(uint uid, string server, string name, MobType type, double x) =>
        new(new ModelInfo(uid, server, name, 1, 2, 1, 100)) { UniqueId = uid, MobType = type, Position = new Position(x, 0) };

    [Fact]
    public void Monster_types_and_berserk_on_several_types()
    {
        Assert.True(MonsterPreferences.IsType(MobType.PartyGiant, "Giant"));
        Assert.True(MonsterPreferences.IsType(MobType.PartyGiant, "Party"));
        Assert.True(MonsterPreferences.IsType(MobType.Titan, "Titan"));
        Assert.True(MonsterPreferences.IsType(MobType.Strong, "Strong"));
        Assert.False(MonsterPreferences.IsType(MobType.Event, "General"));
        Assert.True(ProtectionManager.IsBerserkType(MobType.Unique, "Giant,Unique"));
        Assert.False(ProtectionManager.IsBerserkType(MobType.Champion, "Giant,Unique"));
        Assert.Equal("Party Champion", MonsterPreferences.TypeName(MobType.PartyChampion));
    }

    [Fact]
    public void Monster_preferences_rank_by_their_place_in_the_list()
    {
        var tiger = MobOf(1, "MOB_CH_TIGER", "Tiger", MobType.General, 50);
        var giant = MobOf(2, "MOB_CH_MANGNYANG", "Mangyang", MobType.Giant, 90);
        var near = MobOf(3, "MOB_CH_BEAR", "Bear", MobType.General, 5);
        var prefs = new List<string> { "Giant", "MOB_CH_TIGER" };
        var here = new Position(0, 0);

        Assert.Equal(0, MonsterPreferences.Rank(prefs, giant));
        Assert.Equal(1, MonsterPreferences.Rank(prefs, tiger));
        Assert.Equal(int.MaxValue, MonsterPreferences.Rank(prefs, near));
        Assert.Same(giant, MonsterPreferences.Choose(prefs, [tiger, giant, near], here));
        Assert.Same(near, MonsterPreferences.Choose([], [tiger, giant, near], here)); // no list: the nearest
        Assert.Same(giant, MonsterPreferences.BetterThan(prefs, tiger, [tiger, giant, near], here));
        Assert.Null(MonsterPreferences.BetterThan(prefs, giant, [tiger, giant, near], here));
        Assert.True(MonsterPreferences.Matches("tig", tiger)); // older lists: part of a name
    }
}
