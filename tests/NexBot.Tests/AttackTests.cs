using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Game.Entities;
using NexBot.Network;
using Xunit;

namespace NexBot.Tests;

public class AttackTests
{
    private static SkillInfo Info(uint id, string server, string param, uint mana = 10, uint cooldown = 0, uint duration = 0,
        WeaponType first = WeaponType.None, WeaponType second = WeaponType.None, byte level = 1) =>
        new(id, server, server, id, "", cooldown, duration, 500, mana, true, 1, param, "", first, second, level);

    private static Mob MobOf(uint uid, MobType type, double x, string name = "Mob") =>
        new(new ModelInfo(uid, "MOB_" + uid, name, 1, 2, 1, 100)) { UniqueId = uid, MobType = type, Position = new Position(x, 0) };

    [Fact]
    public void Skill_kinds_come_from_the_params()
    {
        Assert.Equal(SkillKind.Attack, AttackPlan.KindOf(Info(3, "SKILL_CH_SWORD_SMASH_A_01", "0|6386804|5")));
        Assert.Equal(SkillKind.Buff, AttackPlan.KindOf(Info(27, "SKILL_CH_SWORD_SHIELD_A_01", "3|1685418593|15000", duration: 15000)));
        Assert.Equal(SkillKind.Imbue, AttackPlan.KindOf(Info(90, "SKILL_CH_COLD_GIGONGTA_A_01", "3|1685418593|5000", duration: 5000)));
        Assert.Equal(SkillKind.Passive, AttackPlan.KindOf(Info(69, "SKILL_CH_SPEAR_PASSIVE_A_01", "4|6844521|102", mana: 0)));
        Assert.Equal(SkillKind.Passive, AttackPlan.KindOf(Info(8600, "SKILL_EU_WARRIOR_SHIELDP_ATTACK_A_01", "4|1936028790", mana: 0)));
    }

    [Fact]
    public void A_skill_line_has_no_level()
    {
        Assert.Equal("SKILL_CH_SWORD_SMASH_A", AttackPlan.LineOf("SKILL_CH_SWORD_SMASH_A_01"));
        Assert.Equal("SKILL_CH_COLD_GIGONGTA_A", AttackPlan.LineOf("SKILL_CH_COLD_GIGONGTA_A_09"));
        Assert.Equal("SKILL_CH_SWORD_SMASH_A", AttackPlan.LineOf("SKILL_CH_SWORD_SMASH_A"));
    }

    [Fact]
    public void Monster_types_map_to_the_skill_lists()
    {
        Assert.Equal("General", AttackPlan.TypeOf(MobType.General));
        Assert.Equal("Giant", AttackPlan.TypeOf(MobType.Giant));
        Assert.Equal("Party Giant", AttackPlan.TypeOf((MobType)0x14));
        Assert.Equal("Party Champion", AttackPlan.TypeOf((MobType)0x11));
        Assert.Equal("Unique", AttackPlan.TypeOf(MobType.Unique2));
        Assert.Equal("Event", AttackPlan.TypeOf(MobType.Event));
    }

    [Fact]
    public void A_type_without_skills_uses_the_lower_types_or_general()
    {
        var s = new AttackSettings();
        s.SkillsOf("General").Add("SKILL_A");
        s.SkillsOf("Champion").Add("SKILL_B");
        Assert.Equal("Champion", AttackPlan.SkillsFor(s, "Giant").Type);
        Assert.Equal("Champion", AttackPlan.SkillsFor(s, "Titan").Type);
        Assert.Equal("General", AttackPlan.SkillsFor(s, "Party Champion").Type);
        s.UseLowerSkills = false;
        Assert.Equal("General", AttackPlan.SkillsFor(s, "Giant").Type);
        Assert.Equal("Champion", AttackPlan.SkillsFor(s, "Champion").Type);
        Assert.Empty(AttackPlan.SkillsFor(new AttackSettings(), "Giant").Skills);
    }

    [Fact]
    public void Skills_are_cast_in_order_skipping_those_not_ready()
    {
        var smash = new Skill(Info(3, "SKILL_A_01", "0", mana: 10));
        var cut = new Skill(Info(4, "SKILL_B_02", "0", mana: 500));
        var stab = new Skill(Info(5, "SKILL_C_01", "0", mana: 10, cooldown: 60_000));
        var learned = new[] { smash, cut, stab };
        string[] lines = ["SKILL_A", "SKILL_B", "SKILL_C"];
        int next = 0;
        Assert.Same(smash, AttackPlan.NextSkill(lines, learned, 100, 0, true, ref next));
        // SKILL_B needs 500 MP: skipped.
        Assert.Same(stab, AttackPlan.NextSkill(lines, learned, 100, 0, true, ref next));
        stab.StartCooldown();
        Assert.Same(smash, AttackPlan.NextSkill(lines, learned, 100, 0, true, ref next));
        // Not in order: always the first ready one.
        Assert.Same(smash, AttackPlan.NextSkill(lines, learned, 100, 0, false, ref next));
        Assert.Null(AttackPlan.NextSkill(lines, learned, 5, 0, true, ref next));
    }

    [Fact]
    public void A_skill_needs_its_weapon()
    {
        var sword = Info(3, "SKILL_CH_SWORD_SMASH_A_01", "0", first: WeaponType.Sword, second: WeaponType.Blade);
        Assert.True(AttackPlan.WeaponFits(sword, (byte)WeaponType.Blade));
        Assert.False(AttackPlan.WeaponFits(sword, (byte)WeaponType.Spear));
        Assert.True(AttackPlan.WeaponFits(Info(27, "SKILL_X_01", "3"), (byte)WeaponType.Spear));
    }

    [Fact]
    public void Monsters_attacking_you_come_first_then_preferences_then_the_nearest()
    {
        var near = MobOf(1, MobType.General, 5);
        var giant = MobOf(2, MobType.Giant, 30);
        var champion = MobOf(3, MobType.Champion, 20);
        var all = new[] { near, giant, champion };
        var s = new AttackSettings();
        var none = new HashSet<uint>();
        Assert.Same(near, AttackPlan.ChooseTarget(s, [], all, new Position(0, 0), none, none));
        Assert.Same(giant, AttackPlan.ChooseTarget(s, ["Giant"], all, new Position(0, 0), none, none));
        Assert.Same(champion, AttackPlan.ChooseTarget(s, ["Giant"], all, new Position(0, 0), new HashSet<uint> { 3 }, none));
        s.AttackLowerFirst = true;
        Assert.Same(champion, AttackPlan.ChooseTarget(s, [], all, new Position(0, 0), new HashSet<uint> { 2, 3 }, none));
        s.ProtectParty = true;
        Assert.Same(giant, AttackPlan.ChooseTarget(s, [], all, new Position(0, 0), none, new HashSet<uint> { 2 }));
    }

    [Fact]
    public void A_buff_is_cast_without_a_target_or_on_yourself()
    {
        var none = ClientPackets.Cast(27);
        Assert.Equal([1, 4, 27, 0, 0, 0, 0], none.ToArray());
        var self = ClientPackets.Cast(27, 0x01020304);
        Assert.Equal([1, 4, 27, 0, 0, 0, 1, 4, 3, 2, 1], self.ToArray());
    }

    [Fact]
    public void The_buff_is_on_when_its_group_is_on()
    {
        var shield = new Skill(Info(27, "SKILL_CH_SWORD_SHIELD_A_01", "3", duration: 15000));
        var who = MobOf(9, MobType.General, 0);
        Assert.False(AttackPlan.IsOn(who, shield));
        var higher = new Buff(Info(28, "SKILL_CH_SWORD_SHIELD_A_02", "3", duration: 15000) with { GroupId = 27 });
        who.Buffs[higher.Key] = higher;
        Assert.True(AttackPlan.IsOn(who, shield));
    }
}
