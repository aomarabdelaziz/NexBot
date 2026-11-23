using NexBot.Configuration;
using Xunit;

namespace NexBot.Tests;

public sealed class AlchemySwitchTests
{
    [Fact]
    public void OldSettingsWithoutSwitchesUseThePlusValue()
    {
        var s = new AlchemySettings { AstralAt = 3, SteadyAt = 0, StopAfterAttempts = 20 };
        Assert.True(s.AstralOn);
        Assert.False(s.SteadyOn);
        Assert.True(s.StopAfterOn);
    }

    [Fact]
    public void SwitchesOverrideThePlusValue()
    {
        var s = new AlchemySettings { UseAstral = false, AstralAt = 3, UseImmortal = true, ImmortalAt = 0, UseStopAfterAttempts = true, StopAfterAttempts = 0 };
        Assert.False(s.AstralOn);
        Assert.True(s.ImmortalOn);
        Assert.False(s.StopAfterOn);
    }
}
