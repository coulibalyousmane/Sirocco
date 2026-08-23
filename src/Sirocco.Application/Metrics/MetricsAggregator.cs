using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;

namespace Sirocco.Application.Metrics;

/// <summary>
/// Agrege les mesures en statistiques exploitables, sur deux perimetres simultanes :
/// cumule depuis le debut du tir, et glissant sur une fenetre recente.
/// <para>
/// <b>Pourquoi les deux.</b> Un verdict d'integration continue doit porter sur l'integralite
/// du tir, sinon il depend de l'instant ou on regarde. Un tableau de bord temps reel, lui,
/// a besoin de l'inverse : un p99 cumule sur dix minutes met une eternite a reagir a une
/// degradation. Les deux repondent a des questions differentes, et le cout de fournir les
/// deux se limite a un second increment de tableau par mesure.
/// </para>
/// <para>
/// Le tableau des etapes est dimensionne une fois pour toutes d'apres le
/// <see cref="StepRegistry"/> scelle : l'acces se fait par index, sans dictionnaire ni
/// verrou global sur le chemin d'agregation.
/// </para>
/// </summary>
public sealed class MetricsAggregator
{
    private readonly StepRegistry _steps;
    private readonly CustomMetricsAggregator? _customMetrics;
    private readonly MetricsAggregatorOptions _options;
    private readonly Lock _buildGate = new();
    private readonly long _bucketTicks;
    private readonly long _startTicks;

    private StepAccumulator[]? _accumulators;
    private StepId _iterationStep = StepId.None;

    /// <summary>Cree un agregateur pour un registre d'etapes.</summary>
    /// <param name="steps">
    /// Registre d'etapes. Peut encore etre vide a cet instant : voir <see cref="EnsureAccumulators"/>.
    /// </param>
    /// <param name="options">Reglages de la fenetre glissante.</param>
    /// <param name="customMetrics">
    /// Agregateur de metriques personnalisees a inclure dans chaque rapport. Omis (donc rapport
    /// sans <see cref="LoadTestReport.CustomMetrics"/>) si le scenario n'en declare aucune.
    /// </param>
    public MetricsAggregator(StepRegistry steps, MetricsAggregatorOptions? options = null, CustomMetricsAggregator? customMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(steps);

        MetricsAggregatorOptions effectiveOptions = options ?? new MetricsAggregatorOptions();
        effectiveOptions.Validate();

        _steps = steps;
        _customMetrics = customMetrics;
        _options = effectiveOptions;
        _bucketTicks = Math.Max(1L, SiroccoClock.FromTimeSpan(effectiveOptions.BucketDuration));
        _startTicks = SiroccoClock.Now;
        WindowDuration = effectiveOptions.WindowDuration;
    }

    /// <summary>Duree couverte par la fenetre glissante.</summary>
    public TimeSpan WindowDuration { get; }

    /// <summary>Nombre de mesures perdues, renseigne par le processeur depuis le puits.</summary>
    public long MetricsDropped { get; set; }

    /// <summary>
    /// Agrege une mesure. Une etape inconnue, ou un registre pas encore rempli, est ignore en
    /// silence plutot que de faire tomber le processeur : perdre une metrique est moins grave
    /// qu'interrompre la collecte.
    /// </summary>
    public void Record(in MetricResult result)
    {
        StepAccumulator[]? accumulators = EnsureAccumulators();
        if (accumulators is null)
        {
            return;
        }

        int index = result.Step.Value;
        if ((uint)index >= (uint)accumulators.Length)
        {
            return;
        }

        accumulators[index].Record(in result);
    }

    /// <summary>Photographie les statistiques d'une etape.</summary>
    public StepStatistics SnapshotStep(StepId step, StatisticsScope scope) =>
        SnapshotStep(step, scope, SiroccoClock.Now);

    /// <summary>
    /// Photographie les statistiques d'une etape a un instant donne.
    /// <para>
    /// L'instant est explicite parce que la fenetre glissante avance avec lui : c'est ce qui
    /// permet de verifier l'expiration des paniers sans faire attendre un test.
    /// </para>
    /// </summary>
    public StepStatistics SnapshotStep(StepId step, StatisticsScope scope, long nowTicks)
    {
        StepAccumulator[]? accumulators = EnsureAccumulators();
        return accumulators is not null && (uint)step.Value < (uint)accumulators.Length
            ? accumulators[step.Value].Snapshot(scope, nowTicks)
            : StepStatistics.Empty(step, step.ToString());
    }

    /// <summary>Photographie l'ensemble des statistiques sur le perimetre demande.</summary>
    public LoadTestReport Snapshot(StatisticsScope scope) => Snapshot(scope, SiroccoClock.Now);

    /// <inheritdoc cref="Snapshot(StatisticsScope)" />
    /// <param name="scope">Perimetre temporel du rapport.</param>
    /// <param name="nowTicks">Instant auquel la photographie est prise.</param>
    public LoadTestReport Snapshot(StatisticsScope scope, long nowTicks)
    {
        StepAccumulator[]? accumulators = EnsureAccumulators();
        StepStatistics[] statistics = accumulators is null
            ? []
            : SnapshotAll(accumulators, scope, nowTicks);

        StepStatistics iteration = accumulators is not null && _iterationStep.IsValid
            ? statistics[_iterationStep.Value]
            : StepStatistics.Empty(StepId.None, WellKnownSteps.ITERATION);

        return new LoadTestReport
        {
            Scope = scope,
            Duration = scope == StatisticsScope.Cumulative
                ? SiroccoClock.ToTimeSpan(Math.Max(nowTicks - _startTicks, 0L))
                : WindowDuration,
            Steps = statistics,
            Iteration = iteration,
            MetricsDropped = MetricsDropped,
            CustomMetrics = _customMetrics?.Snapshot() ?? [],
        };
    }

    /// <summary>
    /// Exporte l'etat brut cumule de chaque etape, pour fusion inter-process (mode distribue
    /// Master/Workers). Un worker sans aucune etape enregistree (registre pas encore rempli)
    /// exporte une liste vide plutot que d'echouer.
    /// </summary>
    /// <param name="workerId">Identifiant de ce worker (son adresse jointe), pour diagnostic.</param>
    public WorkerReport ExportRaw(string workerId)
    {
        StepAccumulator[]? accumulators = EnsureAccumulators();
        List<WorkerStepReport> steps = accumulators is null
            ? []
            : [.. accumulators.Select(static a => a.ExportRaw())];

        return new WorkerReport(workerId, MetricsDropped, steps);
    }

    private static StepStatistics[] SnapshotAll(StepAccumulator[] accumulators, StatisticsScope scope, long nowTicks)
    {
        StepStatistics[] statistics = new StepStatistics[accumulators.Length];
        for (int i = 0; i < accumulators.Length; i++)
        {
            statistics[i] = accumulators[i].Snapshot(scope, nowTicks);
        }

        return statistics;
    }

    /// <summary>
    /// Construit les accumulateurs au premier usage reel, jamais a la construction.
    /// <para>
    /// Le registre d'etapes n'est rempli et scelle qu'au demarrage du tir
    /// (<c>TargetRpsLoadEngine.RunAsync</c>), pas a la construction de l'agregateur. Un
    /// conteneur d'injection de dependances ne garantit aucun ordre entre deux singletons
    /// independants resolus par des chemins differents : exiger un registre deja rempli au
    /// constructeur fait echouer le demarrage de l'hote des que l'agregateur est resolu avant
    /// le moteur — ce qui s'est produit des le premier tir reel. Construire paresseusement, au
    /// premier enregistrement ou a la premiere lecture, rend l'agregateur insensible a cet ordre.
    /// </para>
    /// </summary>
    /// <returns><see langword="null"/> si le registre n'a pas encore ete rempli.</returns>
    private StepAccumulator[]? EnsureAccumulators()
    {
        if (_accumulators is { } built)
        {
            return built;
        }

        if (_steps.Count == 0)
        {
            // Le tir n'a pas encore demarre : rien a agreger pour l'instant.
            return null;
        }

        lock (_buildGate)
        {
            if (_accumulators is { } builtWhileWaiting)
            {
                return builtWhileWaiting;
            }

            StepAccumulator[] accumulators = new StepAccumulator[_steps.Count];
            for (int i = 0; i < accumulators.Length; i++)
            {
                StepId step = new(i);
                accumulators[i] = new StepAccumulator(step, _steps.GetName(step), _options.WindowBucketCount, _bucketTicks);
            }

            _iterationStep = _steps.TryGetId(WellKnownSteps.ITERATION, out StepId iteration) ? iteration : StepId.None;
            _accumulators = accumulators;

            return accumulators;
        }
    }
}