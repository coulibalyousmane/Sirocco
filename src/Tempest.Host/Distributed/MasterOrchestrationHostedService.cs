using System.Net.Http.Json;
using Tempest.Domain.Metrics;
using Tempest.Host.Configuration;
using Tempest.Scenarios;
using Tempest.Scenarios.Declarative;

namespace Tempest.Host.Distributed;

/// <summary>
/// Deroule le tir distribue : attend les workers, leur distribue un profil reduit, les
/// synchronise sur un depart commun (prepare puis start, plutot qu'un depart immediat),
/// sonde en continu un tableau de bord combine pendant le tir, et fusionne leurs rapports
/// finaux une fois tous rentres.
/// <para>
/// Deux notions de rapport combine, deliberement separees : <see cref="MasterCoordinator.LiveReport"/>
/// (rafraichi en continu par sondage, approximatif par nature) et
/// <see cref="MasterCoordinator.FinalReport"/> (construit une seule fois, a partir des rapports
/// pousses par les workers a la fin de leur tir local — l'un ne remplace pas l'autre).
/// </para>
/// </summary>
internal sealed class MasterOrchestrationHostedService(
    MasterCoordinator coordinator,
    MasterOptions masterOptions,
    TempestHostOptions tempestOptions,
    IConfiguration configuration,
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

        HttpClient client = httpClientFactory.CreateClient(ClusterCertificatePinning.CLUSTER_CLIENT_NAME);
        client.DefaultRequestHeaders.Authorization = ClusterAuthentication.BuildHeader(tempestOptions.ClusterSharedSecret);

        // Le sondage tourne des maintenant, en tache de fond, pendant tout le reste de la
        // sequence (preparation, depart, tir, remontee des rapports finaux) — annule une fois
        // les rapports finaux en main, puisqu'ils remplacent alors avantageusement la derniere
        // valeur sondee.
        using CancellationTokenSource livePollingCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        DateTime livePollingStartedAt = DateTime.UtcNow;
        Task livePolling = PollLiveReportsAsync(client, workers, livePollingStartedAt, livePollingCts.Token);

        WorkerPrepareRequest prepareRequest = BuildPrepareRequest(workers.Count);

        await Task.WhenAll(workers.Select(worker => PrepareAsync(client, worker, prepareRequest, stoppingToken))).ConfigureAwait(false);
        logger.LogInformation("Tous les workers sont prets.");

        DateTime startedAt = DateTime.UtcNow;
        await Task.WhenAll(workers.Select(worker => StartAsync(client, worker, stoppingToken))).ConfigureAwait(false);
        logger.LogInformation("Depart envoye a tous les workers.");

        // CancellationToken.None si aucun plafond absolu n'est configure : seule la detection par
        // heartbeat (MarkDeadIfStale, dans PollLiveReportsAsync) fait alors avancer cette attente
        // au-dela des rapports effectivement recus. Voir MasterOptions.ReportTimeoutSeconds pour
        // le cas residuel qu'elle ne couvre pas (worker vivant, tir local bloque).
        using CancellationTokenSource reportWaitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (masterOptions.ReportTimeoutSeconds is { } reportTimeoutSeconds)
        {
            reportWaitCts.CancelAfter(TimeSpan.FromSeconds(reportTimeoutSeconds));
        }

        IReadOnlyList<WorkerReport> reports;
        try
        {
            reports = await coordinator.WaitForReportsAsync(workers.Count, reportWaitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Plafond absolu atteint (ReportTimeoutSeconds) : on procede avec ce qui est deja en
            // main plutot que d'attendre davantage.
            reports = coordinator.ReportsSoFar;
            logger.LogWarning(
                "Plafond de {Timeout}s atteint pour la remontee des rapports finaux : poursuite avec les {Count} worker(s) deja rentre(s).",
                masterOptions.ReportTimeoutSeconds,
                reports.Count);
        }

        TimeSpan duration = DateTime.UtcNow - startedAt;

        // Ecart entre les workers dispatches et ceux effectivement rentres — pas uniquement
        // coordinator.DeadWorkers (detectes par heartbeat) : un plafond absolu
        // (ReportTimeoutSeconds) peut aussi rendre la main avant que le heartbeat n'ait declare
        // mort un worker toujours vivant mais dont le tir local est bloque.
        HashSet<string> reportedWorkerIds = [.. reports.Select(report => report.WorkerId)];
        IReadOnlyList<string> lostWorkers = [.. workers.Where(worker => !reportedWorkerIds.Contains(worker))];

        await StopLivePollingAsync(livePollingCts, livePolling).ConfigureAwait(false);

        if (reports.Count == 0)
        {
            logger.LogError(
                "Tous les workers dispatches ({Count}) ont ete perdus en cours de tir : aucun rapport a fusionner.",
                workers.Count);
            ExitIfConfigured(passed: false);
            return;
        }

        LoadTestReport report = ClusterReportAggregator.Merge(reports, duration);
        if (lostWorkers.Count > 0)
        {
            report = report with { LostWorkers = lostWorkers };
            logger.LogWarning(
                "{Count} worker(s) perdu(s) en cours de tir : {Workers}. Rapport fusionne a partir des {Reported} restants.",
                lostWorkers.Count,
                string.Join(", ", lostWorkers),
                reports.Count);
        }

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

    /// <summary>
    /// Sonde <c>/worker/report/raw</c> sur chaque worker, en continu, et fusionne les etats
    /// bruts recus pour rafraichir <see cref="MasterCoordinator.LiveReport"/>.
    /// <para>
    /// Au niveau brut (histogrammes), jamais au niveau des centiles deja calcules — la fusion
    /// reste exacte a chaque rafraichissement, meme si l'instant du sondage, lui, est
    /// approximatif. Un worker temporairement injoignable est ignore pour ce cycle plutot que de
    /// faire echouer tout le sondage : le tableau de bord doit rester vivant meme degrade.
    /// </para>
    /// </summary>
    private async Task PollLiveReportsAsync(HttpClient client, IReadOnlyList<string> workers, DateTime startedAt, CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(masterOptions.LivePollIntervalSeconds);
        TimeSpan deadAfter = TimeSpan.FromSeconds(masterOptions.WorkerDeadAfterSeconds);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WorkerReport?[] snapshots = await Task
                    .WhenAll(workers.Select(worker => FetchRawReportAsync(client, worker, cancellationToken)))
                    .ConfigureAwait(false);

                List<WorkerReport> received = [.. snapshots.OfType<WorkerReport>()];
                if (received.Count > 0)
                {
                    coordinator.LiveReport = ClusterReportAggregator.Merge(received, DateTime.UtcNow - startedAt);
                }

                // Meme cadence que le sondage du tableau de bord : pas de minuteur dedie. C'est ce
                // qui permet a WaitForReportsAsync de cesser d'attendre un worker perdu en cours
                // de tir plutot que de rester bloque indefiniment (voir MasterCoordinator).
                IReadOnlyList<string> newlyDead = coordinator.MarkDeadIfStale(deadAfter);
                foreach (string worker in newlyDead)
                {
                    logger.LogWarning(
                        "Worker {WorkerUrl} declare perdu : aucun heartbeat depuis plus de {DeadAfter}s.",
                        worker,
                        masterOptions.WorkerDeadAfterSeconds);
                }

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Arret normal du sondage : les rapports finaux ont pris le relais.
        }
    }

    private async Task<WorkerReport?> FetchRawReportAsync(HttpClient client, string workerUrl, CancellationToken cancellationToken)
    {
        try
        {
            return await client
                .GetFromJsonAsync<WorkerReport>($"{workerUrl.TrimEnd('/')}/worker/report/raw", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            // Pas encore pret (503) ou injoignable un instant : ce cycle de sondage l'ignore,
            // le suivant reessaiera — un tableau de bord vivant tolere un trou plutot que de
            // s'arreter pour un worker en retard.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Sondage live de {WorkerUrl} sans reponse exploitable pour ce cycle.", workerUrl);
            }

            return null;
        }
    }

    private static async Task StopLivePollingAsync(CancellationTokenSource livePollingCts, Task livePolling)
    {
        await livePollingCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await livePolling.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arret attendu.
        }
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

        // Priorite au fichier de scenario, exactement comme en mode autonome (Tempest.Host/Program.cs) :
        // son contenu est lu ici, une seule fois, cote maitre — un worker distant n'a aucune
        // raison de partager le meme systeme de fichiers que lui.
        string? scenarioContent = null;
        ScenarioFormat? scenarioFormat = null;
        if (!string.IsNullOrWhiteSpace(tempestOptions.ScenarioFile))
        {
            (string content, ScenarioFormat format) = ScenarioDefinitionLoader.ReadRaw(tempestOptions.ScenarioFile);
            scenarioContent = content;
            scenarioFormat = format;
        }

        return new WorkerPrepareRequest
        {
            Profile = scaledProfile,
            Workflow = tempestOptions.Workflow,
            ScenarioContent = scenarioContent,
            ScenarioFormat = scenarioFormat,
            WebSocketEchoOptions = configuration.GetSection("WebSocketEcho").Get<WebSocketEchoWorkflowOptions>(),
            GrpcEchoOptions = configuration.GetSection("GrpcEcho").Get<GrpcEchoWorkflowOptions>(),
            DynamicCheckoutOptions = configuration.GetSection("DynamicCheckout").Get<DynamicCheckoutWorkflowOptions>(),
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