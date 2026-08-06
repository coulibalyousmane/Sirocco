using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class LoadTestReportTests
{
    private static StepStatistics CreateStep(string name, long count, long p95Microseconds, long errorCount = 0L)
    {
        LatencySnapshot latency = new(
            Count: count,
            MinMicroseconds: 1_000L,
            MaxMicroseconds: p95Microseconds * 2,
            MeanMicroseconds: p95Microseconds,
            P50Microseconds: p95Microseconds / 2,
            P75Microseconds: (long)(p95Microseconds * 0.8),
            P90Microseconds: (long)(p95Microseconds * 0.9),
            P95Microseconds: p95Microseconds,
            P99Microseconds: (long)(p95Microseconds * 1.1),
            P999Microseconds: (long)(p95Microseconds * 1.2));

        return new StepStatistics
        {
            Name = name,
            Step = new StepId(0),
            Count = count,
            SuccessCount = count - errorCount,
            DroppedCount = 0L,
            CountByOutcome = [count - errorCount, errorCount],
            BytesReceived = 1_234L,
            MaxSchedulingDelayMicroseconds = 500L,
            Response = latency,
            Service = latency,
        };
    }

    private static LoadTestReport CreateReport(IReadOnlyList<StepStatistics> steps, long metricsDropped = 0L)
    {
        StepStatistics iteration = steps.Count > 0 ? steps[0] : StepStatistics.Empty(StepId.None, WellKnownSteps.ITERATION);

        return new LoadTestReport
        {
            Scope = StatisticsScope.Cumulative,
            Duration = TimeSpan.FromSeconds(20),
            Steps = steps,
            Iteration = iteration,
            MetricsDropped = metricsDropped,
        };
    }

    [Fact]
    public void ToHtml_contains_step_names_and_percentiles()
    {
        LoadTestReport report = CreateReport([CreateStep("login", count: 100L, p95Microseconds: 45_000L)]);

        string html = report.ToHtml();

        Assert.Contains("login", html);
        Assert.Contains("45.00 ms", html);
        Assert.Contains("100", html);
    }

    [Fact]
    public void ToHtml_is_a_well_formed_standalone_document()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]);

        string html = report.ToHtml();

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<html", html);
        Assert.Contains("</html>", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToHtml_handles_a_report_with_no_steps()
    {
        LoadTestReport report = CreateReport([]);

        string html = report.ToHtml();

        Assert.Contains("<table>", html);
        Assert.Contains("<tbody>", html);
    }

    [Fact]
    public void ToHtml_warns_when_metrics_were_dropped()
    {
        LoadTestReport untrustworthy = CreateReport([CreateStep("login", 10L, 10_000L)], metricsDropped: 5L);
        LoadTestReport trustworthy = CreateReport([CreateStep("login", 10L, 10_000L)]);

        Assert.Contains("mesures perdues", untrustworthy.ToHtml());
        Assert.DoesNotContain("mesures perdues", trustworthy.ToHtml());
    }

    [Fact]
    public void ToHtml_escapes_step_names_to_prevent_html_injection()
    {
        const string maliciousStepName = "<script>alert(1)</script>";
        LoadTestReport report = CreateReport([CreateStep(maliciousStepName, 1L, 1_000L)]);

        string html = report.ToHtml();

        Assert.DoesNotContain(maliciousStepName, html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(maliciousStepName), html);
    }

    [Fact]
    public void ToHtml_without_thresholds_omits_the_threshold_section()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]);

        Assert.DoesNotContain("class=\"verdict", report.ToHtml());
    }

    private static ThresholdRule ErrorRateRuleFor(string stepName, double limit) => new()
    {
        StepName = stepName,
        Metric = ThresholdMetric.ErrorRate,
        Comparison = ThresholdComparison.LessThanOrEqual,
        Limit = limit,
    };

    [Fact]
    public void ToHtml_reports_a_passing_threshold_verdict()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]);
        ThresholdReport thresholds = ThresholdReport.Evaluate([ErrorRateRuleFor("login", limit: 0.5)], report);

        string html = report.ToHtml(thresholds);

        Assert.Contains("tous respectes", html);
        Assert.Contains("class=\"pass\"", html);
    }

    [Fact]
    public void ToHtml_reports_a_failing_threshold_verdict()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L, errorCount: 10L)]);
        ThresholdReport thresholds = ThresholdReport.Evaluate([ErrorRateRuleFor("login", limit: 0.0)], report);

        string html = report.ToHtml(thresholds);

        Assert.Contains("au moins un echec", html);
        Assert.Contains("class=\"fail\"", html);
    }
}