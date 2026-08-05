using Tempest.Application.Metrics;
using Tempest.Domain.Metrics;
using Tempest.Domain.Timing;
using Tempest.Infrastructure.Metrics;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.Metrics;

public sealed class MetricsProcessorTests
{
    private const string LOGIN_STEP = "login";

    private static StepRegistry CreateRegistry()
    {
        StepRegistry registry = new();
        registry.Register(WellKnownSteps.ITERATION);
        registry.Register(LOGIN_STEP);
        registry.Seal();

        return registry;
    }

    /// <summary>
    /// Ecrit comme le fait le moteur : une seule tentative, sans reessai. Reessayer
    /// gonflerait <see cref="ChannelMetricSink.DroppedMetrics"/>, qui compte les ecritures
    /// rejetees et non les mesures reellement perdues.
    /// </summary>
    private static void Write(ChannelMetricSink sink, in MetricResult metric) => sink.TryWrite(in metric);

    /// <summary>
    /// Le drainage complet n'est pas un detail : couper a l'arret perdrait la queue du tir,
    /// c'est-a-dire les mesures prises quand la cible etait la plus sollicitee.
    /// </summary>
    [Fact]
    public async Task Every_measurement_reaches_the_aggregator_including_the_tail()
    {
        const int MEASUREMENT_COUNT = 20_000;

        StepRegistry registry = CreateRegistry();
        registry.TryGetId(LOGIN_STEP, out StepId login);

        // Capacite superieure au volume : l'ecrivain va plus vite que le consommateur, une
        // partie substantielle des mesures est donc encore en file au moment de l'arret.
        // C'est exactement ce que StopAsync doit finir d'agreger.
        ChannelMetricSink sink = new(capacity: 1 << 16);
        MetricsAggregator aggregator = new(registry);

        await using MetricsProcessor processor = new(sink, aggregator);
        processor.Start();

        long now = TempestClock.Now;
        for (int i = 0; i < MEASUREMENT_COUNT; i++)
        {
            Write(sink, MetricFactory.Create(login, now, 10d, 10d));
        }

        await processor.StopAsync();

        Assert.Equal(MEASUREMENT_COUNT, aggregator.SnapshotStep(login, StatisticsScope.Cumulative, now).Count);
        Assert.Equal(0L, aggregator.MetricsDropped);
    }

    [Fact]
    public async Task Outcomes_survive_the_round_trip_through_the_channel()
    {
        StepRegistry registry = CreateRegistry();
        registry.TryGetId(LOGIN_STEP, out StepId login);

        ChannelMetricSink sink = new(capacity: 64);
        MetricsAggregator aggregator = new(registry);

        await using MetricsProcessor processor = new(sink, aggregator);
        processor.Start();

        long now = TempestClock.Now;
        Write(sink, MetricFactory.Create(login, now, 10d, 10d));
        Write(sink, MetricFactory.Create(login, now, 900d, 20d, RequestOutcome.Timeout));
        Write(sink, MetricFactory.Create(login, now, 50d, 0d, RequestOutcome.Dropped));

        await processor.StopAsync();

        StepStatistics statistics = aggregator.SnapshotStep(login, StatisticsScope.Cumulative, now);

        Assert.Equal(3L, statistics.Count);
        Assert.Equal(1L, statistics.SuccessCount);
        Assert.Equal(1L, statistics.DroppedCount);
        Assert.Equal(1L, statistics.CountByOutcome[(int)RequestOutcome.Timeout]);
    }

    /// <summary>
    /// Un debordement du canal doit remonter jusqu'au rapport : des centiles calcules sur un
    /// echantillon tronque sans le dire seraient pires que pas de centiles du tout.
    /// </summary>
    [Fact]
    public async Task Measurements_lost_before_the_processor_started_are_reported()
    {
        StepRegistry registry = CreateRegistry();
        registry.TryGetId(LOGIN_STEP, out StepId login);

        ChannelMetricSink sink = new(capacity: 4);
        MetricsAggregator aggregator = new(registry);

        long now = TempestClock.Now;
        int accepted = 0;
        for (int i = 0; i < 20; i++)
        {
            if (sink.TryWrite(MetricFactory.Create(login, now, 10d, 10d)))
            {
                accepted++;
            }
        }

        await using MetricsProcessor processor = new(sink, aggregator);
        processor.Start();
        await processor.StopAsync();

        Assert.Equal(4, accepted);
        Assert.Equal(16L, aggregator.MetricsDropped);
        Assert.False(aggregator.Snapshot(StatisticsScope.Cumulative, now).IsTrustworthy);
    }

    [Fact]
    public async Task Stopping_without_any_measurement_is_harmless()
    {
        ChannelMetricSink sink = new();
        MetricsAggregator aggregator = new(CreateRegistry());

        await using MetricsProcessor processor = new(sink, aggregator);
        processor.Start();
        await processor.StopAsync();

        Assert.Equal(0L, aggregator.Snapshot(StatisticsScope.Cumulative).Steps.Sum(step => step.Count));
    }

    [Fact]
    public async Task Starting_twice_is_rejected()
    {
        ChannelMetricSink sink = new();
        MetricsAggregator aggregator = new(CreateRegistry());

        await using MetricsProcessor processor = new(sink, aggregator);
        processor.Start();

        Assert.Throws<InvalidOperationException>(processor.Start);

        await processor.StopAsync();
    }

    [Fact]
    public async Task Disposing_without_stopping_does_not_hang()
    {
        ChannelMetricSink sink = new();
        MetricsAggregator aggregator = new(CreateRegistry());

        MetricsProcessor processor = new(sink, aggregator);
        processor.Start();

        // Aucun appel a StopAsync : le canal reste ouvert, la boucle est en attente.
        // DisposeAsync doit l'interrompre par annulation, pas rester bloque.
        await processor.DisposeAsync();

        Assert.False(processor.IsRunning);
    }
}