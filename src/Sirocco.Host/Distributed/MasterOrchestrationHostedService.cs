using System.Net.Http.Json;
using Sirocco.Domain.Metrics;
using Sirocco.Host.Configuration;
using Sirocco.Scenarios;
using Sirocco.Scenarios.Declarative;

namespace Sirocco.Host.Distributed;

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
    SiroccoHostOptions siroccoOptions,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IHostApplicationLifetime lifetime,
    ILogger<MasterOrchestrationHostedService> logger) : BackgroundService
{
    private const int EXIT_CODE_SUCCESS = 0;
    private const int EXIT_CODE_FAILURE = 1;

    /// <summary>
    /// Mince enveloppe de securite autour de <see cref="RunAsync"/> : une exception non geree
    /// dans un <see cref="BackgroundService"/> l'arrete silencieusement sans jamais positionner
    /// <see cref="Environment.ExitCode"/> — le <c>Job</c> Kubernetes rapportait alors <c>Complete</c>
    /// (sortie 0) et <c>TestRun.status.phase</c> passait a tort a <c>Succeeded</c> sur un tir qui
    /// n'a jamais eu lieu (trouvaille reelle en verifiant sur un vrai cluster : une resolution DNS
    /// transitoire d'un worker de <c>StatefulSet</c> tout juste cree suffit a le declencher — pas
    /// une hypothese). Ne change rien au chemin normal : <see cref="RunAsync"/> contient le corps
    /// exact d'avant ce correctif.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Arret normal de l'hote : rien a signaler comme un echec du tir.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Le tir distribue a echoue avec une exception non geree.");
            ExitIfConfigured(passed: false);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        if (masterOptions.StagePlannedWorkers is { } stagePlan)
        {
            // Chemin adaptatif (autoscaling piloté par l'opérateur Kubernetes) : le corps
            // ci-dessous, inchangé, ne s'exécute alors jamais — voir ExecuteAdaptiveAsync.
            await ExecuteAdaptiveAsync(stagePlan, stoppingToken).ConfigureAwait(false);
            return;
        }

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
        client.DefaultRequestHeaders.Authorization = ClusterAuthentication.BuildHeader(siroccoOptions.ClusterSharedSecret);

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

        await FinalizeAsync(workers, startedAt, livePollingCts, livePolling, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attend les rapports finaux des workers dispatches, fusionne, evalue les seuils et arrete
    /// l'hote si configure — partagee par le chemin figé (<see cref="ExecuteAsync"/>) et le chemin
    /// adaptatif (<see cref="ExecuteAdaptiveAsync"/>), qui different seulement dans la maniere
    /// dont ils construisent <paramref name="dispatchedWorkers"/>.
    /// </summary>
    private async Task FinalizeAsync(
        IReadOnlyList<string> dispatchedWorkers,
        DateTime startedAt,
        CancellationTokenSource livePollingCts,
        Task livePolling,
        CancellationToken stoppingToken)
    {
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
            reports = await coordinator.WaitForReportsAsync(dispatchedWorkers.Count, reportWaitCts.Token).ConfigureAwait(false);
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
        IReadOnlyList<string> lostWorkers = [.. dispatchedWorkers.Where(worker => !reportedWorkerIds.Contains(worker))];

        await StopLivePollingAsync(livePollingCts, livePolling).ConfigureAwait(false);

        if (reports.Count == 0)
        {
            logger.LogError(
                "Tous les workers dispatches ({Count}) ont ete perdus en cours de tir : aucun rapport a fusionner.",
                dispatchedWorkers.Count);
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

        ThresholdReport thresholds = ThresholdReport.Evaluate(siroccoOptions.Thresholds, report);
        coordinator.FinalThresholds = thresholds;

        if (siroccoOptions.Thresholds.Count > 0 && logger.IsEnabled(LogLevel.Information))
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
        List<LoadStageOptions> scaledProfile = [.. siroccoOptions.Profile
            .Select(stage => new LoadStageOptions
            {
                FromRps = stage.FromRps / workerCount,
                ToRps = stage.ToRps / workerCount,
                DurationSeconds = stage.DurationSeconds,
            })];

        int maxVirtualUsersPerWorker = Math.Max(1, (int)Math.Ceiling(siroccoOptions.MaxVirtualUsers / (double)workerCount));

        return BuildPrepareRequest(scaledProfile, maxVirtualUsersPerWorker);
    }

    /// <summary>
    /// Chemin adaptatif : un worker qui rejoint au palier <paramref name="fromStageIndex"/> recoit
    /// en une seule preparation tous les paliers restants (jamais de re-preparation d'un worker
    /// deja lance, voir <see cref="WorkerCoordinator.Prepare"/>), chacun divise par le compte
    /// <i>prevu</i> pour ce palier (<paramref name="stagePlan"/>) — pas par le nombre de workers
    /// reellement actifs a cet instant, puisque ce compte anticipe des workers qui n'ont peut-etre
    /// pas encore rejoint (voir <c>V1TestRun.AutoscalingSpec.ScaleAheadSeconds</c> cote operateur,
    /// dans le projet <c>Sirocco.Operator</c> — non reference depuis ce projet).
    /// </summary>
    private WorkerPrepareRequest BuildAdaptivePrepareRequest(int[] stagePlan, int fromStageIndex)
    {
        List<LoadStageOptions> scaledProfile = [];
        for (int i = fromStageIndex; i < siroccoOptions.Profile.Count; i++)
        {
            LoadStageOptions stage = siroccoOptions.Profile[i];
            scaledProfile.Add(new LoadStageOptions
            {
                FromRps = stage.FromRps / stagePlan[i],
                ToRps = stage.ToRps / stagePlan[i],
                DurationSeconds = stage.DurationSeconds,
            });
        }

        int maxVirtualUsersPerWorker = Math.Max(1, (int)Math.Ceiling(siroccoOptions.MaxVirtualUsers / (double)stagePlan[fromStageIndex]));

        return BuildPrepareRequest(scaledProfile, maxVirtualUsersPerWorker);
    }

    private WorkerPrepareRequest BuildPrepareRequest(List<LoadStageOptions> scaledProfile, int maxVirtualUsersPerWorker)
    {
        // Priorite au fichier de scenario, exactement comme en mode autonome (Sirocco.Host/Program.cs) :
        // son contenu est lu ici, une seule fois, cote maitre — un worker distant n'a aucune
        // raison de partager le meme systeme de fichiers que lui.
        string? scenarioContent = null;
        ScenarioFormat? scenarioFormat = null;
        if (!string.IsNullOrWhiteSpace(siroccoOptions.ScenarioFile))
        {
            (string content, ScenarioFormat format) = ScenarioDefinitionLoader.ReadRaw(siroccoOptions.ScenarioFile);
            scenarioContent = content;
            scenarioFormat = format;
        }

        return new WorkerPrepareRequest
        {
            Profile = scaledProfile,
            Workflow = siroccoOptions.Workflow,
            ScenarioContent = scenarioContent,
            ScenarioFormat = scenarioFormat,
            WebSocketEchoOptions = configuration.GetSection("WebSocketEcho").Get<WebSocketEchoWorkflowOptions>(),
            GrpcEchoOptions = configuration.GetSection("GrpcEcho").Get<GrpcEchoWorkflowOptions>(),
            DynamicCheckoutOptions = configuration.GetSection("DynamicCheckout").Get<DynamicCheckoutWorkflowOptions>(),
            TargetBaseUrl = siroccoOptions.TargetBaseUrl,
            MaxVirtualUsers = maxVirtualUsersPerWorker,
        };
    }

    /// <summary>
    /// Chemin adaptatif (autoscaling) : suit un plan de paliers pose par l'operateur Kubernetes
    /// (<see cref="MasterOptions.StagePlannedWorkers"/>) plutot que d'attendre un nombre fixe de
    /// workers une seule fois puis de figer la liste de travail pour tout le tir. Un nouveau
    /// worker enregistre entre deux paliers est dispatche au debut du palier suivant (jamais
    /// re-prepare un worker deja lance) ; un worker retire par le controleur (StatefulSet reduit)
    /// tombe dans le meme filet de securite que dans le chemin fige
    /// (<see cref="MasterCoordinator.MarkDeadIfStale"/>) s'il ne finit pas son arret propre a temps.
    /// </summary>
    private async Task ExecuteAdaptiveAsync(int[] stagePlan, CancellationToken stoppingToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Orchestration adaptative : {StageCount} palier(s), {Initial} worker(s) requis pour le premier.",
                stagePlan.Length,
                stagePlan[0]);
        }

        IReadOnlyList<string> initialWorkers = await coordinator
            .WaitForRegistrationsAsync(stagePlan[0], TimeSpan.FromSeconds(masterOptions.RegistrationTimeoutSeconds), stoppingToken)
            .ConfigureAwait(false);

        if (initialWorkers.Count == 0)
        {
            logger.LogError("Aucun worker enregistre : impossible de distribuer le tir.");
            ExitIfConfigured(passed: false);
            return;
        }

        HttpClient client = httpClientFactory.CreateClient(ClusterCertificatePinning.CLUSTER_CLIENT_NAME);
        client.DefaultRequestHeaders.Authorization = ClusterAuthentication.BuildHeader(siroccoOptions.ClusterSharedSecret);

        Lock dispatchGate = new();
        List<string> dispatchedWorkers = [];
        DateTime startedAt = DateTime.UtcNow;

        using CancellationTokenSource livePollingCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task livePolling = PollAdaptiveLiveReportsAsync(client, dispatchGate, dispatchedWorkers, startedAt, livePollingCts.Token);

        for (int stageIndex = 0; stageIndex < stagePlan.Length; stageIndex++)
        {
            IReadOnlyList<string> newWorkers;
            lock (dispatchGate)
            {
                newWorkers = [.. coordinator.RegisteredWorkers.Where(worker => !dispatchedWorkers.Contains(worker))];
            }

            if (newWorkers.Count > 0)
            {
                WorkerPrepareRequest request = BuildAdaptivePrepareRequest(stagePlan, stageIndex);
                await Task.WhenAll(newWorkers.Select(worker => PrepareAsync(client, worker, request, stoppingToken))).ConfigureAwait(false);
                await Task.WhenAll(newWorkers.Select(worker => StartAsync(client, worker, stoppingToken))).ConfigureAwait(false);

                lock (dispatchGate)
                {
                    dispatchedWorkers.AddRange(newWorkers);
                }

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "Palier {Stage} : {Count} nouveau(x) worker(s) dispatche(s) : {Workers}.",
                        stageIndex,
                        newWorkers.Count,
                        string.Join(", ", newWorkers));
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(siroccoOptions.Profile[stageIndex].DurationSeconds), stoppingToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> allDispatchedWorkers;
        lock (dispatchGate)
        {
            allDispatchedWorkers = [.. dispatchedWorkers];
        }

        await FinalizeAsync(allDispatchedWorkers, startedAt, livePollingCts, livePolling, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Meme role que <see cref="PollLiveReportsAsync"/> pour le chemin adaptatif : la liste des
    /// workers a sonder grandit au fil des paliers (<paramref name="dispatchedWorkers"/> est
    /// mutee par <see cref="ExecuteAdaptiveAsync"/> pendant que cette boucle tourne), d'ou le
    /// verrou partage plutot qu'une liste figee comme dans le chemin fige. Les workers deja
    /// dispatches sont aussi les seuls candidats a une mort declaree (voir le commentaire de
    /// <see cref="MasterCoordinator.MarkDeadIfStale"/>) : un worker enregistre par avance par
    /// l'operateur mais pas encore prepare ne doit jamais etre declare mort faute d'un rapport
    /// qu'on ne lui a pas encore demande.
    /// </summary>
    private async Task PollAdaptiveLiveReportsAsync(HttpClient client, Lock dispatchGate, List<string> dispatchedWorkers, DateTime startedAt, CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(masterOptions.LivePollIntervalSeconds);
        TimeSpan deadAfter = TimeSpan.FromSeconds(masterOptions.WorkerDeadAfterSeconds);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<string> workers;
                lock (dispatchGate)
                {
                    workers = [.. dispatchedWorkers];
                }

                if (workers.Count > 0)
                {
                    WorkerReport?[] snapshots = await Task
                        .WhenAll(workers.Select(worker => FetchRawReportAsync(client, worker, cancellationToken)))
                        .ConfigureAwait(false);

                    List<WorkerReport> received = [.. snapshots.OfType<WorkerReport>()];
                    if (received.Count > 0)
                    {
                        coordinator.LiveReport = ClusterReportAggregator.Merge(received, DateTime.UtcNow - startedAt);
                    }

                    IReadOnlyList<string> newlyDead = coordinator.MarkDeadIfStale(deadAfter, workers);
                    foreach (string worker in newlyDead)
                    {
                        logger.LogWarning(
                            "Worker {WorkerUrl} declare perdu : aucun heartbeat depuis plus de {DeadAfter}s.",
                            worker,
                            masterOptions.WorkerDeadAfterSeconds);
                    }
                }

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Arret normal du sondage : les rapports finaux ont pris le relais.
        }
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
        if (siroccoOptions.ExitAfterRun)
        {
            Environment.ExitCode = passed ? EXIT_CODE_SUCCESS : EXIT_CODE_FAILURE;
            lifetime.StopApplication();
        }
    }
}