using System.Text.Json;
using System.Text.Json.Serialization;
using Tempest.Application.Execution;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;
using Tempest.Host.Configuration;
using Tempest.Infrastructure.Metrics;

namespace Tempest.Host;

/// <summary>
/// Deroule le tir de charge une fois l'hote demarre, puis journalise le bilan, le rapport
/// final et le verdict des seuils configures.
/// <para>
/// L'hote reste actif apres la fin du tir par defaut : c'est ce qui permet a Prometheus de
/// continuer a scruter <c>/metrics</c> et de recuperer le dernier etat cumule, plutot que de
/// perdre le resultat des que le processus qui l'a produit se termine. Le scenario CI/CD —
/// <see cref="TempestHostOptions.ExitAfterRun"/> — est un choix explicite, pas le defaut.
/// </para>
/// </summary>
internal sealed class LoadTestHostedService(
    TargetRpsLoadEngine engine,
    MetricsProcessor metricsProcessor,
    TempestHostOptions options,
    IWorkflow workflow,
    IHostApplicationLifetime lifetime,
    ILogger<LoadTestHostedService> logger) : BackgroundService
{
    private const int EXIT_CODE_SUCCESS = 0;
    private const int EXIT_CODE_THRESHOLD_FAILURE = 1;

    // Meme convention que ConfigureHttpJsonOptions dans Program.cs (voir son commentaire) : ce
    // fichier est aussi lu par un humain ou par Tempest.Compare, jamais uniquement par du code.
    private static readonly JsonSerializerOptions _reportJsonOptions = CreateReportJsonOptions();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        metricsProcessor.Start();

        logger.LogInformation("Demarrage du tir de charge.");

        LoadTestSummary summary;
        try
        {
            summary = await engine.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            // Toujours drainer, meme si le tir a ete annule : sans ce drainage, la queue du
            // tir — les mesures les plus recentes — resterait perdue dans le canal.
            await metricsProcessor.StopAsync().ConfigureAwait(false);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Tir termine. {Summary}", summary);
        }

        LoadTestReport report = metricsProcessor.Aggregator.Snapshot(StatisticsScope.Cumulative) with { Tags = workflow.Tags };
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("{Report}", report.ToTable());
        }

        if (!report.IsTrustworthy)
        {
            logger.LogWarning(
                "{Count} mesures ont ete perdues : les centiles ci-dessus sous-estiment le rapport reel.",
                report.MetricsDropped);
        }

        ThresholdReport thresholds = ThresholdReport.Evaluate(options.Thresholds, report);
        if (options.Thresholds.Count > 0 && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("{Thresholds}", thresholds.ToTable());
        }

        if (options.ReportHtmlPath is { } htmlPath)
        {
            // CancellationToken.None, deliberement : le tir vient de se terminer normalement,
            // l'annulation de l'hote ne doit pas pouvoir tronquer l'ecriture du rapport final.
            await File.WriteAllTextAsync(htmlPath, report.ToHtml(thresholds), CancellationToken.None).ConfigureAwait(false);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Rapport HTML ecrit : {Path}", htmlPath);
            }
        }

        if (options.ReportJsonPath is { } jsonPath)
        {
            string json = JsonSerializer.Serialize(report, _reportJsonOptions);
            await File.WriteAllTextAsync(jsonPath, json, CancellationToken.None).ConfigureAwait(false);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Rapport JSON ecrit : {Path}", jsonPath);
            }
        }

        if (options.ExitAfterRun)
        {
            Environment.ExitCode = thresholds.Passed ? EXIT_CODE_SUCCESS : EXIT_CODE_THRESHOLD_FAILURE;
            lifetime.StopApplication();
        }
    }

    private static JsonSerializerOptions CreateReportJsonOptions()
    {
        JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        return jsonOptions;
    }
}