using Tempest.Domain.Load;

namespace Tempest.UnitTests.Load;

public sealed class LoadProfileTests
{
    private const double TOLERANCE = 1e-9;

    [Fact]
    public void Constant_holds_the_same_rps_for_the_whole_stage()
    {
        LoadProfile profile = LoadProfile.Constant(100d, TimeSpan.FromSeconds(10));

        Assert.Equal(100d, profile.RpsAt(0d), TOLERANCE);
        Assert.Equal(100d, profile.RpsAt(5d), TOLERANCE);
        Assert.Equal(100d, profile.RpsAt(9.999d), TOLERANCE);
        Assert.Equal(1000d, profile.TotalPlannedRequests, TOLERANCE);
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(2.5d, 250d)]
    [InlineData(5d, 500d)]
    [InlineData(10d, 1000d)]
    public void Ramp_interpolates_the_rps_linearly(double elapsedSeconds, double expectedRps)
    {
        LoadProfile profile = new([LoadStage.Ramp(0d, 1000d, TimeSpan.FromSeconds(10))]);

        // A la borne finale exacte le profil est termine : le debit retombe a zero.
        double expected = elapsedSeconds >= 10d ? 0d : expectedRps;

        Assert.Equal(expected, profile.RpsAt(elapsedSeconds), 1e-6);
    }

    [Fact]
    public void Ramp_integrates_to_the_area_of_the_triangle()
    {
        LoadProfile profile = new([LoadStage.Ramp(0d, 1000d, TimeSpan.FromSeconds(10))]);

        // Aire du triangle : la moitie du rectangle 1000 x 10.
        Assert.Equal(5000d, profile.TotalPlannedRequests, 1e-6);

        // A mi-parcours on n'a emis qu'un quart du total (aire d'un triangle demi-echelle).
        Assert.Equal(1250d, profile.PlannedRequestsUpTo(5d), 1e-6);
    }

    [Fact]
    public void Builder_chains_stages_without_discontinuity()
    {
        var profile = LoadProfile.Create()
            .RampTo(1000d, TimeSpan.FromSeconds(10))
            .Sustain(TimeSpan.FromSeconds(10))
            .RampTo(0d, TimeSpan.FromSeconds(10))
            .Build();

        Assert.Equal(3, profile.Stages.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), profile.TotalDuration);
        Assert.Equal(1000d, profile.PeakRps, TOLERANCE);

        // Le debit final d'un palier est le debit initial du suivant.
        Assert.Equal(profile.Stages[0].ToRps, profile.Stages[1].FromRps, TOLERANCE);
        Assert.Equal(profile.Stages[1].ToRps, profile.Stages[2].FromRps, TOLERANCE);

        // 5 000 (montee) + 10 000 (plateau) + 5 000 (descente).
        Assert.Equal(20_000d, profile.TotalPlannedRequests, 1e-6);
    }

    [Fact]
    public void PlannedRequests_is_continuous_across_stage_boundaries()
    {
        var profile = LoadProfile.Create()
            .RampTo(1000d, TimeSpan.FromSeconds(10))
            .Sustain(TimeSpan.FromSeconds(10))
            .Build();

        double justBefore = profile.PlannedRequestsUpTo(9.999d);
        double atBoundary = profile.PlannedRequestsUpTo(10d);
        double justAfter = profile.PlannedRequestsUpTo(10.001d);

        Assert.Equal(5000d, atBoundary, 1e-6);
        Assert.True(justBefore < atBoundary && atBoundary < justAfter);
    }

    [Fact]
    public void PlannedRequests_never_decreases()
    {
        var profile = LoadProfile.Create()
            .RampTo(500d, TimeSpan.FromSeconds(3))
            .Sustain(TimeSpan.FromSeconds(2))
            .RampTo(50d, TimeSpan.FromSeconds(4))
            .Build();

        double previous = -1d;
        for (double t = -1d; t <= 12d; t += 0.05d)
        {
            double current = profile.PlannedRequestsUpTo(t);
            Assert.True(current >= previous, $"Regression de l'echeancier a t={t}: {current} < {previous}");
            previous = current;
        }
    }

    [Fact]
    public void Outside_the_test_window_the_schedule_is_clamped()
    {
        LoadProfile profile = LoadProfile.Constant(100d, TimeSpan.FromSeconds(10));

        // Avant le depart : aucune requete due, mais le debit affiche est deja le debit initial.
        Assert.Equal(0d, profile.PlannedRequestsUpTo(-5d), TOLERANCE);
        Assert.Equal(100d, profile.RpsAt(-5d), TOLERANCE);

        Assert.Equal(1000d, profile.PlannedRequestsUpTo(999d), TOLERANCE);
        Assert.Equal(0d, profile.RpsAt(999d), TOLERANCE);
        Assert.True(profile.IsCompleted(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void RampUpSustainDown_builds_the_three_expected_stages()
    {
        LoadProfile profile = LoadProfile.RampUpSustainDown(
            peakRps: 2000d,
            rampUp: TimeSpan.FromSeconds(20),
            sustain: TimeSpan.FromSeconds(60),
            rampDown: TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(100), profile.TotalDuration);
        Assert.Equal(2000d, profile.RpsAt(50d), 1e-6);
        Assert.Equal(1000d, profile.RpsAt(10d), 1e-6);
        Assert.Equal(1000d, profile.RpsAt(90d), 1e-6);
    }

    [Fact]
    public void A_stage_rejects_a_non_positive_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadStage.Constant(100d, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadStage.Constant(100d, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void A_stage_rejects_a_negative_rps()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadStage.Constant(-1d, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadStage.Ramp(0d, double.NaN, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void An_empty_profile_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new LoadProfile([]));
        Assert.Throws<InvalidOperationException>(() => LoadProfile.Create().Build());
    }
}