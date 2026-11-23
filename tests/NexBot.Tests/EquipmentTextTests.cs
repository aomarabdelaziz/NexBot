using NexBot.Data;
using NexBot.Map;
using Xunit;

namespace NexBot.Tests;

public class EquipmentTextTests
{
    /// <summary>ITEM_CH_M_CLOTHES_01_BA_A as itemdata has it (columns 14, 158, then 58-117).</summary>
    private static ItemInfo Clothes()
    {
        var d = new Dictionary<int, string>
        {
            [14] = "0", [158] = "9", [58] = "1", [61] = "1", [63] = "39.0", [64] = "48.0", [65] = "2.0", [66] = "2.5", [67] = "0.3",
            [68] = "5.0", [69] = "7.0", [76] = "4.3", [77] = "5.2", [78] = "0.7", [82] = "42.0", [83] = "51.0", [84] = "89.0", [85] = "109.0",
        };
        string equip = string.Join('|', EquipmentText.Columns.Select(c => d.GetValueOrDefault(c, "0")));
        return new ItemInfo(899, "ITEM_CH_M_CLOTHES_01_BA_A", "Cloth Tunic", 1, 1, 3, 0, 0, "", 1, 1, Equip: equip);
    }

    [Fact]
    public void Header_and_requirements_read_like_the_game()
    {
        var info = Clothes();
        Assert.Equal(["Sort of item: Garment", "Mounting part: Chest", "Degree: 1 degrees"], EquipmentText.Header(info));
        Assert.Equal(["Required level 1", "Male", "Chinese", "Max No. of magic options: 9Unit", "Able to use Advanced elixir."],
            EquipmentText.Requirements(info));
    }

    [Fact]
    public void White_stats_use_the_low_end_at_zero_and_the_high_end_at_full()
    {
        var info = Clothes();
        Assert.Equal(
        [
            "Phy. def. pwr. 2.0 (+0%)", "Mag. def. pwr. 4.3 (+0%)", "Durability 30/39 (+0%)", "Parry rate 5 (+0%)",
            "Phy. reinforce 4.2 % (+0%)", "Mag. reinforce 8.9 % (+0%)",
        ], EquipmentText.Stats(info, plus: 0, variance: 0, durability: 30));

        // All six whites at 31 (100 %), +2: defense gets 2 x its increase.
        ulong full = 0;
        for (int i = 0; i < 6; i++)
            full |= 31UL << (i * 5);
        var stats = EquipmentText.Stats(info, plus: 2, variance: full, durability: null);
        Assert.Equal("Phy. def. pwr. 3.1 (+100%)", stats[0]);
        Assert.Equal("Durability 48 (+100%)", stats[2]);
        Assert.Equal("Mag. reinforce 10.9 % (+100%)", stats[5]);
    }

    [Fact]
    public void Items_without_stats_in_the_database_have_no_lines()
    {
        var old = Clothes() with { Equip = "" };
        Assert.Empty(EquipmentText.Stats(old, 0, 0, null));
        Assert.Equal(["Sort of item: Garment", "Mounting part: Chest"], EquipmentText.Header(old));
    }
}
