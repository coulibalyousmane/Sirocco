using Sirocco.Application.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Host.Execution;
using Sirocco.UnitTests.TestDoubles;

namespace Sirocco.UnitTests.Host;

/// <summary>
/// Verifie la garantie centrale d'un tir a scenarios concurrents : chaque scenario tourne sur sa
/// propre chaine de mesure, isolee de celle des autres, jusqu'au nom de ses etapes — deux
/// scenarios qui declarent tous les deux une etape "step" (voir <see cref="DelegateWorkflow.DEFAULT_STEP_NAME"/>)
/// ne doivent jamais voir leurs mesures fusionnees en une seule ligne.
/// </summary>
public sealed class MultiScenarioRunnerTests
{
    private static readonly HttpClient _sharedClient = new();

    private static CancellationToken Guard() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static ScenarioRunSpec Spec(
        string name,
        long iterations,
        int maxVirtualUsers = 4,
        IReadOnlyList<ThresholdRule>? thresholds = null,
        bool alwaysFails = false,
        bool isClosedModel = false) =>
        new()
        {
            Name = name,
            Workflow = alwaysFails ? DelegateWorkflow.AlwaysThrows() : DelegateWorkflow.SingleSuccessfulStep(),
            Scheduler = new IterationCountScheduler(iterations),
            HttpClient = _sharedClient,
            Options = new LoadTestOptions { MaxVirtualUsers = maxVirtualUsers },
            Thresholds = thresholds ?? [],
            IsClosedModel = isClosedModel,
        };

    [Fact]
    public async Task Two_scenarios_with_identically_named_steps_do_not_collide()
    {
        ScenarioRunSpec checkout = Spec("checkout", iterations: 30L);
        ScenarioRunSpec browse = Spec("browse", iterations: 50L);

        MultiScenarioReport report = await MultiScenarioRunner.RunAsync([checkout, browse], Guard());

        Assert.Equal(2, report.Scenarios.Count);

        ScenarioReport checkoutReport = report.Scenarios.First(scenario => scenario.Name == "checkout");
        ScenarioReport browseReport = report.Scenarios.First(scenario => scenario.Name == "browse");

        Assert.Equal(30, checkoutReport.Report.Iteration.Count);
        Assert.Equal(50, browseReport.Report.Iteration.Count);

        // Meme nom d'etape (DelegateWorkflow.DEFAULT_STEP_NAME) dans les deux scenarios : chacun
        // garde son propre compte, jamais fusionne en une seule ligne de 80.
        Assert.Equal(30, checkoutReport.Report.Steps.First(step => step.Name == DelegateWorkflow.DEFAULT_STEP_NAME).Count);
        Assert.Equal(50, browseReport.Report.Steps.First(step => step.Name == DelegateWorkflow.DEFAULT_STEP_NAME).Count);
    }

    [Fact]
    public async Task Each_scenario_evaluates_only_its_own_thresholds()
    {
        // OnIteration, pas OnStep : un scenario qui leve (AlwaysThrows) ne rapporte jamais sa
        // propre etape nommee (l'exception survient avant le premier BeginStep, voir
        // VirtualUserWorker.ExecuteIterationAsync) — seule l'etape technique d'iteration
        // (WellKnownSteps.ITERATION) compte alors l'echec.
        ThresholdRule rule = ThresholdRule.OnIteration(ThresholdMetric.ErrorRate, ThresholdComparison.LessThanOrEqual, 0.0);

        ScenarioRunSpec passing = Spec("passing", iterations: 10L, thresholds: [rule]);
        ScenarioRunSpec failing = Spec("failing", iterations: 10L, thresholds: [rule], alwaysFails: true);

        MultiScenarioReport report = await MultiScenarioRunner.RunAsync([passing, failing], Guard());

        Assert.False(report.ThresholdsPassed);
        Assert.True(report.Scenarios.First(scenario => scenario.Name == "passing").Thresholds.Passed);
        Assert.False(report.Scenarios.First(scenario => scenario.Name == "failing").Thresholds.Passed);
    }

    [Fact]
    public async Task A_scenario_without_thresholds_does_not_affect_another_scenarios_verdict()
    {
        ThresholdRule failingRule = ThresholdRule.OnIteration(ThresholdMetric.ErrorRate, ThresholdComparison.LessThanOrEqual, 0.0);

        ScenarioRunSpec withoutThresholds = Spec("no-gate", iterations: 10L, alwaysFails: true);
        ScenarioRunSpec withFailingThreshold = Spec("gated", iterations: 10L, thresholds: [failingRule], alwaysFails: true);

        MultiScenarioReport report = await MultiScenarioRunner.RunAsync([withoutThresholds, withFailingThreshold], Guard());

        Assert.True(report.Scenarios.First(scenario => scenario.Name == "no-gate").Thresholds.Passed);
        Assert.False(report.Scenarios.First(scenario => scenario.Name == "gated").Thresholds.Passed);
        Assert.False(report.ThresholdsPassed);
    }

    [Fact]
    public async Task IsClosedModel_is_reported_independently_per_scenario()
    {
        ScenarioRunSpec closed = Spec("closed", iterations: 5L, isClosedModel: true);
        ScenarioRunSpec open = Spec("open", iterations: 5L, isClosedModel: false);

        MultiScenarioReport report = await MultiScenarioRunner.RunAsync([closed, open], Guard());

        Assert.True(report.Scenarios.First(scenario => scenario.Name == "closed").Report.ClosedModel);
        Assert.False(report.Scenarios.First(scenario => scenario.Name == "open").Report.ClosedModel);
    }
}