using System.Diagnostics.Metrics;
using Tempest.Application.Metrics;
using Tempest.Domain.Metrics;
using Tempest.Domain.Timing;
using Tempest.Infrastructure.Metrics;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.Metrics;

/// <summary>
/// Verifie le pont vers <see cref="Meter"/> avec un simple <see cref="MeterListener"/> :
/// si un ecouteur de la BCL voit les mesures, OpenTelemetre et Prometheus les verront aussi.
/// </summary>
public sealed class TempestMeterTests
{
    private const string LOGIN_STEP = "login";
    private const string LATENCY_INSTRUMENT = "tempest.latency";
    private const string REQUESTS_INSTRUMENT = "tempest.requests";
    private const string DROPPED_INSTRUMENT = "tempest.metrics.dropped";

    private sealed record Observation(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags);

    private static (MetricsAggregator Aggregator, StepId Login) CreateAggregator()
    {
        StepRegistry registry = new();
        registry.Register(WellKnownSteps.ITERATION);
        StepId login = registry.Register(LOGIN_STEP);
        registry.Seal();

        return (new MetricsAggregator(registry), login);
    }

    private static List<Observation> Collect(TempestMeter meter)
    {
        List<Observation> observations = [];

        using MeterListener listener = new()
        {
            InstrumentPublished = (instrument, activeListener) =>
            {
                if (instrument.Meter.Name == TempestMeter.METER_NAME)
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
        using TempestMeter meter = new(aggregator);

        long now = TempestClock.Now;
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
        using TempestMeter meter = new(aggregator);

        long now = TempestClock.Now;
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
        (MetricsAggregator aggregator, _) = CreateAggregator();
        aggregator.MetricsDropped = 42L;

        using TempestMeter meter = new(aggregator);

        Observation dropped = Collect(meter).Single(o => o.Instrument == DROPPED_INSTRUMENT);

        Assert.Equal(42d, dropped.Value);
    }

    [Fact]
    public void A_step_without_measurements_publishes_no_latency_at_all()
    {
        (MetricsAggregator aggregator, _) = CreateAggregator();
        using TempestMeter meter = new(aggregator);

        // Publier des centiles a zero pour une etape jamais executee polluerait les
        // tableaux de bord avec des courbes plates indiscernables d'une cible instantanee.
        Assert.DoesNotContain(Collect(meter), o => o.Instrument == LATENCY_INSTRUMENT);
    }
}