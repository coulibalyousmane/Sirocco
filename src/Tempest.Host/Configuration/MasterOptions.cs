namespace Tempest.Host.Configuration;

/// <summary>
/// Reglages du role <see cref="TempestHostOptions.ROLE_MASTER"/>. Section <c>Master</c>.
/// </summary>
public sealed class MasterOptions
{
    /// <summary>Duree par defaut de la fenetre d'enregistrement des workers, en secondes.</summary>
    public const int DEFAULT_REGISTRATION_TIMEOUT_SECONDS = 30;

    /// <summary>
    /// Nombre de workers attendus. Des que ce nombre s'est enregistre, le maitre distribue le
    /// tir sans attendre la fin de la fenetre d'enregistrement.
    /// </summary>
    public required int ExpectedWorkers { get; init; }

    /// <summary>
    /// Fenetre d'enregistrement : passe ce delai, le maitre distribue le tir aux workers deja
    /// enregistres (au moins un), plutot que d'attendre indefiniment un worker qui ne viendra
    /// jamais.
    /// </summary>
    public int RegistrationTimeoutSeconds { get; init; } = DEFAULT_REGISTRATION_TIMEOUT_SECONDS;

    /// <summary>Intervalle par defaut de sondage du tableau de bord distribue, en secondes.</summary>
    public const int DEFAULT_LIVE_POLL_INTERVAL_SECONDS = 2;

    /// <summary>
    /// Frequence a laquelle le maitre sonde <c>/worker/report/raw</c> sur chaque worker pour
    /// rafraichir <c>/report/live</c> pendant le tir. Ne remplace pas le rapport final — celui-la
    /// reste construit une seule fois, a partir des rapports pousses par les workers a la fin de
    /// leur tir local (voir <see cref="TempestHostOptions"/>), pas d'un sondage approximatif.
    /// </summary>
    public int LivePollIntervalSeconds { get; init; } = DEFAULT_LIVE_POLL_INTERVAL_SECONDS;

    /// <summary>Valeur par defaut de <see cref="WorkerDeadAfterSeconds"/>.</summary>
    public const int DEFAULT_WORKER_DEAD_AFTER_SECONDS = 20;

    /// <summary>
    /// Delai sans heartbeat (<c>POST /master/heartbeat</c>) au-dela duquel un worker dispatche
    /// mais n'ayant pas encore rapporte est declare perdu : le maitre cesse alors de l'attendre
    /// et fusionne le rapport final avec les workers restants (voir
    /// <see cref="Tempest.Domain.Metrics.LoadTestReport.LostWorkers"/>), plutot que d'attendre
    /// indefiniment un rapport qui ne viendra jamais. <see cref="DEFAULT_WORKER_DEAD_AFTER_SECONDS"/>
    /// par defaut, soit 4 heartbeats manques a l'intervalle par defaut du worker
    /// (<see cref="WorkerOptions.HeartbeatIntervalSeconds"/>).
    /// <para>
    /// Ne detecte que le worker dont le <i>process</i> ne repond plus (crash, coupure reseau) —
    /// pas un worker dont le process reste vivant mais dont le tir local est bloque (le heartbeat
    /// continue alors d'arriver normalement). Ce cas residuel releve de
    /// <see cref="ReportTimeoutSeconds"/>.
    /// </para>
    /// </summary>
    public int WorkerDeadAfterSeconds { get; init; } = DEFAULT_WORKER_DEAD_AFTER_SECONDS;

    /// <summary>
    /// Plafond absolu, en secondes, sur l'attente des rapports finaux — au-dela, le maitre
    /// fusionne ce qu'il a deja recu plutot que d'attendre plus longtemps, meme si des workers
    /// continuent de heartbeat normalement. <see langword="null"/> par defaut : seule la
    /// detection par heartbeat (<see cref="WorkerDeadAfterSeconds"/>) s'applique alors. Filet de
    /// securite pour le cas qu'elle ne couvre pas (process vivant, tir local bloque) — a
    /// renseigner explicitement si ce risque est reel pour le scenario execute.
    /// </summary>
    public int? ReportTimeoutSeconds { get; init; }

    /// <summary>
    /// Plan de paliers pose par l'operateur Kubernetes (un nombre de workers requis par palier du
    /// profil, voir <c>Tempest.Operator.TestRunResources.ComputeStageWorkerCounts</c>) : <see langword="null"/>
    /// par defaut, le chemin d'orchestration existant (<see cref="ExpectedWorkers"/>, liste de
    /// workers figee une seule fois) reste alors totalement inchange. Renseigne, l'orchestration
    /// suit ce plan au lieu d'attendre un nombre fixe de workers une seule fois au demarrage —
    /// voir <c>MasterOrchestrationHostedService.ExecuteAdaptiveAsync</c>.
    /// </summary>
    public int[]? StagePlannedWorkers { get; init; }

    /// <summary>Valide la coherence des reglages.</summary>
    public void Validate()
    {
        // ExpectedWorkers n'a aucun sens dans le chemin adaptatif (StagePlannedWorkers pose le
        // nombre de workers palier par palier) : l'operateur ne l'emet pas dans ce cas (voir
        // TestRunResources.BuildMasterEnv), donc ne pas exiger qu'il soit renseigne ici non plus.
        if (StagePlannedWorkers is null && ExpectedWorkers < 1)
        {
            throw new ArgumentException("ExpectedWorkers doit valoir au moins 1.", nameof(ExpectedWorkers));
        }

        if (RegistrationTimeoutSeconds < 1)
        {
            throw new ArgumentException("RegistrationTimeoutSeconds doit valoir au moins 1.", nameof(RegistrationTimeoutSeconds));
        }

        if (LivePollIntervalSeconds < 1)
        {
            throw new ArgumentException("LivePollIntervalSeconds doit valoir au moins 1.", nameof(LivePollIntervalSeconds));
        }

        if (WorkerDeadAfterSeconds < 1)
        {
            throw new ArgumentException("WorkerDeadAfterSeconds doit valoir au moins 1.", nameof(WorkerDeadAfterSeconds));
        }

        if (ReportTimeoutSeconds is < 1)
        {
            throw new ArgumentException("ReportTimeoutSeconds doit valoir au moins 1 s'il est renseigne.", nameof(ReportTimeoutSeconds));
        }

        if (StagePlannedWorkers is { } stagePlannedWorkers && stagePlannedWorkers.Any(count => count < 1))
        {
            throw new ArgumentException("Chaque palier de StagePlannedWorkers doit valoir au moins 1.", nameof(StagePlannedWorkers));
        }
    }
}