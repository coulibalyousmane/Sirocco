using System.Diagnostics.Metrics;
using Sirocco.Application.Metrics;
using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;
using Sirocco.Infrastructure.Metrics;
using Sirocco.UnitTests.TestDoubles;

namespace Sirocco.UnitTests.Metrics;

/// <summary>
/// Verifie le pont vers <see cref="Meter"/> avec un simple <see cref="MeterListener"/> :
/// si un ecouteur de la BCL voit les mesures, OpenTelemetre et Prometheus les verront aussi.
/// </summary>
public sealed class SiroccoMeterTests
{
    private const string LOGIN_STEP = "login";
    private const string LATENCY_INSTRUMENT = "sirocco.latency";
    private const string REQUESTS_INSTRUMENT = "sirocco.requests";
    private const string DROPPED_INSTRUMENT = "sirocco.metrics.dropped";

    private sealed record Observation(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags);

    private static (MetricsAggregator Aggregator, StepId Login) CreateAggregator()
    {
        StepRegistry registry = new();
        registry.Register(WellKnownSteps.ITERATION);
        StepId login = registry.Register(LOGIN_STEP);
        registry.Seal();

        return (new MetricsAggregator(registry), login);
    }

    private static List<Observation> Collect(SiroccoMeter meter)
    {
        List<Observation> observations = [];

        using MeterListener listener = new()
        {
            InstrumentPublished = (instrument, activeListener) =>
            {
                if (instrument.Meter.Name == SiroccoMeter.METER_NAME)
                {
                    activeListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            observations.Add(new Observation(instrument.Name, value, ToDictionary(tags))));

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            observations.Add(new Observation(instrument.Name, value, ToDictionary(tags))));

        listener.Start();
        listener.RecordObservableInstruments();

        GC.KeepAlive(meter);
        return observations;
    }

    private static Dictionary<string, string> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            result[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        return result;
    }

    [Fact]
    public void Latency_percentiles_are_published_for_both_kinds_of_measurement()
    {
        (MetricsAggregator aggregator, StepId login) = CreateAggregator();
        using SiroccoMeter meter = new(aggregator);

        long now = SiroccoClock.Now;
        for (int i = 0; i < 100; i++)
        {
            aggregator.Record(MetricFactory.Create(login, now, responseMilliseconds: 500d, serviceMilliseconds: 20d));
        }

        List<Observation> observations = Collect(meter);
        List<Observation> latency = [.. observations.Where(o => o.Instrument == LATENCY_INSTRUMENT && o.Tags["step"] == LOGIN_STEP)];

        Assert.NotEmpty(latency);

        Observation correctedP99 = latency.Single(o => o.Tags["kind"] == "response" && o.Tags["quantile"] == "0.99");
        Observation rawP99 = latency.Single(o => o.Tags["kind"] == "service" && o.Tags["quantile"] == "0.99");

        // Les deux courbes sont exposees : c'est leur ecart qui revele la saturation.
        Assert.InRange(correctedP99.Value, 500d, 504d);
        Assert.InRange(rawP99.Value, 20d, 20.5d);
    }

    [Fact]
    public void Request_counts_are_published_per_step_and_per_outcome()
    {
        (MetricsAggregator aggregator, StepId login) = CreateAggregator();
        using SiroccoMeter meter = new(aggregator);

        long now = SiroccoClock.Now;
        for (int i = 0; i < 7; i++)
        {
            aggregator.Record(MetricFactory.Create(login, now, 10d, 10d));
        }

        aggregator.Record(MetricFactory.Create(login, now, 10d, 10d, RequestOutcome.HttpError));

        List<Observation> requests = [.. Collect(meter).Where(o => o.Instrument == REQUESTS_INSTRUMENT && o.Tags["step"] == LOGIN_STEP)];

        Assert.Equal(7d, requests.Single(o => o.Tags["outcome"] == nameof(RequestOutcome.Success)).Value);
        Assert.Equal(1d, requests.Single(o => o.Tags["outcome"] == nameof(RequestOutcome.HttpError)).Value);
    }

    [Fact]
    public void Lost_measurements_are_exposed_so_a_dashboard_can_alert_on_them()
    {
        // Sentinelle deliberement improbable : DROPPED_INSTRUMENT ne porte aucun tag qui
        // distinguerait cette mesure de celle d'un autre test publiant sur le meme nom de
        // Meter en parallele (meme convention que SiroccoMetricsServiceCollectionExtensionsTests).
        const long SENTINEL_DROPPED_COUNT = 837_465_918L;

        (MetricsAggregator aggregator, _) = CreateAggregator();
        aggregator.MetricsDropped = SENTINEL_DROPPED_COUNT;

        using SiroccoMeter meter = new(aggregator);

        Observation dropped = Collect(meter)
            .Single(o => o.Instrument == DROPPED_INSTRUMENT && o.Value == SENTINEL_DROPPED_COUNT);

        Assert.Equal(SENTINEL_DROPPED_COUNT, dropped.Value);
    }

    [Fact]
    public void A_step_without_measurements_publishes_no_latency_at_all()
    {
        (MetricsAggregator aggregator, _) = CreateAggregator();
        using SiroccoMeter meter = new(aggregator);

        // Publier des centiles a zero pour une etape jamais executee polluerait les
        // tableaux de bord avec des courbes plates indiscernables d'une cible instantanee.
        Assert.DoesNotContain(Collect(meter), o => o.Instrument == LATENCY_INSTRUMENT);
    }

    private static MetricsAggregator CreateAggregatorWithCustomMetrics(CustomMetricsAggregator customMetrics)
    {
        StepRegistry steps = new();
        steps.Register(WellKnownSteps.ITERATION);
        steps.Seal();

        return new MetricsAggregator(steps, customMetrics: customMetrics);
    }

    [Fact]
    public void Custom_counter_is_published_as_its_cumulative_sum()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId ordersTotal = registry.Register("orders_total", CustomMetricKind.Counter);
        registry.Seal();

        CustomMetricsAggregator customMetrics = new(registry);
        customMetrics.Record(new CustomMetricResult(ordersTotal, 1d));
        customMetrics.Record(new CustomMetricResult(ordersTotal, 2d));

        using SiroccoMeter meter = new(CreateAggregatorWithCustomMetrics(customMetrics));

        Observation counter = Collect(meter).Single(o => o.Instrument == "sirocco.custom.counter");
        Assert.Equal(3d, counter.Value);
        Assert.Equal("orders_total", counter.Tags["metric"]);
    }

    [Fact]
    public void Custom_gauge_is_published_as_its_last_value()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId activeCarts = registry.Register("active_carts", CustomMetricKind.Gauge);
        registry.Seal();

        CustomMetricsAggregator customMetrics = new(registry);
        customMetrics.Record(new CustomMetricResult(activeCarts, 5d));
        customMetrics.Record(new CustomMetricResult(activeCarts, 8d));

        using SiroccoMeter meter = new(CreateAggregatorWithCustomMetrics(customMetrics));

        Observation gauge = Collect(meter).Single(o => o.Instrument == "sirocco.custom.gauge");
        Assert.Equal(8d, gauge.Value);
    }

    [Fact]
    public void Custom_rate_is_published_as_a_fraction()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId cacheHitRate = registry.Register("cache_hit_rate", CustomMetricKind.Rate);
        registry.Seal();

        CustomMetricsAggregator customMetrics = new(registry);
        customMetrics.Record(new CustomMetricResult(cacheHitRate, 1d));
        customMetrics.Record(new CustomMetricResult(cacheHitRate, 1d));
        customMetrics.Record(new CustomMetricResult(cacheHitRate, 0d));
        customMetrics.Record(new CustomMetricResult(cacheHitRate, 0d));

        using SiroccoMeter meter = new(CreateAggregatorWithCustomMetrics(customMetrics));

        Observation rate = Collect(meter).Single(o => o.Instrument == "sirocco.custom.rate");
        Assert.Equal(0.5d, rate.Value);
    }

    [Fact]
    public void Custom_trend_publishes_min_mean_and_max_as_separate_measurements()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId orderValue = registry.Register("order_value", CustomMetricKind.Trend);
        registry.Seal();

        CustomMetricsAggregator customMetrics = new(registry);
        customMetrics.Record(new CustomMetricResult(orderValue, 10d));
        customMetrics.Record(new CustomMetricResult(orderValue, 20d));
        customMetrics.Record(new CustomMetricResult(orderValue, 30d));

        using SiroccoMeter meter = new(CreateAggregatorWithCustomMetrics(customMetrics));

        List<Observation> trend = [.. Collect(meter).Where(o => o.Instrument == "sirocco.custom.trend")];
        Assert.Equal(10d, trend.Single(o => o.Tags["stat"] == "min").Value);
        Assert.Equal(20d, trend.Single(o => o.Tags["stat"] == "mean").Value);
        Assert.Equal(30d, trend.Single(o => o.Tags["stat"] == "max").Value);
    }
}