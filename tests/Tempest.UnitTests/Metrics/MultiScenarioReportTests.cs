using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class MultiScenarioReportTests
{
    private static StepStatistics CreateStep(string name, long count, long errorCount = 0L) => new()
    {
        Name = name,
        Step = new StepId(0),
        Count = count,
        SuccessCount = count - errorCount,
        DroppedCount = 0L,
        CountByOutcome = [count - errorCount, errorCount],
        BytesReceived = 0L,
        MaxSchedulingDelayMicroseconds = 0L,
        Response = LatencySnapshot.Empty,
        Service = LatencySnapshot.Empty,
    };

    private static LoadTestReport CreateReport(string stepName, long count, long metricsDropped = 0L, bool closedModel = false) => new()
    {
        Scope = StatisticsScope.Cumulative,
        Duration = TimeSpan.FromSeconds(10),
        Steps = [CreateStep(stepName, count)],
        Iteration = CreateStep(WellKnownSteps.ITERATION, count),
        MetricsDropped = metricsDropped,
        ClosedModel = closedModel,
    };

    private static ScenarioReport CreateScenario(string name, LoadTestReport report, IReadOnlyList<ThresholdRule>? rules = null) =>
        new()
        {
            Name = name,
            Report = report,
            Thresholds = ThresholdReport.Evaluate(rules ?? [], report),
        };

    [Fact]
    public void IsTrustworthy_is_true_only_when_no_scenario_dropped_metrics()
    {
        MultiScenarioReport trustworthy = new()
        {
            Scenarios = [CreateScenario("checkout", CreateReport("login", 10L)), CreateScenario("browse", CreateReport("search", 10L))],
        };
        MultiScenarioReport untrustworthy = new()
        {
            Scenarios = [CreateScenario("checkout", CreateReport("login", 10L)), CreateScenario("browse", CreateReport("search", 10L, metricsDropped: 3L))],
        };

        Assert.True(trustworthy.IsTrustworthy);
        Assert.False(untrustworthy.IsTrustworthy);
    }

    [Fact]
    public void ThresholdsPassed_is_true_only_when_every_scenario_passes_its_own_thresholds()
    {
        ThresholdRule passingRule = new()
        {
            StepName = "login",
            Metric = ThresholdMetric.ErrorRate,
            Comparison = ThresholdComparison.LessThanOrEqual,
            Limit = 0.5,
        };
        ThresholdRule failingRule = new()
        {
            StepName = "search",
            Metric = ThresholdMetric.ErrorRate,
            Comparison = ThresholdComparison.LessThanOrEqual,
            Limit = 0.0,
        };

        MultiScenarioReport allPass = new()
        {
            Scenarios =
            [
                CreateScenario("checkout", CreateReport("login", 10L), [passingRule]),
                CreateScenario("browse", CreateReport("search", 10L), []),
            ],
        };
        MultiScenarioReport onePassOneFail = new()
        {
            Scenarios =
            [
                CreateScenario("checkout", CreateReport("login", 10L), [passingRule]),
                CreateScenario("browse", CreateReport("search", 10L, metricsDropped: 0L) with { Steps = [CreateStep("search", 10L, errorCount: 10L)] }, [failingRule]),
            ],
        };

        Assert.True(allPass.ThresholdsPassed);
        Assert.False(onePassOneFail.ThresholdsPassed);
    }

    [Fact]
    public void ToTable_shows_every_scenario_by_name()
    {
        MultiScenarioReport report = new()
        {
            Scenarios = [CreateScenario("checkout", CreateReport("login", 10L)), CreateScenario("browse", CreateReport("search", 20L))],
        };

        string table = report.ToTable();

        Assert.Contains("checkout", table);
        Assert.Contains("browse", table);
        Assert.Contains("login", table);
        Assert.Contains("search", table);
    }

    [Fact]
    public void ToHtml_is_a_single_well_formed_document_with_one_section_per_scenario()
    {
        MultiScenarioReport report = new()
        {
            Scenarios = [CreateScenario("checkout", CreateReport("login", 10L)), CreateScenario("browse", CreateReport("search", 20L))],
        };

        string html = report.ToHtml();

        Assert.StartsWith("<!doctype html>", html);
        // Un seul document : le doctype (donc l'ouverture de <html>) n'apparait qu'une fois,
        // jamais imbrique une deuxieme fois pour le second scenario.
        Assert.Equal(1, html.Split("<!doctype html>").Length - 1);
        Assert.Equal(2, html.Split("<section>").Length - 1);
        Assert.Contains("checkout", html);
        Assert.Contains("browse", html);
        Assert.Contains("login", html);
        Assert.Contains("search", html);
    }

    [Fact]
    public void ToHtml_escapes_scenario_names_to_prevent_html_injection()
    {
        const string maliciousName = "<script>alert(1)</script>";
        MultiScenarioReport report = new()
        {
            Scenarios = [CreateScenario(maliciousName, CreateReport("login", 10L))],
        };

        string html = report.ToHtml();

        Assert.DoesNotContain(maliciousName, html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(maliciousName), html);
    }
}