using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Data;
using NexBot.Game;
using NexBot.Network;
using Xunit;

namespace NexBot.Tests;

public class TrainingTests
{
    private static SkillInfo Info(uint id, string server, string param) =>
        new(id, server, server, id, "", 0, 0, 500, 10, true, 1, param, "", WeaponType.None, WeaponType.None, 1);

    [Fact]
    public void Special_skills_are_found_by_their_codes()
    {
        var heal = Info(1, "SKILL_CH_WATER_HEAL_A_01", $"3|{AttackPlan.HealCode}|50");
        var resurrect = Info(2, "SKILL_CH_WATER_RESURRECTION_A_01", $"3|{AttackPlan.HealCode}|{AttackPlan.ResurrectCode}");
        var cure = Info(3, "SKILL_CH_WATER_CURE_A_01", $"3|{AttackPlan.CureCode}");
        var ghost = Info(4, "SKILL_CH_LIGHTNING_GYEONGGONG_A_01", $"0|{AttackPlan.GhostWalkCode}|200");
        Assert.True(AttackPlan.IsHeal(heal));
        Assert.False(AttackPlan.IsHeal(resurrect)); // resurrect skills carry the heal code too
        Assert.True(AttackPlan.IsResurrect(resurrect));
        Assert.True(AttackPlan.IsCure(cure));
        Assert.True(AttackPlan.IsTeleport(ghost));
        Assert.False(AttackPlan.IsTeleport(heal));
        Assert.True(AttackPlan.IsDevilSpirit(Info(5, "SKILL_EU_NASRUN_A_01", "3")));
    }

    [Fact]
    public void Healing_picks_self_then_group_then_the_lowest_member()
    {
        var s = new AttackSettings { HealSelfBelow = 50, GroupHealBelow = 60, HealMemberBelow = 70 };
        Assert.Equal(AttackPlan.HealKind.Self, AttackPlan.WhoToHeal(s, 40, [("A", 10)]).Kind);
        Assert.Equal(AttackPlan.HealKind.Group, AttackPlan.WhoToHeal(s, 60, [("A", 40), ("B", 50)]).Kind);
        var member = AttackPlan.WhoToHeal(s, 100, [("A", 60), ("B", 30), ("Dead", 0)]);
        Assert.Equal((AttackPlan.HealKind.Member, "B"), member);
        Assert.Equal(AttackPlan.HealKind.None, AttackPlan.WhoToHeal(s, 100, [("A", 90)]).Kind);
    }

    [Fact]
    public void The_area_for_a_level_is_the_highest_condition_under_it()
    {
        TrainingCondition[] list = [new() { Level = 1, Area = "Jangan" }, new() { Level = 40, Area = "Donwhang" }, new() { Level = 60, Area = "" }];
        Assert.Null(AttackPlan.AreaForLevel(list, 0));
        Assert.Equal("Jangan", AttackPlan.AreaForLevel(list, 39));
        Assert.Equal("Donwhang", AttackPlan.AreaForLevel(list, 80));
    }

    [Fact]
    public void Luring_stops_over_the_limits()
    {
        var s = new AttackSettings { LureStopIfDead = 1, LureStopIfMonsters = 5, LureStopIfPartyAway = true };
        Assert.Null(AttackPlan.LureStop(s, 1, 9, 9, 5, false));
        Assert.NotNull(AttackPlan.LureStop(s, 2, 0, 0, 0, false));
        Assert.NotNull(AttackPlan.LureStop(s, 0, 0, 0, 6, false));
        Assert.NotNull(AttackPlan.LureStop(s, 0, 0, 0, 0, true));
    }

    [Fact]
    public void Random_walk_points_stay_in_the_area()
    {
        var circle = new TrainingAreaSettings { X = 1000, Y = 2000, Radius = 50 };
        var square = new TrainingAreaSettings { UsePolygon = true, Polygon = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)] };
        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var (x, y) = AttackPlan.RandomPoint(circle, random);
            Assert.True(circle.Contains(x, y));
            (x, y) = AttackPlan.RandomPoint(square, random);
            Assert.True(square.Contains(x, y));
        }
    }

    [Fact]
    public void A_skill_cast_on_the_ground_has_the_position()
    {
        var p = ClientPackets.CastAt(5, new Position(25000, 10, 0, 20)).ToArray();
        Assert.Equal([1, 4, 5, 0, 0, 0, 2], p[..7]);
        Assert.Equal(25000, BitConverter.ToUInt16(p, 7));
        Assert.Equal(10f, BitConverter.ToSingle(p, 9));
        Assert.Equal(0f, BitConverter.ToSingle(p, 13));
        Assert.Equal(20f, BitConverter.ToSingle(p, 17));
    }

    [Fact]
    public void Old_settings_get_one_area_and_enabling_one_copies_it()
    {
        var s = new BotSettings();
        s.Training.X = 6436;
        s.Training.Y = 826;
        s.Town.TrainingScript = "jangan.txt";
        s.EnsureTrainingAreas();
        var first = Assert.Single(s.TrainingSetup.Areas);
        Assert.Equal(("Training", 6436.0, "jangan.txt"), (first.Name, first.X, first.Script));
        Assert.Equal("Training", s.TrainingSetup.Enabled);

        var other = new TrainingAreaSettings { Name = "Donwhang", X = 3500, Y = 2000, Radius = 80, Script = "dw.txt", PickRadius = 30 };
        s.TrainingSetup.Areas.Add(other);
        Assert.True(s.EnableTrainingArea("donwhang"));
        Assert.Equal((3500.0, 80, 30), (s.Training.X, s.Training.Radius, s.Training.PickRadius));
        Assert.Equal("dw.txt", s.Town.TrainingScript);
        Assert.False(s.EnableTrainingArea("nowhere"));

        s.Training.X = 3600; // moved on the map
        s.SaveTrainingToList();
        Assert.Equal((3600.0, "Donwhang", "dw.txt"), (other.X, other.Name, other.Script));
        Assert.Equal(6436, first.X);
    }
}
