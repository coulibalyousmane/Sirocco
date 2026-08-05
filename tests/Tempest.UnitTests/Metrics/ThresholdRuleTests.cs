using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class ThresholdRuleTests
{
    private const string ITERATION_STEP = WellKnownSteps.ITERATION;
    private const string CHECKOUT_STEP = "checkout";

    private static StepStatistics CreateStep(
        string name,
        long count = 100L,
        long successCount = 100L,
        double responseP99Milliseconds = 200d,
        long maxSchedulingDelayMicroseconds = 5_000L)
    {
        LatencySnapshot response = new(
            Count: count,
            MinMicroseconds: 1_000L,
            MaxMicroseconds: (long)(responseP99Milliseconds * 1_500d),
            MeanMicroseconds: responseP99Milliseconds * 500d,
            P50Microseconds: (long)(responseP99Milliseconds * 400d),
            P75Microseconds: (long)(responseP99Milliseconds * 600d),
            P90Microseconds: (long)(responseP99Milliseconds * 800d),
            P95Microseconds: (long)(responseP99Milliseconds * 900d),
            P99Microseconds: (long)(responseP99Milliseconds * 1_000d),
            P999Microseconds: (long)(responseP99Milliseconds * 1_400d));

        return new StepStatistics
        {
            Name = name,
            Step = new StepId(0),
            Count = count,
            SuccessCount = successCount,
            DroppedCount = 0L,
            CountByOutcome = [],
            BytesReceived = 0L,
            MaxSchedulingDelayMicroseconds = maxSchedulingDelayMicroseconds,
            Response = response,
            Service = response,
        };
    }

    private static LoadTestReport CreateReport(params StepStatistics[] steps) => new()
    {
        Scope = StatisticsScope.Cumulative,
        Duration = TimeSpan.FromSeconds(30),
        Steps = steps,
        Iteration = steps.FirstOrDefault(step => step.Name == ITERATION_STEP) ?? StepStatistics.Empty(StepId.None, ITERATION_STEP),
        MetricsDropped = 0L,
    };

    [Fact]
    public void A_response_percentile_below_the_limit_passes()
    {
        LoadTestReport report = CreateReport(CreateStep(ITERATION_STEP, responseP99Milliseconds: 200d));
        ThresholdRule rule = ThresholdRule.OnIteration(ThresholdMetric.ResponseP99Milliseconds, ThresholdComparison.LessThan, 500d);

        ThresholdEvaluation evaluation = rule.Evaluate(report);

        Assert.True(evaluation.Passed);
        Assert.True(evaluation.StepFound);
        Assert.Equal(200d, evaluation.ActualValue);
    }

    [Fact]
    public void A_response_percentile_above_the_limit_fails()
    {
        LoadTestReport report = CreateReport(CreateStep(ITERATION_STEP, responseP99Milliseconds: 800d));
        ThresholdRule rule = ThresholdRule.OnIteration(ThresholdMetric.ResponseP99Milliseconds, ThresholdComparison.LessThan, 500d);

        ThresholdEvaluation evaluation = rule.Evaluate(report);

        Assert.False(evaluation.Passed);
        Assert.Equal(800d, evaluation.ActualValue);
    }

    // Valeur observee fixe a 100 pour toutes ces lignes : seule la limite varie.
    [Theory]
    [InlineData(ThresholdComparison.LessThan, 101d, true)]
    [InlineData(ThresholdComparison.LessThan, 100d, false)]
    [InlineData(ThresholdComparison.LessThanOrEqual, 100d, true)]
    [InlineData(ThresholdComparison.LessThanOrEqual, 99d, false)]
    [InlineData(ThresholdComparison.GreaterThan, 101d, false)]
    [InlineData(ThresholdComparison.GreaterThan, 99d, true)]
    [InlineData(ThresholdComparison.GreaterThanOrEqual, 100d, true)]
    [InlineData(ThresholdComparison.GreaterThanOrEqual, 101d, false)]
    public void Every_comparison_operator_behaves_as_named(ThresholdComparison comparison, double limit, bool expectedPassed)
    {
        LoadTestReport report = CreateReport(CreateStep(ITERATION_STEP, responseP99Milliseconds: 100d));
        ThresholdRule rule = ThresholdRule.OnIteration(ThresholdMetric.ResponseP99Milliseconds, comparison, limit);

        Assert.Equal(expectedPassed, rule.Evaluate(report).Passed);
    }

    [Fact]
    public void An_error_rate_threshold_reads_the_step_error_rate()
    {
        StepStatistics step = CreateStep(ITERATION_STEP, count: 1_000L, successCount: 950L);
        LoadTestReport report = CreateReport(step);
        ThresholdRule rule = ThresholdRule.OnIteration(ThresholdMetric.ErrorRate, ThresholdComparison.LessThanOrEqual, 0.1d);

        ThresholdEvaluation evaluation = rule.Evaluate(report);

        Assert.True(evaluation.Passed);
        Assert.Equal(0.05d, evaluation.ActualValue!.Value, 1e-9);
    }

    [Fact]
    public void A_scheduling_delay_threshold_reads_milliseconds_not_microseconds()
    {
        LoadTestReport report = CreateReport(CreateStep(ITERATION_STEP, maxSchedulingDelayMicroseconds: 25_000L));
        ThresholdRule rule = ThresholdRule.OnIteration(ThresholdMetric.SchedulingDelayMaxMilliseconds, ThresholdComparison.LessThan, 30d);

        ThresholdEvaluation evaluation = rule.Evaluate(report);

        Assert.True(evaluation.Passed);
        Assert.Equal(25d, evaluation.ActualValue!.Value, 1e-9);
    }

    [Fact]
    public void A_count_threshold_reads_the_raw_measurement_count()
    {
        LoadTestReport report = CreateReport(CreateStep(CHECKOUT_STEP, count: 0L, successCount: 0L));
        ThresholdRule rule = ThresholdRule.OnStep(CHECKOUT_STEP, ThresholdMetric.Count, ThresholdComparison.GreaterThan, 0d);

        Assert.False(rule.Evaluate(report).Passed);
    }

    /// <summary>
    /// Une regle mal configuree (nom d'etape errone) doit echouer, pas disparaitre : un
    /// pipeline qui continuerait de passer au vert malgre une faute de frappe serait pire
    /// qu'un pipeline sans seuil du tout.
    /// </summary>
    [Fact]
    public void A_rule_targeting_an_unknown_step_fails_rather_than_passing_silently()
    {
        LoadTestReport report = CreateReport(CreateStep(ITERATION_STEP));
        ThresholdRule rule = ThresholdRule.OnStep("etape-inexistante", ThresholdMetric.ResponseP99Milliseconds, ThresholdComparison.LessThan, 500d);

        ThresholdEvaluation evaluation = rule.Evaluate(report);

        Assert.False(evaluation.Passed);
        Assert.False(evaluation.StepFound);
        Assert.Null(evaluation.ActualValue);
    }

    [Fact]
    public void Describe_falls_back_to_an_auto_generated_label_when_no_name_is_given()
    {
        ThresholdRule rule = ThresholdRule.OnIteration(ThresholdMetric.ResponseP99Milliseconds, ThresholdComparison.LessThan, 500d);

        Assert.Contains("ResponseP99Milliseconds", rule.Describe(), StringComparison.Ordinal);
        Assert.Contains("<", rule.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_prefers_the_explicit_name_when_one_is_given()
    {
        ThresholdRule rule = ThresholdRule.OnIteration(
            ThresholdMetric.ResponseP99Milliseconds,
            ThresholdComparison.LessThan,
            500d,
            name: "P99 sous 500ms");

        Assert.Equal("P99 sous 500ms", rule.Describe());
    }

    [Fact]
    public void A_null_report_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() =>
            ThresholdRule.OnIteration(ThresholdMetric.Count, ThresholdComparison.GreaterThan, 0d).Evaluate(null!));
}