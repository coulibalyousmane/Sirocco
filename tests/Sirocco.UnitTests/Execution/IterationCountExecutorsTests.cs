using System.Collections.Concurrent;
using Sirocco.Application.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.UnitTests.TestDoubles;

namespace Sirocco.UnitTests.Execution;

/// <summary>
/// Verifie les deux executeurs pilotes par un nombre d'iterations au travers de la seule porte
/// d'entree publique qui les exerce, <see cref="TargetRpsLoadEngine"/> — meme convention que
/// <see cref="RampingVirtualUserPoolTests"/> pour <c>VirtualUserWorker</c>, interne au module.
/// </summary>
public sealed class IterationCountExecutorsTests
{
    private static readonly HttpClient _sharedClient = new();

    private static CancellationToken Guard() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [Fact]
    public async Task Shared_iterations_are_split_across_workers_up_to_the_total()
    {
        DelegateWorkflow workflow = DelegateWorkflow.SingleSuccessfulStep();
        IterationCountScheduler scheduler = new(500L);
        TargetRpsLoadEngine engine = new(
            scheduler,
            workflow,
            _sharedClient,
            new CollectingMetricSink(),
            new LoadTestOptions { MaxVirtualUsers = 16 },
            new StepRegistry());

        var summary = await engine.RunAsync(Guard());

        Assert.Equal(500, summary.TokensPlanned);
        Assert.Equal(500, summary.TokensIssued);
        Assert.Equal(500, summary.IterationsStarted);
        Assert.Equal(500, summary.IterationsCompleted);
        Assert.False(summary.InjectorFellBehind);
    }

    /// <summary>
    /// Le coeur de l'executeur "iterations par utilisateur" : chaque utilisateur virtuel doit en
    /// faire exactement sa part, jamais plus (il s'arrete de lui-meme), jamais moins (le total
    /// emis par l'ordonnanceur suffit exactement a couvrir tout le monde) — pas une repartition
    /// inegale au gre de qui repond le plus vite au canal partage.
    /// </summary>
    [Fact]
    public async Task Each_virtual_user_runs_exactly_its_own_quota_independently_of_the_others()
    {
        const int VIRTUAL_USERS = 8;
        const int ITERATIONS_PER_USER = 15;

        ConcurrentDictionary<int, int> perUserCount = new();

        DelegateWorkflow workflow = new((self, context, _) =>
        {
            perUserCount.AddOrUpdate(context.VirtualUserId, 1, static (_, count) => count + 1);
            context.BeginStep(self.Step).Success();
            return ValueTask.CompletedTask;
        });

        IterationCountScheduler scheduler = new(VIRTUAL_USERS * ITERATIONS_PER_USER);
        TargetRpsLoadEngine engine = new(
            scheduler,
            workflow,
            _sharedClient,
            new CollectingMetricSink(),
            new LoadTestOptions { MaxVirtualUsers = VIRTUAL_USERS, IterationsPerVirtualUser = ITERATIONS_PER_USER },
            new StepRegistry());

        var summary = await engine.RunAsync(Guard());

        Assert.Equal(VIRTUAL_USERS * ITERATIONS_PER_USER, summary.IterationsCompleted);
        Assert.Equal(VIRTUAL_USERS, perUserCount.Count);
        Assert.All(perUserCount.Values, count => Assert.Equal(ITERATIONS_PER_USER, count));
    }

    [Fact]
    public async Task A_scenario_that_always_throws_still_counts_toward_the_personal_quota()
    {
        const int VIRTUAL_USERS = 4;
        const int ITERATIONS_PER_USER = 10;

        DelegateWorkflow workflow = DelegateWorkflow.AlwaysThrows("cible injoignable");
        IterationCountScheduler scheduler = new(VIRTUAL_USERS * ITERATIONS_PER_USER);
        TargetRpsLoadEngine engine = new(
            scheduler,
            workflow,
            _sharedClient,
            new CollectingMetricSink(),
            new LoadTestOptions { MaxVirtualUsers = VIRTUAL_USERS, IterationsPerVirtualUser = ITERATIONS_PER_USER },
            new StepRegistry());

        var summary = await engine.RunAsync(Guard());

        // Un echec consomme quand meme une iteration du quota : sans quoi un scenario qui
        // echoue systematiquement ferait tourner un utilisateur virtuel indefiniment.
        Assert.Equal(VIRTUAL_USERS * ITERATIONS_PER_USER, summary.IterationsFailed);
        Assert.Equal(0, summary.IterationsCompleted);
    }

    [Fact]
    public async Task Cancellation_ends_an_iteration_count_run_promptly()
    {
        DelegateWorkflow workflow = DelegateWorkflow.Slow(TimeSpan.FromMilliseconds(50));
        IterationCountScheduler scheduler = new(1_000_000L);
        TargetRpsLoadEngine engine = new(
            scheduler,
            workflow,
            _sharedClient,
            new CollectingMetricSink(),
            new LoadTestOptions { MaxVirtualUsers = 4 },
            new StepRegistry());

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
        var summary = await engine.RunAsync(cts.Token);

        Assert.True(summary.Duration < TimeSpan.FromSeconds(10), $"Arret trop lent : {summary.Duration}.");
        Assert.True(summary.InjectorFellBehind);
        Assert.Equal(1, workflow.TearDownCalls);
    }
}