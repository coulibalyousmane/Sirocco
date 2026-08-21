using Tempest.Domain.Metrics;

namespace Tempest.Host.Distributed;

/// <summary>
/// Etat du maitre : workers enregistres, rapports collectes, verdict final une fois le tir
/// distribue termine.
/// <para>
/// Auto-enregistrement dynamique : le maitre ne connait a l'avance ni l'identite ni le nombre
/// exact de workers, seulement combien il en <i>attend</i> (<see cref="MasterOptions.ExpectedWorkers"/>).
/// </para>
/// </summary>
public sealed class MasterCoordinator
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _registeredWorkers = new(StringComparer.Ordinal);
    private readonly List<WorkerReport> _reports = [];
    private readonly Dictionary<string, DateTimeOffset> _lastHeartbeat = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deadWorkers = new(StringComparer.Ordinal);

    private TaskCompletionSource? _registrationTarget;
    private int _registrationGoal;

    private TaskCompletionSource? _reportsTarget;
    private int _reportsGoal;

    /// <summary>Rapport fusionne final, disponible une fois tous les workers rentres.</summary>
    public LoadTestReport? FinalReport { get; set; }

    /// <summary>Verdict des seuils sur le rapport fusionne.</summary>
    public ThresholdReport? FinalThresholds { get; set; }

    /// <summary>
    /// Dernier rapport combine obtenu par sondage des workers pendant le tir — approximatif
    /// (intervalle de sondage), rafraichi en continu, contrairement a <see cref="FinalReport"/>
    /// qui n'est construit qu'une seule fois, a partir des rapports pousses par les workers.
    /// </summary>
    public LoadTestReport? LiveReport { get; set; }

    /// <summary>Enregistre un worker. Idempotent : un doublon n'est compte qu'une fois.</summary>
    public void Register(string workerUrl)
    {
        lock (_gate)
        {
            _registeredWorkers.Add(workerUrl);
            _lastHeartbeat[workerUrl] = DateTimeOffset.UtcNow;

            if (_registrationTarget is not null && _registeredWorkers.Count >= _registrationGoal)
            {
                _registrationTarget.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Signale que <paramref name="workerUrl"/> est toujours vivant (<c>POST /master/heartbeat</c>).
    /// Annule un faux positif si ce worker avait ete declare mort entre-temps
    /// (<see cref="MarkDeadIfStale"/>) — sauf si l'attente des rapports a deja rendu la main sur la
    /// base de cette perte : voir la remarque de <see cref="MarkDeadIfStale"/>.
    /// </summary>
    public void Heartbeat(string workerUrl)
    {
        lock (_gate)
        {
            _lastHeartbeat[workerUrl] = DateTimeOffset.UtcNow;
            _deadWorkers.Remove(workerUrl);
        }
    }

    /// <summary>
    /// Declare perdu tout worker enregistre qui n'a pas rapporte, n'est pas deja marque mort, et
    /// n'a pas donne signe de vie depuis plus de <paramref name="deadAfter"/>. Renvoie les workers
    /// nouvellement declares morts par cet appel (pour un log unique cote appelant, pas repete a
    /// chaque sondage).
    /// <para>
    /// Un worker ainsi marque compte comme "rentre" pour <see cref="WaitForReportsAsync"/> : c'est
    /// ce qui permet au maitre de cesser d'attendre un rapport qui ne viendra jamais plutot que de
    /// rester bloque indefiniment.
    /// </para>
    /// <para>
    /// <paramref name="candidates"/> restreint les workers eligibles a une mort declaree —
    /// <see langword="null"/> (comportement par defaut, inchange) considere tous les workers
    /// enregistres. Le chemin d'orchestration adaptatif (<c>MasterOrchestrationHostedService.ExecuteAdaptiveAsync</c>)
    /// y passe explicitement les seuls workers deja dispatches : un worker enregistre par avance
    /// (le controleur Kubernetes agrandit le <c>StatefulSet</c> avant le palier qui en a besoin)
    /// mais pas encore prepare ne doit jamais etre declare mort faute de rapport qu'on ne lui a
    /// pas encore demande.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> MarkDeadIfStale(TimeSpan deadAfter, IReadOnlyCollection<string>? candidates = null)
    {
        List<string> newlyDead = [];

        lock (_gate)
        {
            DateTimeOffset threshold = DateTimeOffset.UtcNow - deadAfter;

            foreach (string worker in candidates ?? _registeredWorkers)
            {
                if (_deadWorkers.Contains(worker))
                {
                    continue;
                }

                if (_reports.Any(report => report.WorkerId == worker))
                {
                    continue;
                }

                if (_lastHeartbeat.TryGetValue(worker, out DateTimeOffset lastSeen) && lastSeen <= threshold)
                {
                    _deadWorkers.Add(worker);
                    newlyDead.Add(worker);
                }
            }

            if (newlyDead.Count > 0)
            {
                TryCompleteReportsWait();
            }
        }

        return newlyDead;
    }

    /// <summary>
    /// Attend que <paramref name="expected"/> workers se soient enregistres, ou l'expiration
    /// de <paramref name="timeout"/> — au premier des deux. Renvoie les workers enregistres a
    /// cet instant, quel qu'en soit le nombre.
    /// </summary>
    public async Task<IReadOnlyList<string>> WaitForRegistrationsAsync(int expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _registrationGoal = expected;
            _registrationTarget = tcs;

            if (_registeredWorkers.Count >= expected)
            {
                tcs.TrySetResult();
            }
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await tcs.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Fenetre d'enregistrement expiree : on distribue le tir aux workers deja la.
        }

        lock (_gate)
        {
            return [.. _registeredWorkers];
        }
    }

    /// <summary>Enregistre le rapport pousse par un worker a la fin de son tir local.</summary>
    public void SubmitReport(WorkerReport report)
    {
        lock (_gate)
        {
            _reports.Add(report);
            TryCompleteReportsWait();
        }
    }

    /// <summary>
    /// Attend que les <paramref name="expected"/> workers dispatches aient tous rapporte —
    /// "rapporte" incluant desormais un worker declare mort (<see cref="MarkDeadIfStale"/>), pas
    /// seulement un worker dont le rapport a ete effectivement recu : sans cela, un worker perdu
    /// en cours de tir bloquerait cette attente indefiniment.
    /// </summary>
    public async Task<IReadOnlyList<WorkerReport>> WaitForReportsAsync(int expected, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _reportsGoal = expected;
            _reportsTarget = tcs;

            if (_reports.Count + _deadWorkers.Count >= expected)
            {
                tcs.TrySetResult();
            }
        }

        await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            return [.. _reports];
        }
    }

    /// <summary>Complete l'attente en cours si assez de workers sont rentres ou declares morts.</summary>
    private void TryCompleteReportsWait()
    {
        if (_reportsTarget is not null && _reports.Count + _deadWorkers.Count >= _reportsGoal)
        {
            _reportsTarget.TrySetResult();
        }
    }

    /// <summary>
    /// Workers enregistres a l'instant de l'appel, y compris ceux arrives apres qu'une premiere
    /// attente ait deja rendu la main (voir <see cref="WaitForRegistrationsAsync"/>) — utilise par
    /// le chemin d'orchestration adaptatif (<c>MasterOrchestrationHostedService.ExecuteAdaptiveAsync</c>)
    /// pour detecter les workers nouvellement inscrits palier apres palier.
    /// </summary>
    public IReadOnlyList<string> RegisteredWorkers
    {
        get
        {
            lock (_gate)
            {
                return [.. _registeredWorkers];
            }
        }
    }

    /// <summary>Workers actuellement declares morts (voir <see cref="MarkDeadIfStale"/>), a l'instant de l'appel.</summary>
    public IReadOnlyList<string> DeadWorkers
    {
        get
        {
            lock (_gate)
            {
                return [.. _deadWorkers];
            }
        }
    }

    /// <summary>
    /// Rapports deja recus a l'instant de l'appel, meme si <see cref="WaitForReportsAsync"/> n'a
    /// pas encore rendu la main pour tous les workers dispatches — utilise quand un plafond
    /// absolu (<c>MasterOptions.ReportTimeoutSeconds</c>) force a proceder avec un sous-ensemble.
    /// </summary>
    public IReadOnlyList<WorkerReport> ReportsSoFar
    {
        get
        {
            lock (_gate)
            {
                return [.. _reports];
            }
        }
    }

    /// <summary>
    /// Rapport le plus recent disponible pour ce maitre : <see cref="FinalReport"/> une fois le
    /// tir termine (autoritatif), <see cref="LiveReport"/> pendant qu'il tourne (rafraichi par
    /// sondage), ou un rapport vide avant le premier sondage.
    /// <para>
    /// Meme signature que <see cref="Tempest.Application.Metrics.MetricsAggregator.Snapshot(StatisticsScope)"/> :
    /// le maitre n'a pas de fenetre glissante propre (il ne fait que fusionner des rapports
    /// deja construits par les workers), donc <paramref name="scope"/> n'a d'effet que sur le
    /// champ <see cref="LoadTestReport.Scope"/> du rapport vide — utilise pour que
    /// <see cref="Infrastructure.Metrics.TempestMeter"/> reste utilisable telle quelle, sans
    /// distinguer maitre et agregateur local.
    /// </para>
    /// </summary>
    public LoadTestReport Snapshot(StatisticsScope scope) => FinalReport ?? LiveReport ?? EmptyReport(scope);

    private static LoadTestReport EmptyReport(StatisticsScope scope) => new()
    {
        Scope = scope,
        Duration = TimeSpan.Zero,
        Steps = [],
        Iteration = StepStatistics.Empty(StepId.None, WellKnownSteps.ITERATION),
        MetricsDropped = 0L,
    };
}