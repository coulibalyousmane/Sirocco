using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class LoadTestReportComparisonTests
{
    private static StepStatistics CreateStep(string name, long p95Microseconds, long count = 100L, long errorCount = 0L)
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
            BytesReceived = 0L,
            MaxSchedulingDelayMicroseconds = 0L,
            Response = latency,
            Service = latency,
        };
    }

    private static LoadTestReport CreateReport(IReadOnlyList<StepStatistics> steps) => new()
    {
        Scope = StatisticsScope.Cumulative,
        Duration = TimeSpan.FromSeconds(10),
        Steps = steps,
        Iteration = steps.Count > 0 ? steps[0] : StepStatistics.Empty(StepId.None, WellKnownSteps.ITERATION),
        MetricsDropped = 0L,
    };

    [Fact]
    public void Compare_computes_the_p95_delta_for_a_matching_step()
    {
        LoadTestReport baseline = CreateReport([CreateStep("login", p95Microseconds: 100_000L)]);
        LoadTestReport current = CreateReport([CreateStep("login", p95Microseconds: 120_000L)]);

        LoadTestReportComparison comparison = LoadTestReportComparison.Compare(baseline, current);

        StepComparison login = Assert.Single(comparison.Steps);
        Assert.Equal(20d, login.P95DeltaMilliseconds);
        Assert.Equal(0.2, login.P95DeltaPercent!.Value, precision: 3);
    }

    [Fact]
    public void Compare_flags_a_step_only_present_in_the_current_report()
    {
        LoadTestReport baseline = CreateReport([CreateStep("login", 100_000L)]);
        LoadTestReport current = CreateReport([CreateStep("login", 100_000L), CreateStep("checkout", 50_000L)]);

        LoadTestReportComparison comparison = LoadTestReportComparison.Compare(baseline, current);

        StepComparison checkout = comparison.Steps.Single(s => s.Name == "checkout");
        Assert.Null(checkout.Baseline);
        Assert.NotNull(checkout.Current);
        Assert.Null(checkout.P95DeltaPercent);
        Assert.Null(checkout.P95DeltaMilliseconds);
    }

    [Fact]
    public void Compare_flags_a_step_only_present_in_the_baseline_report()
    {
        LoadTestReport baseline = CreateReport([CreateStep("login", 100_000L), CreateStep("legacy-step", 50_000L)]);
        LoadTestReport current = CreateReport([CreateStep("login", 100_000L)]);

        LoadTestReportComparison comparison = LoadTestReportComparison.Compare(baseline, current);

        StepComparison legacy = comparison.Steps.Single(s => s.Name == "legacy-step");
        Assert.NotNull(legacy.Baseline);
        Assert.Null(legacy.Current);
    }

    [Fact]
    public void WorstP95RegressionPercent_returns_the_largest_regression_across_steps()
    {
        LoadTestReport baseline = CreateReport([CreateStep("login", 100_000L), CreateStep("checkout", 100_000L)]);
        LoadTestReport current = CreateReport([CreateStep("login", 110_000L), CreateStep("checkout", 150_000L)]);

        LoadTestReportComparison comparison = LoadTestReportComparison.Compare(baseline, current);

        Assert.Equal(0.5, comparison.WorstP95RegressionPercent()!.Value, precision: 3);
    }

    [Fact]
    public void WorstP95RegressionPercent_is_null_when_no_step_is_comparable()
    {
        LoadTestReport baseline = CreateReport([CreateStep("login", 100_000L)]);
        LoadTestReport current = CreateReport([CreateStep("checkout", 100_000L)]);

        LoadTestReportComparison comparison = LoadTestReportComparison.Compare(baseline, current);

        Assert.Null(comparison.WorstP95RegressionPercent());
    }

    [Fact]
    public void WorstP95RegressionPercent_can_be_negative_when_every_step_improved()
    {
        LoadTestReport baseline = CreateReport([CreateStep("login", 100_000L)]);
        LoadTestReport current = CreateReport([CreateStep("login", 80_000L)]);

        LoadTestReportComparison comparison = LoadTestReportComparison.Compare(baseline, current);

        Assert.True(comparison.WorstP95RegressionPercent() < 0d);
    }

    [Fact]
    public void ToTable_and_ToHtml_render_without_throwing_and_contain_the_step_name()
    {
        LoadTestReport baseline = CreateReport([CreateStep("login", 100_000L)]);
        LoadTestReport current = CreateReport([CreateStep("login", 120_000L)]);
        LoadTestReportComparison comparison = LoadTestReportComparison.Compare(baseline, current);

        string table = comparison.ToTable();
        string html = comparison.ToHtml();

        Assert.Contains("login", table);
        Assert.Contains("login", html);
        Assert.StartsWith("<!doctype html>", html);
    }

    [Fact]
    public void ToHtml_marks_a_regression_and_an_improvement_with_distinct_css_classes()
    {
        LoadTestReport baseline = CreateReport([CreateStep("slower", 100_000L), CreateStep("faster", 100_000L)]);
        LoadTestReport current = CreateReport([CreateStep("slower", 150_000L), CreateStep("faster", 50_000L)]);

        string html = LoadTestReportComparison.Compare(baseline, current).ToHtml();

        Assert.Contains("class=\"regression\"", html);
        Assert.Contains("class=\"improvement\"", html);
    }

    [Fact]
    public void ToHtml_escapes_step_names_to_prevent_html_injection()
    {
        const string maliciousName = "<script>alert(1)</script>";
        LoadTestReport baseline = CreateReport([CreateStep(maliciousName, 100_000L)]);
        LoadTestReport current = CreateReport([CreateStep(maliciousName, 100_000L)]);

        string html = LoadTestReportComparison.Compare(baseline, current).ToHtml();

        Assert.DoesNotContain(maliciousName, html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(maliciousName), html);
    }
}