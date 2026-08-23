using System.Runtime.CompilerServices;
using Sirocco.Domain.Metrics;

namespace Sirocco.UnitTests.Metrics;

public sealed class MetricResultTests
{
    /// <summary>
    /// Garde-fou du contrat "zero-allocation" : si un champ reference (string, objet)
    /// est ajoute a <see cref="MetricResult"/>, chaque element du buffer de metriques
    /// devient scannable par le GC et le chemin critique s'effondre sous charge.
    /// </summary>
    [Fact]
    public void MetricResult_contains_no_managed_reference()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<MetricResult>());
    }

    [Fact]
    public void MetricResult_stays_within_its_size_budget()
    {
        Assert.True(
            Unsafe.SizeOf<MetricResult>() <= 48,
            $"MetricResult occupe {Unsafe.SizeOf<MetricResult>()} octets (budget : 48).");
    }

    [Fact]
    public void Service_time_ignores_the_scheduling_debt()
    {
        MetricResult metric = new(
            Step: new StepId(0),
            VirtualUserId: 7,
            ScheduledTicks: 1_000,
            StartedTicks: 1_500,
            CompletedTicks: 1_900,
            StatusCode: 200,
            Outcome: RequestOutcome.Success,
            BytesReceived: 512);

        Assert.Equal(400, metric.ServiceTicks);
    }

    [Fact]
    public void Response_time_absorbs_the_scheduling_debt()
    {
        // L'injecteur est parti 500 ticks en retard : c'est du temps subi par l'utilisateur.
        MetricResult metric = new(
            Step: new StepId(0),
            VirtualUserId: 7,
            ScheduledTicks: 1_000,
            StartedTicks: 1_500,
            CompletedTicks: 1_900,
            StatusCode: 200,
            Outcome: RequestOutcome.Success,
            BytesReceived: 512);

        Assert.Equal(900, metric.ResponseTicks);
        Assert.Equal(500, metric.SchedulingDelayTicks);
        Assert.Equal(metric.ServiceTicks + metric.SchedulingDelayTicks, metric.ResponseTicks);
    }

    [Fact]
    public void An_on_time_request_has_no_scheduling_debt()
    {
        MetricResult metric = new(
            new StepId(1), 0, ScheduledTicks: 500, StartedTicks: 500, CompletedTicks: 800,
            StatusCode: 204, RequestOutcome.Success, BytesReceived: 0);

        Assert.Equal(0, metric.SchedulingDelayTicks);
        Assert.Equal(metric.ServiceTicks, metric.ResponseTicks);
        Assert.True(metric.IsSuccess);
    }

    [Theory]
    [InlineData(RequestOutcome.Success, true)]
    [InlineData(RequestOutcome.HttpError, false)]
    [InlineData(RequestOutcome.AssertionFailed, false)]
    [InlineData(RequestOutcome.Timeout, false)]
    [InlineData(RequestOutcome.Dropped, false)]
    public void IsSuccess_only_covers_the_success_outcome(RequestOutcome outcome, bool expected)
    {
        MetricResult metric = new(new StepId(0), 0, 0, 0, 0, 0, outcome, 0);

        Assert.Equal(expected, metric.IsSuccess);
    }

    [Fact]
    public void A_dropped_iteration_still_reports_the_time_it_owed()
    {
        MetricResult metric = MetricResult.Dropped(new StepId(2), virtualUserId: 3, scheduledTicks: 1_000, detectedAtTicks: 4_000);

        Assert.Equal(RequestOutcome.Dropped, metric.Outcome);
        Assert.Equal(0, metric.ServiceTicks);
        Assert.Equal(3_000, metric.ResponseTicks);
        Assert.Equal(3_000, metric.SchedulingDelayTicks);
    }
}