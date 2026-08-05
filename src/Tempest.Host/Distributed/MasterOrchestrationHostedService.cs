using System.Net.Http.Json;
using Tempest.Domain.Metrics;
using Tempest.Host.Configuration;

namespace Tempest.Host.Distributed;

/// <summary>
/// Deroule le tir distribue : attend les workers, leur distribue un profil reduit, les
/// synchronise sur un depart commun (prepare puis start, plutot qu'un depart immediat), et
/// fusionne leurs rapports une fois tous rentres.
/// <para>
/// Agregation uniquement en fin de tir (choix de portee) : le maitre n'expose un rapport
/// combine qu'une fois tous les workers termines. Chaque worker garde son propre
/// <c>/report/live</c> pendant le tir pour un suivi individuel.
/// </para>
/// </summary>
internal sealed class MasterOrchestrationHostedService(
    MasterCoordinator coordinator,
    MasterOptions masterOptions,
    TempestHostOptions tempestOptions,
    IHttpClientFactory httpClientFactory,
    IHostApplicationLifetime lifetime,
    ILogger<MasterOrchestrationHostedService> logger) : BackgroundService
{
    private const int EXIT_CODE_SUCCESS = 0;
    private const int EXIT_CODE_FAILURE = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "En attente de {Expected} worker(s), fenetre d'enregistrement de {Timeout}s.",
                masterOptions.ExpectedWorkers,
                masterOptions.RegistrationTimeoutSeconds);
        }

        IReadOnlyList<string> workers = await coordinator
            .WaitForRegistrationsAsync(masterOptions.ExpectedWorkers, TimeSpan.FromSeconds(masterOptions.RegistrationTimeoutSeconds), stoppingToken)
            .ConfigureAwait(false);

        if (workers.Count == 0)
        {
            logger.LogError("Aucun worker enregistre : impossible de distribuer le tir.");
            ExitIfConfigured(passed: false);
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("{Count} worker(s) enregistre(s) : {Workers}", workers.Count, string.Join(", ", workers));
        }

        WorkerPrepareRequest prepareRequest = BuildPrepareRequest(workers.Count);
        HttpClient client = httpClientFactory.CreateClient();

        await Task.WhenAll(workers.Select(worker => PrepareAsync(client, worker, prepareRequest, stoppingToken))).ConfigureAwait(false);
        logger.LogInformation("Tous les workers sont prets.");

        DateTime startedAt = DateTime.UtcNow;
        await Task.WhenAll(workers.Select(worker => StartAsync(client, worker, stoppingToken))).ConfigureAwait(false);
        logger.LogInformation("Depart envoye a tous les workers.");

        IReadOnlyList<WorkerReport> reports = await coordinator.WaitForReportsAsync(workers.Count, CancellationToken.None).ConfigureAwait(false);
        TimeSpan duration = DateTime.UtcNow - startedAt;

        LoadTestReport report = ClusterReportAggregator.Merge(reports, duration);
        coordinator.FinalReport = report;

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("{Report}", report.ToTable());
        }

        if (!report.IsTrustworthy)
        {
            logger.LogWarning(
                "{Count} mesures ont ete perdues sur l'ensemble du cluster : les centiles ci-dessus sous-estiment le rapport reel.",
                report.MetricsDropped);
        }

        ThresholdReport thresholds = ThresholdReport.Evaluate(tempestOptions.Thresholds, report);
        coordinator.FinalThresholds = thresholds;

        if (tempestOptions.Thresholds.Count > 0 && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("{Thresholds}", thresholds.ToTable());
        }

        ExitIfConfigured(thresholds.Passed);
    }

    private WorkerPrepareRequest BuildPrepareRequest(int workerCount)
    {
        List<LoadStageOptions> scaledProfile = [.. tempestOptions.Profile
            .Select(stage => new LoadStageOptions
            {
                FromRps = stage.FromRps / workerCount,
                ToRps = stage.ToRps / workerCount,
                DurationSeconds = stage.DurationSeconds,
            })];

        int maxVirtualUsersPerWorker = Math.Max(1, (int)Math.Ceiling(tempestOptions.MaxVirtualUsers / (double)workerCount));

        return new WorkerPrepareRequest
        {
            Profile = scaledProfile,
            Workflow = tempestOptions.Workflow,
            ScenarioFile = tempestOptions.ScenarioFile,
            TargetBaseUrl = tempestOptions.TargetBaseUrl,
            MaxVirtualUsers = maxVirtualUsersPerWorker,
        };
    }

    private static async Task PrepareAsync(HttpClient client, string workerUrl, WorkerPrepareRequest request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client
            .PostAsJsonAsync($"{workerUrl.TrimEnd('/')}/worker/prepare", request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task StartAsync(HttpClient client, string workerUrl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client
            .PostAsync($"{workerUrl.TrimEnd('/')}/worker/start", content: null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private void ExitIfConfigured(bool passed)
    {
        if (tempestOptions.ExitAfterRun)
        {
            Environment.ExitCode = passed ? EXIT_CODE_SUCCESS : EXIT_CODE_FAILURE;
            lifetime.StopApplication();
        }
    }
}