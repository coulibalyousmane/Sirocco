using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class ThresholdReportTests
{
    // Comme le fait le vrai MetricsAggregator.Snapshot, l'etape d'iteration figure aussi
    // dans Steps : AlwaysPasses() cible cette etape et doit donc l'y trouver.
    private static LoadTestReport CreateEmptyReport()
    {
        StepStatistics iteration = StepStatistics.Empty(new StepId(0), WellKnownSteps.ITERATION);

        return new LoadTestReport
        {
            Scope = StatisticsScope.Cumulative,
            Duration = TimeSpan.FromSeconds(10),
            Steps = [iteration],
            Iteration = iteration,
            MetricsDropped = 0L,
        };
    }

    private static ThresholdRule AlwaysPasses() =>
        ThresholdRule.OnIteration(ThresholdMetric.Count, ThresholdComparison.GreaterThanOrEqual, 0d);

    private static ThresholdRule AlwaysFails() =>
        ThresholdRule.OnStep("etape-inexistante", ThresholdMetric.Count, ThresholdComparison.GreaterThanOrEqual, 0d);

    [Fact]
    public void An_empty_rule_set_passes_vacuously()
    {
        ThresholdReport report = ThresholdReport.Evaluate([], CreateEmptyReport());

        Assert.True(report.Passed);
        Assert.Empty(report.Evaluations);
        Assert.Equal("Aucun seuil configure.", report.ToTable());
    }

    [Fact]
    public void The_verdict_passes_only_when_every_rule_passes()
    {
        LoadTestReport loadReport = new()
        {
            Scope = StatisticsScope.Cumulative,
            Duration = TimeSpan.FromSeconds(10),
            Steps = [StepStatistics.Empty(new StepId(0), WellKnownSteps.ITERATION)],
            Iteration = StepStatistics.Empty(new StepId(0), WellKnownSteps.ITERATION),
            MetricsDropped = 0L,
        };

        ThresholdReport allPassing = ThresholdReport.Evaluate([AlwaysPasses(), AlwaysPasses()], loadReport);
        Assert.True(allPassing.Passed);

        ThresholdReport onePassingOneFailing = ThresholdReport.Evaluate([AlwaysPasses(), AlwaysFails()], loadReport);
        Assert.False(onePassingOneFailing.Passed);
    }

    [Fact]
    public void Failures_lists_only_the_rules_that_did_not_pass()
    {
        LoadTestReport loadReport = CreateEmptyReport();
        ThresholdRule failing = AlwaysFails();

        ThresholdReport report = ThresholdReport.Evaluate([AlwaysPasses(), failing], loadReport);

        ThresholdEvaluation onlyFailure = Assert.Single(report.Failures);
        Assert.Same(failing, onlyFailure.Rule);
    }

    [Fact]
    public void ToTable_reports_every_rule_in_order()
    {
        ThresholdReport report = ThresholdReport.Evaluate([AlwaysPasses(), AlwaysFails()], CreateEmptyReport());

        string table = report.ToTable();

        Assert.Contains("au moins un echec", table, StringComparison.Ordinal);
        Assert.Equal(2, table.Split('\n').Count(line => line.Contains('[', StringComparison.Ordinal)));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ThresholdReport.Evaluate(null!, CreateEmptyReport()));
        Assert.Throws<ArgumentNullException>(() => ThresholdReport.Evaluate([], null!));
    }
}