using Tempest.Application.Execution;
using Tempest.Application.Metrics;
using Tempest.Domain.Metrics;
using Tempest.Infrastructure.Metrics;

namespace Tempest.Host.Execution;

/// <summary>
/// Fait tourner N scenarios concurrents dans le meme processus, chacun sur son propre
/// <see cref="TargetRpsLoadEngine"/>, sa propre chaine de mesure et son propre registre d'etapes —
/// jamais assembles via le conteneur d'injection de dependances, qui ne sait enregistrer qu'un
/// singleton de chaque type. Deux scenarios qui declarent tous les deux une etape "login"
/// produisent donc deux <see cref="ScenarioReport"/> distincts, jamais une seule ligne fusionnee :
/// c'est l'isolement complet de la chaine (pas seulement du nom de l'etape) qui le garantit.
/// <para>
/// Reste hors de <c>Tempest.Application</c> parce qu'il a besoin de <see cref="MetricsProcessor"/>
/// (<c>Tempest.Infrastructure</c>), qui depend deja de <c>Tempest.Application</c> — l'inverse
/// casserait la Clean Architecture du projet (voir le README).
/// </para>
/// </summary>
public static class MultiScenarioRunner
{
    /// <summary>
    /// Lance tous les scenarios en parallele et attend qu'ils se terminent tous, meme si l'un
    /// d'eux echoue ou est annule avant les autres — un scenario plus long ne doit pas etre
    /// tronque par la fin anticipee d'un autre.
    /// </summary>
    public static async Task<MultiScenarioReport> RunAsync(IReadOnlyList<ScenarioRunSpec> specs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specs);

        ScenarioReport[] reports = await Task.WhenAll(specs.Select(spec => RunOneAsync(spec, cancellationToken)))
            .ConfigureAwait(false);

        return new MultiScenarioReport { Scenarios = reports };
    }

    private static async Task<ScenarioReport> RunOneAsync(ScenarioRunSpec spec, CancellationToken cancellationToken)
    {
        spec.Options.Validate();

        StepRegistry steps = new();
        CustomMetricRegistry customMetrics = new();
        ChannelMetricSink sink = new(ChannelMetricSink.DEFAULT_CAPACITY);
        ChannelCustomMetricSink customMetricSink = new(ChannelCustomMetricSink.DEFAULT_CAPACITY);
        CustomMetricsAggregator customMetricsAggregator = new(customMetrics);
        MetricsAggregator aggregator = new(steps, null, customMetricsAggregator);
        MetricsProcessor processor = new(sink, aggregator, customMetricSink, customMetricsAggregator);

        TargetRpsLoadEngine engine = new(
            spec.Scheduler, spec.Workflow, spec.HttpClient, sink, spec.Options, steps, customMetrics, customMetricSink);

        processor.Start();
        try
        {
            await engine.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Meme discipline que LoadTestHostedService : toujours drainer, meme apres une
            // annulation, sous peine de perdre la queue du tir de ce scenario.
            await processor.StopAsync().ConfigureAwait(false);
        }

        LoadTestReport report = processor.Aggregator.Snapshot(StatisticsScope.Cumulative) with
        {
            Tags = spec.Workflow.Tags,
            ClosedModel = spec.IsClosedModel,
        };

        ThresholdReport thresholds = ThresholdReport.Evaluate(spec.Thresholds, report);

        return new ScenarioReport { Name = spec.Name, Report = report, Thresholds = thresholds };
    }
}