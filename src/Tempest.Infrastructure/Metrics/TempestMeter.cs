using System.Diagnostics.Metrics;
using Tempest.Application.Metrics;
using Tempest.Domain.Metrics;

namespace Tempest.Infrastructure.Metrics;

/// <summary>
/// Publie les statistiques du tir sous forme d'instruments <see cref="Meter"/>.
/// <para>
/// Tempest ne depend d'aucun exportateur : il alimente les instruments standards de la BCL,
/// et OpenTelemetry — ou n'importe quel autre collecteur — vient les ecouter. C'est ce qui
/// permet de brancher Prometheus, OTLP ou un simple <c>MeterListener</c> de test sans que
/// le moteur ait a le savoir.
/// </para>
/// <para>
/// Les jauges lisent la fenetre <b>glissante</b>, les compteurs lisent le <b>cumul</b> : un
/// compteur OpenTelemetry doit etre monotone, une jauge doit refleter l'instant present.
/// </para>
/// </summary>
public sealed class TempestMeter : IDisposable
{
    /// <summary>Nom du <see cref="Meter"/> a ecouter cote collecteur.</summary>
    public const string METER_NAME = "Tempest";

    private const string LATENCY_INSTRUMENT = "tempest.latency";
    private const string REQUESTS_INSTRUMENT = "tempest.requests";
    private const string BYTES_INSTRUMENT = "tempest.bytes.received";
    private const string SCHEDULING_DELAY_INSTRUMENT = "tempest.scheduling.delay.max";
    private const string DROPPED_METRICS_INSTRUMENT = "tempest.metrics.dropped";

    private const string TAG_STEP = "step";
    private const string TAG_QUANTILE = "quantile";
    private const string TAG_KIND = "kind";
    private const string TAG_OUTCOME = "outcome";

    /// <summary>Latence corrigee du <i>coordinated omission</i>.</summary>
    private const string KIND_RESPONSE = "response";

    /// <summary>Temps de service brut, tel que le mesurerait un outil naif.</summary>
    private const string KIND_SERVICE = "service";

    private const string UNIT_MILLISECONDS = "ms";
    private const string UNIT_REQUESTS = "{request}";
    private const string UNIT_BYTES = "By";

    private static readonly (string Label, Func<LatencySnapshot, double> Selector)[] _quantiles =
    [
        ("0.5", static snapshot => snapshot.P50Milliseconds),
        ("0.95", static snapshot => snapshot.P95Milliseconds),
        ("0.99", static snapshot => snapshot.P99Milliseconds),
        ("0.999", static snapshot => snapshot.P999Milliseconds),
        ("1.0", static snapshot => snapshot.MaxMilliseconds),
    ];

    private readonly MetricsAggregator _aggregator;
    private readonly Meter _meter;

    /// <summary>Cree les instruments et les rattache a l'agregateur.</summary>
    /// <param name="aggregator">Source des statistiques.</param>
    /// <param name="meterFactory">Fabrique de <see cref="Meter"/> ; une instance autonome si omise.</param>
    public TempestMeter(MetricsAggregator aggregator, IMeterFactory? meterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(aggregator);

        _aggregator = aggregator;
        _meter = meterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

        _meter.CreateObservableGauge(
            LATENCY_INSTRUMENT,
            ObserveLatency,
            UNIT_MILLISECONDS,
            "Centiles de latence sur la fenetre glissante, par etape et par nature de mesure.");

        _meter.CreateObservableCounter(
            REQUESTS_INSTRUMENT,
            ObserveRequests,
            UNIT_REQUESTS,
            "Nombre cumule de mesures, par etape et par issue.");

        _meter.CreateObservableCounter(
            BYTES_INSTRUMENT,
            ObserveBytesReceived,
            UNIT_BYTES,
            "Volume cumule recu, par etape.");

        _meter.CreateObservableGauge(
            SCHEDULING_DELAY_INSTRUMENT,
            ObserveSchedulingDelay,
            UNIT_MILLISECONDS,
            "Dette d'ordonnancement maximale : au-dela de quelques millisecondes, l'injecteur sature.");

        _meter.CreateObservableCounter(
            DROPPED_METRICS_INSTRUMENT,
            ObserveDroppedMetrics,
            UNIT_REQUESTS,
            "Mesures perdues faute de place : toute valeur non nulle invalide les centiles.");
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<double>> ObserveLatency()
    {
        LoadTestReport report = _aggregator.Snapshot(StatisticsScope.Sliding);
        List<Measurement<double>> measurements = new(report.Steps.Count * _quantiles.Length * 2);

        foreach (StepStatistics step in report.Steps)
        {
            if (step.Count == 0L)
            {
                continue;
            }

            AddQuantiles(measurements, step.Name, KIND_RESPONSE, step.Response);
            AddQuantiles(measurements, step.Name, KIND_SERVICE, step.Service);
        }

        return measurements;
    }

    private static void AddQuantiles(
        List<Measurement<double>> measurements,
        string stepName,
        string kind,
        LatencySnapshot snapshot)
    {
        foreach ((string label, Func<LatencySnapshot, double> selector) in _quantiles)
        {
            measurements.Add(new Measurement<double>(
                selector(snapshot),
                new KeyValuePair<string, object?>(TAG_STEP, stepName),
                new KeyValuePair<string, object?>(TAG_KIND, kind),
                new KeyValuePair<string, object?>(TAG_QUANTILE, label)));
        }
    }

    private IEnumerable<Measurement<long>> ObserveRequests()
    {
        LoadTestReport report = _aggregator.Snapshot(StatisticsScope.Cumulative);
        List<Measurement<long>> measurements = [];

        foreach (StepStatistics step in report.Steps)
        {
            for (int outcome = 0; outcome < step.CountByOutcome.Count; outcome++)
            {
                long count = step.CountByOutcome[outcome];
                if (count == 0L)
                {
                    continue;
                }

                measurements.Add(new Measurement<long>(
                    count,
                    new KeyValuePair<string, object?>(TAG_STEP, step.Name),
                    new KeyValuePair<string, object?>(TAG_OUTCOME, ((RequestOutcome)outcome).ToString())));
            }
        }

        return measurements;
    }

    private IEnumerable<Measurement<long>> ObserveBytesReceived()
    {
        LoadTestReport report = _aggregator.Snapshot(StatisticsScope.Cumulative);

        return [.. report.Steps
            .Where(static step => step.BytesReceived > 0L)
            .Select(static step => new Measurement<long>(
                step.BytesReceived,
                new KeyValuePair<string, object?>(TAG_STEP, step.Name)))];
    }

    private IEnumerable<Measurement<double>> ObserveSchedulingDelay()
    {
        LoadTestReport report = _aggregator.Snapshot(StatisticsScope.Cumulative);

        return [.. report.Steps
            .Where(static step => step.Count > 0L)
            .Select(static step => new Measurement<double>(
                step.MaxSchedulingDelayMicroseconds / 1_000d,
                new KeyValuePair<string, object?>(TAG_STEP, step.Name)))];
    }

    private IEnumerable<Measurement<long>> ObserveDroppedMetrics() =>
        [new Measurement<long>(_aggregator.MetricsDropped)];
}