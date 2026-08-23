using System.Threading.Channels;
using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;

namespace Sirocco.Application.Execution;

/// <summary>
/// Chef d'orchestre du tir de charge : un <see cref="ILoadScheduler"/> sur son thread dedie,
/// un canal borne de jetons, et un banc d'utilisateurs virtuels consommateurs.
/// <para>
/// <b>Pourquoi ce decoupage.</b> Melanger l'horloge et l'execution dans une meme boucle est
/// le defaut classique des injecteurs maison : la latence de la cible se met a piloter la
/// cadence de tir, et l'outil mesure alors sa propre lenteur. Ici, le rythme est produit par
/// un thread qui ne fait <i>que</i> compter le temps, et l'execution vit de l'autre cote d'un
/// canal sans verrou. Si les consommateurs decrochent, le rythme ne ralentit pas : la file
/// se remplit, et le retard devient une metrique au lieu de disparaitre.
/// </para>
/// </summary>
public sealed class TargetRpsLoadEngine
{
    private readonly ILoadScheduler _scheduler;
    private readonly IWorkflow _workflow;
    private readonly HttpClient _httpClient;
    private readonly IMetricSink _sink;
    private readonly ICustomMetricSink _customMetricSink;
    private readonly LoadTestOptions _options;

    /// <summary>Cree un moteur de tir.</summary>
    /// <param name="scheduler">Source de cadence.</param>
    /// <param name="workflow">Scenario execute en boucle.</param>
    /// <param name="httpClient">Client HTTP partage par tous les utilisateurs virtuels.</param>
    /// <param name="sink">Destination des mesures.</param>
    /// <param name="options">Reglages de l'injecteur.</param>
    /// <param name="steps">Registre d'etapes, partage avec l'agregateur de metriques.</param>
    /// <param name="customMetrics">
    /// Registre de metriques personnalisees, partage avec son propre agregateur. Un registre
    /// autonome si omis : seul le cablage par injection de dependances (<c>AddSiroccoEngine</c>)
    /// le partage reellement avec un <c>CustomMetricsAggregator</c> capable de le lire.
    /// </param>
    /// <param name="customMetricSink">Destination des metriques personnalisees. Sans effet si omis.</param>
    public TargetRpsLoadEngine(
        ILoadScheduler scheduler,
        IWorkflow workflow,
        HttpClient httpClient,
        IMetricSink sink,
        LoadTestOptions options,
        StepRegistry steps,
        CustomMetricRegistry? customMetrics = null,
        ICustomMetricSink? customMetricSink = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(steps);

        options.Validate();

        _scheduler = scheduler;
        _workflow = workflow;
        _httpClient = httpClient;
        _sink = sink;
        _customMetricSink = customMetricSink ?? NullCustomMetricSink.Instance;
        _options = options;
        Steps = steps;
        CustomMetrics = customMetrics ?? new CustomMetricRegistry();
    }

    /// <summary>Registre des etapes, scelle des le demarrage du tir.</summary>
    public StepRegistry Steps { get; }

    /// <summary>Registre des metriques personnalisees, scelle des le demarrage du tir.</summary>
    public CustomMetricRegistry CustomMetrics { get; }

    /// <summary>
    /// Nombre d'utilisateurs virtuels actuellement actifs, mis a jour par chaque
    /// <see cref="VirtualUserWorker"/>. Disponible avant meme le demarrage du tir (comme
    /// <see cref="Steps"/>/<see cref="CustomMetrics"/>) pour qu'un appelant puisse le relever
    /// periodiquement en parallele de <see cref="RunAsync"/>.
    /// </summary>
    public ActiveVirtualUserGauge ActiveVirtualUsers { get; } = new();

    /// <summary>Deroule le tir complet et renvoie son bilan.</summary>
    /// <param name="cancellationToken">Interrompt le tir avant la fin du profil.</param>
    public async Task<LoadTestSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        StepId iterationStep = PrepareSteps();

        await _workflow.SetUpAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await RunCoreAsync(iterationStep, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Jamais avec le jeton du tir : un arret anticipe empecherait tout nettoyage.
            await _workflow.TearDownAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Declare les etapes puis scelle le registre. Sceller avant le premier tir garantit
    /// qu'aucun identifiant n'apparaitra pendant que l'agregateur indexe deja ses tableaux.
    /// </summary>
    private StepId PrepareSteps()
    {
        StepId iterationStep = Steps.Register(WellKnownSteps.ITERATION);
        _workflow.RegisterSteps(Steps);
        Steps.Seal();

        _workflow.RegisterMetrics(CustomMetrics);
        CustomMetrics.Seal();

        return iterationStep;
    }

    private async Task<LoadTestSummary> RunCoreAsync(StepId iterationStep, CancellationToken cancellationToken)
    {
        Channel<ExecutionToken> tokens = CreateTokenChannel();

        SchedulerThreadHost schedulerHost = new(_scheduler, tokens.Writer);
        schedulerHost.Start(cancellationToken);

        IReadOnlyList<VirtualUserWorker> workers;
        if (_options.RampProfile is { } rampProfile)
        {
            RampingVirtualUserPool pool = new(rampProfile, index => CreateWorker(index, iterationStep));
            await pool.RunAsync(tokens.Reader, cancellationToken).ConfigureAwait(false);
            workers = pool.Workers;
        }
        else
        {
            VirtualUserWorker[] fixedWorkers = CreateWorkers(iterationStep);
            Task[] running = new Task[fixedWorkers.Length];
            for (int i = 0; i < fixedWorkers.Length; i++)
            {
                VirtualUserWorker worker = fixedWorkers[i];
                running[i] = Task.Run(
                    () => worker.RunAsync(tokens.Reader, cancellationToken),
                    CancellationToken.None);
            }

            await Task.WhenAll(running).ConfigureAwait(false);
            workers = fixedWorkers;
        }

        schedulerHost.WaitForCompletion();

        return Summarize(workers, SiroccoClock.Now - _scheduler.StartTicks);
    }

    private Channel<ExecutionToken> CreateTokenChannel() =>
        Channel.CreateBounded<ExecutionToken>(new BoundedChannelOptions(_options.EffectiveTokenQueueCapacity)
        {
            // Wait, jamais DropWrite : un jeton perdu est une requete qui disparait du rapport.
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false,
        });

    private VirtualUserWorker[] CreateWorkers(StepId iterationStep)
    {
        VirtualUserWorker[] workers = new VirtualUserWorker[_options.MaxVirtualUsers];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = CreateWorker(i, iterationStep);
        }

        return workers;
    }

    private VirtualUserWorker CreateWorker(int index, StepId iterationStep)
    {
        long maxDelayTicks = _options.MaxSchedulingDelay is { } delay
            ? SiroccoClock.FromTimeSpan(delay)
            : 0L;

        VirtualUserContext context = new(index, _httpClient, _sink, iterationStep, _customMetricSink);
        return new VirtualUserWorker(context, _workflow, maxDelayTicks, _options.IterationsPerVirtualUser, ActiveVirtualUsers);
    }

    private LoadTestSummary Summarize(IReadOnlyList<VirtualUserWorker> workers, long durationTicks)
    {
        long started = 0L;
        long completed = 0L;
        long failed = 0L;
        long dropped = 0L;
        long emitted = 0L;
        long maxDelay = 0L;
        Exception? firstError = null;

        foreach (VirtualUserWorker worker in workers)
        {
            VirtualUserContext context = worker.Context;

            started += context.IterationsStarted;
            completed += context.IterationsCompleted;
            failed += context.IterationsFailed;
            dropped += context.IterationsDropped;
            emitted += context.MetricsEmitted;
            maxDelay = Math.Max(maxDelay, context.MaxSchedulingDelayTicks);
            firstError ??= context.FirstScenarioError;
        }

        return new LoadTestSummary
        {
            Duration = SiroccoClock.ToTimeSpan(Math.Max(durationTicks, 0L)),
            TokensIssued = _scheduler.TokensIssued,
            TokensPlanned = _scheduler.TokensPlanned,
            IterationsStarted = started,
            IterationsCompleted = completed,
            IterationsFailed = failed,
            IterationsDropped = dropped,
            MetricsEmitted = emitted,
            MetricsDropped = _sink.DroppedMetrics,
            MaxSchedulingDelayTicks = maxDelay,
            FirstScenarioError = firstError,
        };
    }
}