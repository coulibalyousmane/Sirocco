using Tempest.Application.Execution;
using Tempest.Application.Metrics;
using Tempest.Domain.Metrics;
using Tempest.Domain.Timing;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.Metrics;

public sealed class TimeSeriesRecorderTests
{
    private static StepRegistry CreateRegistry()
    {
        StepRegistry registry = new();
        registry.Register(WellKnownSteps.ITERATION);
        registry.Seal();

        return registry;
    }

    [Fact]
    public void A_null_aggregator_or_gauge_is_rejected()
    {
        MetricsAggregator aggregator = new(CreateRegistry());
        ActiveVirtualUserGauge gauge = new();

        Assert.Throws<ArgumentNullException>(() => new TimeSeriesRecorder(null!, gauge, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => new TimeSeriesRecorder(aggregator, null!, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_non_positive_interval_is_rejected()
    {
        MetricsAggregator aggregator = new(CreateRegistry());
        ActiveVirtualUserGauge gauge = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeSeriesRecorder(aggregator, gauge, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeSeriesRecorder(aggregator, gauge, TimeSpan.FromSeconds(-1)));
    }

    /// <summary>
    /// Un tir plus court que l'intervalle de releve ne doit jamais produire une serie vide :
    /// c'est ce qui garantit qu'un rapport porte toujours au moins un point de trajectoire.
    /// </summary>
    [Fact]
    public async Task Cancelling_before_the_first_interval_still_yields_one_sample()
    {
        MetricsAggregator aggregator = new(CreateRegistry());
        TimeSeriesRecorder recorder = new(aggregator, new ActiveVirtualUserGauge(), TimeSpan.FromSeconds(30));

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(10));
        await recorder.RunAsync(cts.Token);

        Assert.Single(recorder.Samples);
    }

    [Fact]
    public async Task Repeated_intervals_produce_multiple_samples_with_increasing_elapsed_time()
    {
        MetricsAggregator aggregator = new(CreateRegistry());
        TimeSeriesRecorder recorder = new(aggregator, new ActiveVirtualUserGauge(), TimeSpan.FromMilliseconds(20));

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(90));
        await recorder.RunAsync(cts.Token);

        // ~90ms a 20ms d'intervalle : autour de 4 points, large marge pour la gigue du test.
        Assert.True(recorder.Samples.Count >= 2, $"Trop peu de points : {recorder.Samples.Count}");

        for (int i = 1; i < recorder.Samples.Count; i++)
        {
            Assert.True(
                recorder.Samples[i].ElapsedSeconds > recorder.Samples[i - 1].ElapsedSeconds,
                "Le temps ecoule doit strictement augmenter d'un point au suivant.");
        }
    }

    [Fact]
    public async Task Each_sample_reflects_the_aggregator_and_gauge_at_the_time_it_was_taken()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry);
        registry.TryGetId(WellKnownSteps.ITERATION, out StepId iteration);
        ActiveVirtualUserGauge gauge = new();
        gauge.Increment();
        gauge.Increment();
        gauge.Increment();

        long now = TempestClock.Now;
        for (int i = 0; i < 5; i++)
        {
            aggregator.Record(MetricFactory.Create(iteration, now, responseMilliseconds: 42d, serviceMilliseconds: 42d));
        }

        TimeSeriesRecorder recorder = new(aggregator, gauge, TimeSpan.FromSeconds(30));
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(10));
        await recorder.RunAsync(cts.Token);

        TimeSeriesSample sample = Assert.Single(recorder.Samples);
        Assert.Equal(3, sample.ActiveVirtualUsers);
        Assert.Equal(0d, sample.ErrorRate, 1e-9);
        Assert.True(sample.ResponseP50Milliseconds > 0d);
    }
}