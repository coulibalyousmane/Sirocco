using Tempest.Domain.Load;

namespace Tempest.UnitTests.Load;

/// <summary>
/// <see cref="LoadProfile.ScheduledSecondsFor(double)"/> doit etre la reciproque exacte de
/// <see cref="LoadProfile.PlannedRequestsUpTo(double)"/> : c'est cette propriete qui garantit
/// que l'instant grave dans un jeton correspond bien a l'echeancier annonce.
/// </summary>
public sealed class LoadProfileInversionTests
{
    [Fact]
    public void Constant_profile_spaces_requests_evenly()
    {
        LoadProfile profile = LoadProfile.Constant(100d, TimeSpan.FromSeconds(10));

        Assert.Equal(0d, profile.ScheduledSecondsFor(0), 1e-9);
        Assert.Equal(0.01d, profile.ScheduledSecondsFor(1), 1e-9);
        Assert.Equal(1d, profile.ScheduledSecondsFor(100), 1e-9);
        Assert.Equal(9.99d, profile.ScheduledSecondsFor(999), 1e-9);
        Assert.Equal(1000, profile.PlannedRequestCount);
    }

    [Fact]
    public void Rising_ramp_front_loads_the_gaps()
    {
        // 0 -> 1000 RPS en 10 s : la moitie du volume est emise dans les 3 derniers dixiemes.
        LoadProfile profile = new([LoadStage.Ramp(0d, 1000d, TimeSpan.FromSeconds(10))]);

        Assert.Equal(5d, profile.ScheduledSecondsFor(1250), 1e-6);
        Assert.Equal(10d, profile.ScheduledSecondsFor(5000), 1e-6);

        // Les intervalles se resserrent a mesure que le debit monte.
        double earlyGap = profile.ScheduledSecondsFor(101) - profile.ScheduledSecondsFor(100);
        double lateGap = profile.ScheduledSecondsFor(4001) - profile.ScheduledSecondsFor(4000);
        Assert.True(lateGap < earlyGap, $"L'ecart devrait se reduire : {lateGap} vs {earlyGap}");
    }

    [Fact]
    public void Falling_ramp_is_inverted_without_numeric_blow_up()
    {
        // 1000 -> 0 RPS en 10 s. La formule quadratique naive perd toute precision ici.
        LoadProfile profile = new([LoadStage.Ramp(1000d, 0d, TimeSpan.FromSeconds(10))]);

        Assert.Equal(2.9289321881d, profile.ScheduledSecondsFor(2500), 1e-8);
        Assert.Equal(10d, profile.ScheduledSecondsFor(5000), 1e-6);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(37d)]
    [InlineData(2500d)]
    [InlineData(9999d)]
    public void Inversion_round_trips_on_a_multi_stage_profile(double requests)
    {
        var profile = LoadProfile.Create()
            .RampTo(1000d, TimeSpan.FromSeconds(10))
            .Sustain(TimeSpan.FromSeconds(10))
            .RampTo(200d, TimeSpan.FromSeconds(10))
            .Build();

        double instant = profile.ScheduledSecondsFor(requests);

        Assert.Equal(requests, profile.PlannedRequestsUpTo(instant), 1e-6);
    }

    [Fact]
    public void Scheduled_instants_are_strictly_increasing_across_boundaries()
    {
        var profile = LoadProfile.Create()
            .RampTo(500d, TimeSpan.FromSeconds(2))
            .Sustain(TimeSpan.FromSeconds(2))
            .RampTo(100d, TimeSpan.FromSeconds(2))
            .Build();

        double previous = -1d;
        for (long i = 0L; i < profile.PlannedRequestCount; i++)
        {
            double instant = profile.ScheduledSecondsFor(i);
            Assert.True(instant > previous, $"Jeton {i} programme a {instant}, apres {previous}.");
            Assert.InRange(instant, 0d, profile.TotalDurationSeconds);
            previous = instant;
        }
    }

    [Fact]
    public void A_profile_starting_at_zero_rps_delays_the_very_first_request()
    {
        // "Attendre 5 s, puis frapper a 100 RPS" : le premier jeton ne part pas a t = 0.
        LoadProfile profile = new([
            LoadStage.Constant(0d, TimeSpan.FromSeconds(5)),
            LoadStage.Constant(100d, TimeSpan.FromSeconds(5)),
        ]);

        Assert.Equal(500, profile.PlannedRequestCount);
        Assert.Equal(5d, profile.ScheduledSecondsFor(0), 1e-9);
        Assert.Equal(5.01d, profile.ScheduledSecondsFor(1), 1e-9);
        Assert.Equal(9.99d, profile.ScheduledSecondsFor(499), 1e-9);
    }

    [Fact]
    public void A_trailing_zero_rps_stage_does_not_swallow_the_last_request()
    {
        LoadProfile profile = new([
            LoadStage.Constant(100d, TimeSpan.FromSeconds(5)),
            LoadStage.Constant(0d, TimeSpan.FromSeconds(5)),
        ]);

        Assert.Equal(500, profile.PlannedRequestCount);
        Assert.Equal(4.99d, profile.ScheduledSecondsFor(499), 1e-9);
    }

    [Fact]
    public void Every_planned_request_falls_inside_the_test_window()
    {
        LoadProfile profile = LoadProfile.RampUpSustainDown(
            peakRps: 300d,
            rampUp: TimeSpan.FromSeconds(2),
            sustain: TimeSpan.FromSeconds(3),
            rampDown: TimeSpan.FromSeconds(2));

        double last = profile.ScheduledSecondsFor(profile.PlannedRequestCount - 1);

        Assert.True(last < profile.TotalDurationSeconds);
        Assert.Equal(1500, profile.PlannedRequestCount); // 300 (montee) + 900 (plateau) + 300 (descente)
    }
}