using Tempest.Application.Execution;
using Tempest.Domain.Load;
using Tempest.Domain.Metrics;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.Execution;

/// <summary>
/// <c>RampingVirtualUserPool</c> est interne : ces tests le verifient au travers de la seule
/// porte d'entree publique qui l'exerce, <see cref="TargetRpsLoadEngine"/> configure avec
/// <see cref="LoadTestOptions.RampProfile"/> — meme convention que <c>VirtualUserWorker</c> ou
/// <c>BlockingTokenWriter</c>, jamais testes hors de leur moteur.
/// </summary>
public sealed class RampingVirtualUserPoolTests
{
    private static readonly HttpClient _sharedClient = new();

    private static CancellationToken Guard() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static TargetRpsLoadEngine CreateRampingEngine(DelegateWorkflow workflow, VirtualUserProfile rampProfile)
    {
        ClosedModelScheduler scheduler = new(rampProfile.TotalDuration);
        return new TargetRpsLoadEngine(
            scheduler,
            workflow,
            _sharedClient,
            new CollectingMetricSink(),
            new LoadTestOptions { RampProfile = rampProfile },
            new StepRegistry());
    }

    [Fact]
    public async Task Concurrency_follows_the_ramp_instead_of_staying_fixed()
    {
        int active = 0;
        int peakObserved = 0;
        object gate = new();

        DelegateWorkflow workflow = new(async (self, context, cancellationToken) =>
        {
            var scope = context.BeginStep(self.Step);
            int current = Interlocked.Increment(ref active);
            lock (gate)
            {
                peakObserved = Math.Max(peakObserved, current);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }

            scope.Success();
        });

        VirtualUserProfile rampProfile = new([
            VirtualUserStage.Ramp(0, 20, TimeSpan.FromMilliseconds(400)),
            VirtualUserStage.Ramp(20, 0, TimeSpan.FromMilliseconds(400)),
        ]);

        var summary = await CreateRampingEngine(workflow, rampProfile).RunAsync(Guard());

        Assert.True(peakObserved > 5, $"Concurrence trop faible pour une rampe qui monte a 20 : {peakObserved}.");
        Assert.True(peakObserved <= 22, $"Concurrence au-dela du pic attendu (20 + marge) : {peakObserved}.");
        Assert.True(summary.IterationsCompleted > 0);

        // Une descente d'utilisateurs interrompt des iterations en cours : comptees en echec
        // (Cancelled, voir VirtualUserContext.EndIteration), exactement comme le modele ouvert
        // annule (voir TargetRpsLoadEngineTests.Cancellation_ends_the_run_promptly, qui ne verifie
        // pas non plus IterationsFailed). Seul l'effectif final compte ici.
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task All_workers_are_stopped_by_the_time_the_run_returns()
    {
        DelegateWorkflow workflow = DelegateWorkflow.SingleSuccessfulStep();
        VirtualUserProfile rampProfile = new([VirtualUserStage.Ramp(0, 10, TimeSpan.FromMilliseconds(300))]);

        var summary = await CreateRampingEngine(workflow, rampProfile).RunAsync(Guard());

        Assert.True(summary.IterationsCompleted > 0);
        Assert.Equal(0, summary.IterationsFailed);
    }

    [Fact]
    public async Task Cancellation_ends_a_ramping_run_promptly()
    {
        DelegateWorkflow workflow = DelegateWorkflow.Slow(TimeSpan.FromMilliseconds(50));
        VirtualUserProfile rampProfile = new([VirtualUserStage.Ramp(0, 10, TimeSpan.FromSeconds(30))]);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
        var summary = await CreateRampingEngine(workflow, rampProfile).RunAsync(cts.Token);

        Assert.True(summary.Duration < TimeSpan.FromSeconds(10), $"Arret trop lent : {summary.Duration}.");
        Assert.Equal(1, workflow.TearDownCalls);
    }

    [Fact]
    public async Task A_scenario_that_always_throws_neither_stops_nor_silences_a_ramping_run()
    {
        DelegateWorkflow workflow = DelegateWorkflow.AlwaysThrows("cible injoignable");
        VirtualUserProfile rampProfile = new([VirtualUserStage.Ramp(0, 5, TimeSpan.FromMilliseconds(200))]);

        var summary = await CreateRampingEngine(workflow, rampProfile).RunAsync(Guard());

        Assert.True(summary.IterationsFailed > 0);
        Assert.Equal(0, summary.IterationsCompleted);
        Assert.Equal("cible injoignable", summary.FirstScenarioError?.Message);
        Assert.Equal(1, workflow.TearDownCalls);
    }
}