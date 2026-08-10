using Tempest.Domain.Load;

namespace Tempest.UnitTests.Load;

public sealed class VirtualUserStageTests
{
    [Fact]
    public void Constant_holds_the_same_vus_for_the_whole_stage()
    {
        VirtualUserStage stage = VirtualUserStage.Constant(10, TimeSpan.FromSeconds(5));

        Assert.Equal(10d, stage.VusAt(0d));
        Assert.Equal(10d, stage.VusAt(2.5d));
        Assert.Equal(10d, stage.VusAt(5d));
        Assert.True(stage.IsFlat);
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(2.5d, 25d)]
    [InlineData(5d, 50d)]
    [InlineData(10d, 100d)]
    public void Ramp_interpolates_the_vus_count_linearly(double elapsedSeconds, double expectedVus)
    {
        VirtualUserStage stage = VirtualUserStage.Ramp(0, 100, TimeSpan.FromSeconds(10));

        Assert.Equal(expectedVus, stage.VusAt(elapsedSeconds), 1e-9);
        Assert.False(stage.IsFlat);
    }

    [Fact]
    public void VusAt_clamps_outside_the_stage_bounds()
    {
        VirtualUserStage stage = VirtualUserStage.Ramp(0, 100, TimeSpan.FromSeconds(10));

        Assert.Equal(0d, stage.VusAt(-5d));
        Assert.Equal(100d, stage.VusAt(20d));
    }

    [Fact]
    public void A_negative_vus_count_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VirtualUserStage.Ramp(-1, 10, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VirtualUserStage.Ramp(10, -1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_negative_or_zero_duration_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VirtualUserStage.Constant(10, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => VirtualUserStage.Constant(10, TimeSpan.FromSeconds(-1)));
    }
}