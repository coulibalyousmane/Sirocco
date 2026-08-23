using System.Text.Json;
using System.Text.Json.Serialization;
using Sirocco.Application.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Host.Configuration;
using Sirocco.Host.Execution;

namespace Sirocco.Host;

/// <summary>
/// Deroule un tir a scenarios concurrents une fois l'hote demarre, puis journalise le bilan
/// combine — meme role que <see cref="LoadTestHostedService"/> pour le tir simple, mais un seul
/// <see cref="MultiScenarioReport"/> pour N scenarios plutot qu'un <c>LoadTestReport</c> pour un
/// seul, et aucun <c>MetricsProcessor</c> a piloter ici : chaque scenario a le sien, demarre et
/// arrete par <see cref="MultiScenarioRunner"/> lui-meme.
/// </summary>
internal sealed class MultiScenarioLoadTestHostedService(
    IReadOnlyList<ScenarioRunSpec> specs,
    MultiScenarioReportHolder holder,
    SiroccoHostOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<MultiScenarioLoadTestHostedService> logger) : BackgroundService
{
    private const int EXIT_CODE_SUCCESS = 0;
    private const int EXIT_CODE_THRESHOLD_FAILURE = 1;

    private static readonly JsonSerializerOptions _reportJsonOptions = CreateReportJsonOptions();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Demarrage du tir de charge ({Count} scenarios concurrents).", specs.Count);
        }

        MultiScenarioReport report = await MultiScenarioRunner.RunAsync(specs, stoppingToken).ConfigureAwait(false);
        holder.Report = report;

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Tir termine.\n{Report}", report.ToTable());
        }

        if (!report.IsTrustworthy)
        {
            logger.LogWarning(
                "Au moins un scenario a perdu des mesures : les centiles correspondants sous-estiment le rapport reel.");
        }

        if (options.ReportHtmlPath is { } htmlPath)
        {
            // CancellationToken.None, deliberement : le tir vient de se terminer normalement,
            // l'annulation de l'hote ne doit pas pouvoir tronquer l'ecriture du rapport final.
            await File.WriteAllTextAsync(htmlPath, report.ToHtml(), CancellationToken.None).ConfigureAwait(false);
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
            Environment.ExitCode = report.ThresholdsPassed ? EXIT_CODE_SUCCESS : EXIT_CODE_THRESHOLD_FAILURE;
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