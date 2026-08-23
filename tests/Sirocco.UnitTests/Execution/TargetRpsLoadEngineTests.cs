using Sirocco.Application.Execution;
using Sirocco.Domain.Load;
using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;
using Sirocco.UnitTests.TestDoubles;

namespace Sirocco.UnitTests.Execution;

public sealed class TargetRpsLoadEngineTests
{
    private const int DEFAULT_TEST_VIRTUAL_USERS = 32;

    private static readonly HttpClient _sharedClient = new();

    /// <summary>
    /// Filet de securite : un blocage du moteur doit faire echouer le test, pas figer
    /// la suite. Aucun tir de ce fichier ne depasse la seconde en fonctionnement normal.
    /// </summary>
    private static CancellationToken Guard() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static TargetRpsLoadEngine CreateEngine(
        ILoadScheduler scheduler,
        DelegateWorkflow workflow,
        IMetricSink sink,
        int maxVirtualUsers = DEFAULT_TEST_VIRTUAL_USERS,
        TimeSpan? maxSchedulingDelay = null) =>
        new(
            scheduler,
            workflow,
            _sharedClient,
            sink,
            new LoadTestOptions
            {
                MaxVirtualUsers = maxVirtualUsers,
                MaxSchedulingDelay = maxSchedulingDelay,
            },
            new StepRegistry());

    private static TargetRpsLoadEngine CreateEngine(
        DelegateWorkflow workflow,
        IMetricSink sink,
        LoadProfile profile,
        int maxVirtualUsers = DEFAULT_TEST_VIRTUAL_USERS,
        TimeSpan? maxSchedulingDelay = null) =>
        CreateEngine(new CoordinatedRateLimiter(profile), workflow, sink, maxVirtualUsers, maxSchedulingDelay);

    [Fact]
    public async Task Runs_every_iteration_the_profile_plans()
    {
        DelegateWorkflow workflow = DelegateWorkflow.SingleSuccessfulStep();
        CollectingMetricSink sink = new();
        var engine = CreateEngine(workflow, sink, LoadProfile.Constant(200d, TimeSpan.FromMilliseconds(250)));

        var summary = await engine.RunAsync(Guard());

        Assert.Equal(50, summary.TokensPlanned);
        Assert.Equal(50, summary.TokensIssued);
        Assert.Equal(50, summary.IterationsStarted);
        Assert.Equal(50, summary.IterationsCompleted);
        Assert.Equal(0, summary.IterationsFailed);
        Assert.Equal(0, summary.IterationsDropped);
        Assert.False(summary.InjectorFellBehind);
        Assert.False(summary.MetricsAreIncomplete);
    }

    /// <summary>
    /// Le moteur ne depend que de l'abstraction <see cref="ILoadScheduler"/> : on peut donc
    /// verifier sa mecanique avec une cadence deterministe, sans horloge ni test instable.
    /// </summary>
    [Fact]
    public async Task Any_scheduler_drives_the_engine_not_just_the_rate_limiter()
    {
        DelegateWorkflow workflow = DelegateWorkflow.SingleSuccessfulStep();
        CollectingMetricSink sink = new();
        var engine = CreateEngine(new ImmediateScheduler(tokenCount: 250), workflow, sink);

        var summary = await engine.RunAsync(Guard());

        Assert.Equal(250, summary.TokensIssued);
        Assert.Equal(250, summary.IterationsCompleted);
        Assert.Equal(500, summary.MetricsEmitted);
    }

    [Fact]
    public async Task Publishes_one_end_to_end_metric_per_iteration_plus_the_scenario_steps()
    {
        DelegateWorkflow workflow = DelegateWorkflow.SingleSuccessfulStep();
        CollectingMetricSink sink = new();
        var engine = CreateEngine(workflow, sink, LoadProfile.Constant(200d, TimeSpan.FromMilliseconds(250)));

        var summary = await engine.RunAsync(Guard());

        StepId iterationStep = engine.Steps.Register(WellKnownSteps.ITERATION);
        Assert.Equal(50, sink.For(iterationStep).Count());
        Assert.Equal(50, sink.For(workflow.Step).Count());
        Assert.Equal(100, summary.MetricsEmitted);
        Assert.All(sink.Results, m => Assert.True(m.IsSuccess));
    }

    [Fact]
    public async Task The_step_registry_is_sealed_once_the_run_has_started()
    {
        DelegateWorkflow workflow = DelegateWorkflow.NoOp();
        var engine = CreateEngine(new ImmediateScheduler(tokenCount: 10), workflow, new CollectingMetricSink());

        await engine.RunAsync(Guard());

        Assert.True(engine.Steps.TryGetId(WellKnownSteps.ITERATION, out _));
        Assert.True(engine.Steps.TryGetId(DelegateWorkflow.DEFAULT_STEP_NAME, out _));
        Assert.Throws<InvalidOperationException>(() => engine.Steps.Register("trop-tard"));
    }

    [Fact]
    public async Task Set_up_and_tear_down_run_exactly_once()
    {
        DelegateWorkflow workflow = DelegateWorkflow.NoOp();
        var engine = CreateEngine(new ImmediateScheduler(tokenCount: 10), workflow, new CollectingMetricSink());

        await engine.RunAsync(Guard());

        Assert.Equal(1, workflow.SetUpCalls);
        Assert.Equal(1, workflow.TearDownCalls);
    }

    [Fact]
    public async Task A_scenario_that_always_throws_neither_stops_nor_silences_the_run()
    {
        DelegateWorkflow workflow = DelegateWorkflow.AlwaysThrows("cible injoignable");
        CollectingMetricSink sink = new();
        var engine = CreateEngine(new ImmediateScheduler(tokenCount: 40), workflow, sink);

        var summary = await engine.RunAsync(Guard());

        Assert.Equal(40, summary.IterationsStarted);
        Assert.Equal(40, summary.IterationsFailed);
        Assert.Equal(0, summary.IterationsCompleted);
        Assert.Equal("cible injoignable", summary.FirstScenarioError?.Message);
        Assert.All(sink.Results, m => Assert.Equal(RequestOutcome.ScenarioError, m.Outcome));
        Assert.Equal(1, workflow.TearDownCalls);
    }

    /// <summary>
    /// Le test qui justifie tout le projet : quand l'injecteur ne peut plus suivre, le retard
    /// doit apparaitre dans les mesures au lieu d'etre absorbe par un ralentissement du tir.
    /// </summary>
    [Fact]
    public async Task A_saturated_injector_reports_its_debt_instead_of_hiding_it()
    {
        // Un seul utilisateur virtuel pour 100 RPS demandes : la saturation est garantie.
        DelegateWorkflow workflow = DelegateWorkflow.Slow(TimeSpan.FromMilliseconds(25));
        CollectingMetricSink sink = new();
        var engine = CreateEngine(
            workflow,
            sink,
            LoadProfile.Constant(100d, TimeSpan.FromMilliseconds(200)),
            maxVirtualUsers: 1);

        var summary = await engine.RunAsync(Guard());

        Assert.Equal(20, summary.IterationsStarted);
        Assert.True(
            summary.MaxSchedulingDelayMilliseconds > 100d,
            $"La dette devrait etre visible, mesuree : {summary.MaxSchedulingDelayMilliseconds:F1} ms.");

        StepId iterationStep = engine.Steps.Register(WellKnownSteps.ITERATION);

        // L'ecart response - service, mesure SUR LA MEME iteration, EST le temps passe en file.
        // C'est la seule formulation insensible a la charge de la machine : une borne absolue sur
        // le temps de service ne tenait pas, car sous contention CPU (la suite tourne en parallele
        // et les tests de tir y saturent des coeurs, PrecisionWait faisant de l'attente active) le
        // Task.Delay(25 ms) du scenario derive au-dela de 100 ms sans que la these change d'un
        // iota. La contention rend d'ailleurs cet ecart PLUS grand, jamais plus petit : elle
        // allonge chaque iteration, donc le retard accumule par les jetons suivants.
        double worstOmission = sink.For(iterationStep).Max(m => m.ResponseMilliseconds - m.ServiceMilliseconds);
        double worstService = sink.For(iterationStep).Max(m => m.ServiceMilliseconds);

        // Le temps de reponse corrige revele les centaines de millisecondes d'attente...
        Assert.True(
            worstOmission > 100d,
            $"Correction absente : l'ecart response/service plafonne a {worstOmission:F1} ms (service {worstService:F1} ms).");

        // ...que le temps de service, la mesure trompeuse, n'absorbe pas : l'attente domine.
        Assert.True(
            worstService < worstOmission,
            $"Temps de service inattendu : {worstService:F1} ms pour une attente de {worstOmission:F1} ms.");
    }

    [Fact]
    public async Task Stale_tokens_are_dropped_when_a_ceiling_is_set_and_still_measured()
    {
        // Jetons antidates d'une seconde : ils sont perimes des leur arrivee.
        DelegateWorkflow workflow = DelegateWorkflow.SingleSuccessfulStep();
        CollectingMetricSink sink = new();
        var engine = CreateEngine(
            new ImmediateScheduler(tokenCount: 30, backdatedTicks: SiroccoClock.FromSeconds(1d)),
            workflow,
            sink,
            maxSchedulingDelay: TimeSpan.FromMilliseconds(50));

        var summary = await engine.RunAsync(Guard());

        Assert.Equal(30, summary.IterationsDropped);
        Assert.Equal(0, summary.IterationsStarted);

        StepId iterationStep = engine.Steps.Register(WellKnownSteps.ITERATION);
        List<MetricResult> dropped = [.. sink.For(iterationStep).Where(m => m.Outcome == RequestOutcome.Dropped)];

        Assert.Equal(30, dropped.Count);

        // Un abandon reste compte dans les percentiles : le sortir du calcul recreerait le biais.
        Assert.All(dropped, m => Assert.True(m.ResponseMilliseconds >= 1_000d));
    }

    [Fact]
    public async Task Cancellation_ends_the_run_promptly()
    {
        DelegateWorkflow workflow = DelegateWorkflow.SingleSuccessfulStep();
        var engine = CreateEngine(
            workflow,
            new CollectingMetricSink(),
            LoadProfile.Constant(100d, TimeSpan.FromSeconds(60)));

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));
        var summary = await engine.RunAsync(cts.Token);

        Assert.True(summary.Duration < TimeSpan.FromSeconds(10), $"Arret trop lent : {summary.Duration}.");
        Assert.True(summary.InjectorFellBehind);
        Assert.True(summary.IterationsStarted < summary.TokensPlanned);
        Assert.Equal(1, workflow.TearDownCalls);
    }

    [Fact]
    public void Invalid_options_are_rejected_at_construction()
    {
        Assert.Throws<ArgumentException>(() => CreateEngine(
            new ImmediateScheduler(tokenCount: 1),
            DelegateWorkflow.NoOp(),
            new CollectingMetricSink(),
            maxVirtualUsers: 0));

        Assert.Throws<ArgumentException>(() => CreateEngine(
            new ImmediateScheduler(tokenCount: 1),
            DelegateWorkflow.NoOp(),
            new CollectingMetricSink(),
            maxSchedulingDelay: TimeSpan.Zero));
    }

    [Fact]
    public void Missing_collaborators_are_rejected_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() => new TargetRpsLoadEngine(
            null!,
            DelegateWorkflow.NoOp(),
            _sharedClient,
            new CollectingMetricSink(),
            new LoadTestOptions(),
            new StepRegistry()));

        Assert.Throws<ArgumentNullException>(() => new TargetRpsLoadEngine(
            new ImmediateScheduler(tokenCount: 1),
            DelegateWorkflow.NoOp(),
            _sharedClient,
            new CollectingMetricSink(),
            new LoadTestOptions(),
            null!));
    }
}