using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class ClusterReportAggregatorTests
{
    private static readonly int _outcomeCount = Enum.GetValues<RequestOutcome>().Length;

    private static WorkerStepReport StepReport(string name, IEnumerable<long> values, RequestOutcome outcome = RequestOutcome.Success)
    {
        LatencyHistogram response = new();
        LatencyHistogram service = new();
        long[] countByOutcome = new long[_outcomeCount];
        long bytesReceived = 0L;

        foreach (long value in values)
        {
            response.Record(value);
            service.Record(value);
            countByOutcome[(int)outcome]++;
            bytesReceived += value;
        }

        return new WorkerStepReport(name, countByOutcome, bytesReceived, MaxSchedulingDelayMicroseconds: 0L, response.Export(), service.Export());
    }

    [Fact]
    public void Merging_requires_at_least_one_worker() =>
        Assert.Throws<ArgumentException>(() => ClusterReportAggregator.Merge([], TimeSpan.Zero));

    /// <summary>
    /// La propriete qui compte le plus : fusionner les rapports bruts de N workers doit donner
    /// exactement les memes centiles que si toutes les mesures avaient ete enregistrees dans un
    /// seul histogramme — sans quoi le mode distribue mentirait sur ses propres resultats.
    /// </summary>
    [Fact]
    public void Merged_percentiles_match_recording_every_value_into_a_single_histogram()
    {
        LatencyHistogram reference = new();
        List<long> workerAValues = [];
        List<long> workerBValues = [];
        List<long> workerCValues = [];

        for (long value = 1L; value <= 9_000L; value++)
        {
            reference.Record(value);
            ((value % 3L) switch { 0L => workerAValues, 1L => workerBValues, _ => workerCValues }).Add(value);
        }

        WorkerReport workerA = new("worker-a", MetricsDropped: 0L, [StepReport("ping", workerAValues)]);
        WorkerReport workerB = new("worker-b", MetricsDropped: 0L, [StepReport("ping", workerBValues)]);
        WorkerReport workerC = new("worker-c", MetricsDropped: 0L, [StepReport("ping", workerCValues)]);

        LoadTestReport merged = ClusterReportAggregator.Merge([workerA, workerB, workerC], TimeSpan.FromSeconds(10));

        StepStatistics pingStats = Assert.Single(merged.Steps);
        Assert.Equal(reference.Snapshot(), pingStats.Response);
        Assert.Equal(reference.Snapshot(), pingStats.Service);
    }

    [Fact]
    public void Counts_bytes_and_dropped_metrics_are_summed_across_workers()
    {
        WorkerReport workerA = new("worker-a", MetricsDropped: 2L, [StepReport("ping", [10L, 20L])]);
        WorkerReport workerB = new("worker-b", MetricsDropped: 3L, [StepReport("ping", [30L])]);

        LoadTestReport merged = ClusterReportAggregator.Merge([workerA, workerB], TimeSpan.FromSeconds(1));

        StepStatistics pingStats = Assert.Single(merged.Steps);
        Assert.Equal(3L, pingStats.Count);
        Assert.Equal(3L, pingStats.SuccessCount);
        Assert.Equal(60L, pingStats.BytesReceived);
        Assert.Equal(5L, merged.MetricsDropped);
    }

    [Fact]
    public void Failures_are_reflected_in_the_merged_error_rate()
    {
        WorkerReport workerA = new("worker-a", MetricsDropped: 0L, [StepReport("ping", [10L], RequestOutcome.HttpError)]);
        WorkerReport workerB = new("worker-b", MetricsDropped: 0L, [StepReport("ping", [20L, 30L])]);

        LoadTestReport merged = ClusterReportAggregator.Merge([workerA, workerB], TimeSpan.FromSeconds(1));

        StepStatistics pingStats = Assert.Single(merged.Steps);
        Assert.Equal(3L, pingStats.Count);
        Assert.Equal(2L, pingStats.SuccessCount);
        Assert.Equal(1L, pingStats.FailureCount);
    }

    [Fact]
    public void The_iteration_step_is_identified_by_its_well_known_name()
    {
        WorkerReport worker = new(
            "worker-a",
            MetricsDropped: 0L,
            [StepReport(WellKnownSteps.ITERATION, [100L]), StepReport("ping", [10L])]);

        LoadTestReport merged = ClusterReportAggregator.Merge([worker], TimeSpan.FromSeconds(1));

        Assert.Equal(WellKnownSteps.ITERATION, merged.Iteration.Name);
        Assert.Equal(1L, merged.Iteration.Count);
        Assert.Equal(2, merged.Steps.Count);
    }

    [Fact]
    public void The_reported_duration_is_the_one_supplied_by_the_caller_not_derived_from_workers()
    {
        WorkerReport worker = new("worker-a", MetricsDropped: 0L, [StepReport("ping", [10L])]);

        LoadTestReport merged = ClusterReportAggregator.Merge([worker], TimeSpan.FromSeconds(42));

        Assert.Equal(TimeSpan.FromSeconds(42), merged.Duration);
        Assert.Equal(StatisticsScope.Cumulative, merged.Scope);
    }
}