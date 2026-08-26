using Sirocco.Cli;
using Sirocco.Domain.Metrics;

namespace Sirocco.UnitTests.Cli;

public sealed class CliOptionsTests
{
    [Fact]
    public void A_scenario_path_and_flags_are_all_captured()
    {
        CliOptions options = CliOptions.Parse([
            "scenario.yaml",
            "--target-url", "http://localhost:5299",
            "--rps", "50",
            "--duration", "30s",
            "--max-vus", "100",
            "--report-html", "report.html",
            "--report-json", "report.json",
        ]);

        Assert.Equal("scenario.yaml", options.ScenarioPath);
        Assert.Equal("http://localhost:5299", options.TargetUrl);
        Assert.Equal(50, options.Rps);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Duration);
        Assert.Equal(100, options.MaxVirtualUsers);
        Assert.Equal("report.html", options.ReportHtmlPath);
        Assert.Equal("report.json", options.ReportJsonPath);
    }

    [Fact]
    public void Allow_env_is_repeatable_and_allow_env_all_is_captured()
    {
        CliOptions options = CliOptions.Parse([
            "--allow-env", "SIROCCO_DEMO_API_TOKEN",
            "--allow-env", "SOME_OTHER_NAME",
            "--allow-env-all",
        ]);

        Assert.Equal(["SIROCCO_DEMO_API_TOKEN", "SOME_OTHER_NAME"], options.AllowedEnvironmentVariables);
        Assert.True(options.AllowAllEnvironmentVariables);
    }

    [Fact]
    public void Allow_env_and_allow_env_all_are_false_and_empty_by_default()
    {
        CliOptions options = CliOptions.Parse(["--target-url", "http://localhost:5299", "--rps", "10", "--duration", "5s"]);

        Assert.Empty(options.AllowedEnvironmentVariables);
        Assert.False(options.AllowAllEnvironmentVariables);
    }

    [Fact]
    public void A_workflow_name_replaces_the_positional_scenario_path()
    {
        CliOptions options = CliOptions.Parse(["--workflow", "websocket-echo"]);

        Assert.Null(options.ScenarioPath);
        Assert.Equal("websocket-echo", options.Workflow);
    }

    [Fact]
    public void A_ramp_captures_both_endpoints()
    {
        CliOptions options = CliOptions.Parse(["--from-rps", "0", "--to-rps", "100", "--duration", "10s"]);

        Assert.Equal(0, options.FromRps);
        Assert.Equal(100, options.ToRps);
    }

    [Fact]
    public void From_rps_without_to_rps_is_rejected() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--from-rps", "0"]));

    [Fact]
    public void Rps_and_ramp_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--rps", "50", "--from-rps", "0", "--to-rps", "100"]));

    [Fact]
    public void A_vus_flag_is_captured()
    {
        CliOptions options = CliOptions.Parse(["--vus", "50", "--duration", "30s"]);

        Assert.Equal(50, options.Vus);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Duration);
    }

    [Fact]
    public void Vus_and_rps_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--vus", "50", "--rps", "100", "--duration", "10s"]));

    [Fact]
    public void Vus_and_ramp_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() =>
            CliOptions.Parse(["--vus", "50", "--from-rps", "0", "--to-rps", "100", "--duration", "10s"]));

    [Fact]
    public void Vus_and_max_vus_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--vus", "50", "--max-vus", "100", "--duration", "10s"]));

    [Fact]
    public void A_vus_ramp_is_captured()
    {
        CliOptions options = CliOptions.Parse(["--vus-from", "0", "--vus-to", "50", "--duration", "30s"]);

        Assert.Equal(0, options.VusFrom);
        Assert.Equal(50, options.VusTo);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Duration);
    }

    [Fact]
    public void Vus_from_without_vus_to_is_rejected() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--vus-from", "0", "--duration", "10s"]));

    [Fact]
    public void Vus_ramp_and_vus_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() =>
            CliOptions.Parse(["--vus-from", "0", "--vus-to", "50", "--vus", "10", "--duration", "10s"]));

    [Fact]
    public void Vus_ramp_and_rps_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() =>
            CliOptions.Parse(["--vus-from", "0", "--vus-to", "50", "--rps", "100", "--duration", "10s"]));

    [Fact]
    public void Vus_ramp_and_max_vus_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() =>
            CliOptions.Parse(["--vus-from", "0", "--vus-to", "50", "--max-vus", "100", "--duration", "10s"]));

    [Fact]
    public void An_iterations_per_vu_flag_is_captured()
    {
        CliOptions options = CliOptions.Parse(["--vus", "10", "--iterations-per-vu", "50"]);

        Assert.Equal(10, options.Vus);
        Assert.Equal(50, options.IterationsPerVirtualUser);
    }

    [Fact]
    public void Iterations_per_vu_and_duration_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() =>
            CliOptions.Parse(["--vus", "10", "--iterations-per-vu", "50", "--duration", "10s"]));

    [Fact]
    public void Iterations_per_vu_and_rps_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() =>
            CliOptions.Parse(["--iterations-per-vu", "50", "--rps", "100", "--duration", "10s"]));

    [Fact]
    public void Iterations_per_vu_and_iterations_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() =>
            CliOptions.Parse(["--iterations-per-vu", "50", "--iterations", "1000"]));

    [Fact]
    public void An_iterations_flag_is_captured()
    {
        CliOptions options = CliOptions.Parse(["--iterations", "1000", "--max-vus", "20"]);

        Assert.Equal(1000, options.Iterations);
        Assert.Equal(20, options.MaxVirtualUsers);
    }

    [Fact]
    public void Iterations_and_vus_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--iterations", "1000", "--vus", "10"]));

    [Fact]
    public void Iterations_and_duration_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--iterations", "1000", "--duration", "10s"]));

    [Fact]
    public void Iterations_and_rps_together_are_mutually_exclusive() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--iterations", "1000", "--rps", "50", "--duration", "10s"]));

    [Fact]
    public void A_max_rps_flag_is_captured()
    {
        CliOptions options = CliOptions.Parse(["--rps", "50", "--duration", "10s", "--max-rps", "20"]);

        Assert.Equal(20, options.MaxRequestsPerSecond);
    }

    [Fact]
    public void A_max_rps_flag_composes_with_the_closed_model() =>
        // Le bridage est un overlay independant du modele, pas un cinquieme modele mutuellement
        // exclusif : il doit rester accepte avec --vus.
        Assert.Equal(5, CliOptions.Parse(["--vus", "10", "--duration", "10s", "--max-rps", "5"]).MaxRequestsPerSecond);

    [Fact]
    public void A_zero_or_negative_max_rps_is_rejected()
    {
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--max-rps", "0"]));
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--max-rps", "-1"]));
    }

    [Fact]
    public void A_second_positional_argument_is_rejected() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["scenario.yaml", "other.yaml"]));

    [Fact]
    public void An_unrecognized_option_is_rejected() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--not-a-real-option"]));

    [Fact]
    public void A_flag_missing_its_value_is_rejected() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--target-url"]));

    [Fact]
    public void A_non_numeric_rps_is_rejected() =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--rps", "fast"]));

    [Fact]
    public void A_full_threshold_rule_is_parsed()
    {
        CliOptions options = CliOptions.Parse([
            "--threshold", "__iteration:ResponseP95Milliseconds:LessThan:200:p95 sous 200ms",
        ]);

        ThresholdRule rule = Assert.Single(options.Thresholds);
        Assert.Equal("__iteration", rule.StepName);
        Assert.Equal(ThresholdMetric.ResponseP95Milliseconds, rule.Metric);
        Assert.Equal(ThresholdComparison.LessThan, rule.Comparison);
        Assert.Equal(200, rule.Limit);
        Assert.Equal("p95 sous 200ms", rule.Name);
    }

    [Fact]
    public void A_threshold_rule_without_a_name_is_parsed()
    {
        CliOptions options = CliOptions.Parse(["--threshold", "__iteration:ErrorRate:LessThanOrEqual:0.01"]);

        ThresholdRule rule = Assert.Single(options.Thresholds);
        Assert.Null(rule.Name);
        Assert.Equal(ThresholdMetric.ErrorRate, rule.Metric);
    }

    [Fact]
    public void Multiple_thresholds_are_all_captured()
    {
        CliOptions options = CliOptions.Parse([
            "--threshold", "__iteration:ResponseP95Milliseconds:LessThan:200",
            "--threshold", "__iteration:ErrorRate:LessThanOrEqual:0.01",
        ]);

        Assert.Equal(2, options.Thresholds.Count);
    }

    [Theory]
    [InlineData("too:few:parts")]
    [InlineData("__iteration:NotAMetric:LessThan:200")]
    [InlineData("__iteration:ResponseP95Milliseconds:NotAComparison:200")]
    [InlineData("__iteration:ResponseP95Milliseconds:LessThan:not-a-number")]
    public void A_malformed_threshold_is_rejected(string malformed) =>
        Assert.Throws<FormatException>(() => CliOptions.Parse(["--threshold", malformed]));
}