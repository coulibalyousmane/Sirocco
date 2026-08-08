using Tempest.Application.Execution;
using Tempest.Domain.Metrics;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.Execution;

/// <summary>
/// Tests deterministes du contexte : aucun horodatage reel n'est compare, seules les
/// regles d'imputation de la dette d'ordonnancement sont verifiees.
/// </summary>
public sealed class VirtualUserContextTests
{
    private static readonly HttpClient _sharedClient = new();
    private static readonly StepId _iterationStep = new(0);
    private static readonly StepId _firstStep = new(1);
    private static readonly StepId _secondStep = new(2);

    private static VirtualUserContext CreateContext(CollectingMetricSink sink, int virtualUserId = 3) =>
        new(virtualUserId, _sharedClient, sink, _iterationStep);

    [Fact]
    public void The_first_step_of_an_iteration_inherits_the_scheduled_instant()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink);
        ExecutionToken token = new(IterationIndex: 7, ScheduledTicks: 1_000);

        context.BeginIteration(in token, startedTicks: 5_000, CancellationToken.None);
        var scope = context.BeginStep(_firstStep);

        // La dette accumulee par l'injecteur pese sur la premiere etape.
        Assert.Equal(1_000, scope.ScheduledTicks);
        Assert.True(scope.StartedTicks > scope.ScheduledTicks);
    }

    [Fact]
    public void Later_steps_do_not_inherit_the_debt_a_second_time()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink);
        ExecutionToken token = new(IterationIndex: 7, ScheduledTicks: 1_000);

        context.BeginIteration(in token, startedTicks: 5_000, CancellationToken.None);
        context.BeginStep(_firstStep).Success();
        var second = context.BeginStep(_secondStep);

        // La deuxieme etape n'a attendu que la reponse precedente, pas la file de l'injecteur.
        Assert.Equal(second.StartedTicks, second.ScheduledTicks);
    }

    [Fact]
    public void Each_iteration_resets_the_first_step_rule()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink);

        ExecutionToken first = new(0, 1_000);
        context.BeginIteration(in first, 5_000, CancellationToken.None);
        context.BeginStep(_firstStep).Success();
        context.BeginStep(_secondStep).Success();

        ExecutionToken second = new(1, 9_000);
        context.BeginIteration(in second, 12_000, CancellationToken.None);
        var scope = context.BeginStep(_firstStep);

        Assert.Equal(9_000, scope.ScheduledTicks);
    }

    [Fact]
    public void Ending_an_iteration_publishes_the_end_to_end_metric()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink, virtualUserId: 11);
        ExecutionToken token = new(IterationIndex: 4, ScheduledTicks: 1_000);

        context.BeginIteration(in token, startedTicks: 3_000, CancellationToken.None);
        context.EndIteration(startedTicks: 3_000, RequestOutcome.Success);

        var metric = Assert.Single(sink.For(_iterationStep));
        Assert.Equal(1_000, metric.ScheduledTicks);
        Assert.Equal(3_000, metric.StartedTicks);
        Assert.Equal(11, metric.VirtualUserId);
        Assert.Equal(RequestOutcome.Success, metric.Outcome);
        Assert.Equal(1, context.IterationsCompleted);
        Assert.Equal(0, context.IterationsFailed);
    }

    [Fact]
    public void A_failed_iteration_counts_as_a_failure_not_a_completion()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink);
        ExecutionToken token = new(0, 1_000);

        context.BeginIteration(in token, 1_000, CancellationToken.None);
        context.EndIteration(1_000, RequestOutcome.ScenarioError);

        Assert.Equal(0, context.IterationsCompleted);
        Assert.Equal(1, context.IterationsFailed);
    }

    /// <summary>
    /// Un scenario n'a pas a lever d'exception quand une etape HTTP echoue : un code 500
    /// n'est pas un bug du scenario. Mais si l'iteration se termine normalement malgre
    /// une etape en echec, le rapport ne doit pas afficher un succes.
    /// </summary>
    [Fact]
    public void A_step_failure_makes_the_iteration_fail_even_if_the_scenario_returns_normally()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink);
        ExecutionToken token = new(0, 1_000);

        context.BeginIteration(in token, 1_000, CancellationToken.None);
        context.BeginStep(_firstStep).CompleteHttp(statusCode: 500);
        context.BeginStep(_secondStep).Success();

        // Le scenario n'a pas leve : l'appelant transmet Success, comme le ferait le moteur.
        context.EndIteration(1_000, RequestOutcome.Success);

        var metric = Assert.Single(sink.For(_iterationStep));
        Assert.Equal(RequestOutcome.HttpError, metric.Outcome);
        Assert.Equal(0, context.IterationsCompleted);
        Assert.Equal(1, context.IterationsFailed);
    }

    [Fact]
    public void Only_the_first_step_failure_of_an_iteration_is_kept()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink);
        ExecutionToken token = new(0, 1_000);

        context.BeginIteration(in token, 1_000, CancellationToken.None);
        context.BeginStep(_firstStep).CompleteHttp(statusCode: 500);
        context.BeginStep(_secondStep).Fail(RequestOutcome.Timeout);
        context.EndIteration(1_000, RequestOutcome.Success);

        var metric = Assert.Single(sink.For(_iterationStep));
        Assert.Equal(RequestOutcome.HttpError, metric.Outcome);
    }

    [Fact]
    public void A_step_failure_does_not_leak_into_the_next_iteration()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink);

        ExecutionToken first = new(0, 1_000);
        context.BeginIteration(in first, 1_000, CancellationToken.None);
        context.BeginStep(_firstStep).CompleteHttp(statusCode: 500);
        context.EndIteration(1_000, RequestOutcome.Success);

        ExecutionToken second = new(1, 2_000);
        context.BeginIteration(in second, 2_000, CancellationToken.None);
        context.BeginStep(_firstStep).Success();
        context.EndIteration(2_000, RequestOutcome.Success);

        Assert.Equal(1, context.IterationsCompleted);
        Assert.Equal(1, context.IterationsFailed);
    }

    [Fact]
    public void The_scheduling_debt_high_water_mark_is_tracked()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink);

        ExecutionToken small = new(0, 1_000);
        context.BeginIteration(in small, 1_500, CancellationToken.None);

        ExecutionToken large = new(1, 2_000);
        context.BeginIteration(in large, 12_000, CancellationToken.None);

        ExecutionToken smallAgain = new(2, 3_000);
        context.BeginIteration(in smallAgain, 3_100, CancellationToken.None);

        Assert.Equal(10_000, context.MaxSchedulingDelayTicks);
        Assert.Equal(3, context.IterationsStarted);
    }

    [Fact]
    public void A_dropped_token_is_still_measured()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink, virtualUserId: 2);
        ExecutionToken token = new(IterationIndex: 9, ScheduledTicks: 1_000);

        context.RecordDropped(in token, detectedAtTicks: 8_000);

        var metric = Assert.Single(sink.For(_iterationStep));
        Assert.Equal(RequestOutcome.Dropped, metric.Outcome);
        Assert.Equal(7_000, metric.ResponseTicks);
        Assert.Equal(0, metric.ServiceTicks);
        Assert.Equal(1, context.IterationsDropped);
        Assert.Equal(7_000, context.MaxSchedulingDelayTicks);
    }

    [Fact]
    public void Only_the_first_scenario_error_is_kept()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink);

        context.RecordScenarioError(new InvalidOperationException("premier"));
        context.RecordScenarioError(new InvalidOperationException("second"));

        Assert.Equal("premier", context.FirstScenarioError?.Message);
    }

    [Fact]
    public void RecordCustomMetric_forwards_to_the_custom_metric_sink()
    {
        CollectingMetricSink sink = new();
        CollectingCustomMetricSink customMetricSink = new();
        VirtualUserContext context = new(3, _sharedClient, sink, _iterationStep, customMetricSink);
        CustomMetricId ordersTotal = new(0);

        context.RecordCustomMetric(ordersTotal, 42d);

        Assert.Equal([42d], customMetricSink.ValuesFor(ordersTotal));
    }

    [Fact]
    public void RecordCustomMetric_without_a_sink_does_not_throw()
    {
        CollectingMetricSink sink = new();
        VirtualUserContext context = CreateContext(sink);

        Exception? escaped = Record.Exception(() => context.RecordCustomMetric(new CustomMetricId(0), 1d));

        Assert.Null(escaped);
    }

    [Fact]
    public void State_survives_across_iterations_of_the_same_virtual_user()
    {
        CollectingMetricSink sink = new();
        var context = CreateContext(sink);

        ExecutionToken first = new(0, 0);
        context.BeginIteration(in first, 0, CancellationToken.None);
        context.State = "jeton-auth";

        ExecutionToken second = new(1, 100);
        context.BeginIteration(in second, 100, CancellationToken.None);

        Assert.Equal("jeton-auth", context.State);
    }
}