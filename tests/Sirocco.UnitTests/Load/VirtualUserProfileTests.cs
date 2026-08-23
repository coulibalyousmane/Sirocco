using Sirocco.Domain.Load;

namespace Sirocco.UnitTests.Load;

public sealed class VirtualUserProfileTests
{
    [Fact]
    public void An_empty_stage_list_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new VirtualUserProfile([]));
    }

    [Fact]
    public void A_single_ramp_stage_rounds_to_the_nearest_integer()
    {
        VirtualUserProfile profile = new([VirtualUserStage.Ramp(0, 10, TimeSpan.FromSeconds(10))]);

        Assert.Equal(0, profile.VusAt(0d));
        Assert.Equal(3, profile.VusAt(2.5d));
        Assert.Equal(5, profile.VusAt(5d));
        Assert.Equal(10, profile.VusAt(10d));
        Assert.Equal(TimeSpan.FromSeconds(10), profile.TotalDuration);
        Assert.Equal(10, profile.PeakVus);
    }

    [Fact]
    public void VusAt_before_the_start_returns_the_first_stage_starting_value()
    {
        VirtualUserProfile profile = new([VirtualUserStage.Ramp(5, 20, TimeSpan.FromSeconds(10))]);

        Assert.Equal(5, profile.VusAt(-1d));
    }

    [Fact]
    public void VusAt_past_the_end_returns_the_last_stage_ending_value()
    {
        VirtualUserProfile profile = new([VirtualUserStage.Ramp(5, 20, TimeSpan.FromSeconds(10))]);

        Assert.Equal(20, profile.VusAt(999d));
    }

    [Fact]
    public void Up_sustain_down_transitions_without_a_discontinuity_at_stage_boundaries()
    {
        VirtualUserProfile profile = new([
            VirtualUserStage.Ramp(0, 50, TimeSpan.FromSeconds(10)),
            VirtualUserStage.Constant(50, TimeSpan.FromSeconds(20)),
            VirtualUserStage.Ramp(50, 0, TimeSpan.FromSeconds(10)),
        ]);

        Assert.Equal(3, profile.Stages.Count);
        Assert.Equal(TimeSpan.FromSeconds(40), profile.TotalDuration);
        Assert.Equal(50, profile.PeakVus);

        // Le palier de montee et le plateau se rejoignent a la meme valeur.
        Assert.Equal(50, profile.VusAt(10d));
        Assert.Equal(50, profile.VusAt(20d));
        Assert.Equal(50, profile.VusAt(30d));

        // La descente rejoint zero exactement a la fin du profil.
        Assert.Equal(25, profile.VusAt(35d));
        Assert.Equal(0, profile.VusAt(40d));
    }
}