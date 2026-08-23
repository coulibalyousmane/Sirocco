using Sirocco.Domain.Data;
using Sirocco.Domain.Declarative;
using Sirocco.Domain.Metrics;

namespace Sirocco.UnitTests.Declarative;

public sealed class ScenarioDefinitionTests
{
    private static HttpStepDefinition CreateStep(string name = "login") => new()
    {
        Name = name,
        Method = "GET",
        Path = "/api/ping",
    };

    private static DataSetDefinition CreateDataSet(string name = "users") => new()
    {
        Name = name,
        Path = "users.csv",
    };

    [Fact]
    public void A_scenario_with_a_name_and_at_least_one_step_is_valid()
    {
        ScenarioDefinition scenario = new() { Name = "smoke", Steps = [CreateStep()] };

        scenario.Validate();
    }

    [Fact]
    public void A_blank_name_is_rejected()
    {
        ScenarioDefinition scenario = new() { Name = "   ", Steps = [CreateStep()] };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void A_scenario_without_any_step_is_rejected()
    {
        ScenarioDefinition scenario = new() { Name = "smoke", Steps = [] };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void Duplicate_step_names_are_rejected()
    {
        ScenarioDefinition scenario = new()
        {
            Name = "smoke",
            Steps = [CreateStep("login"), CreateStep("login")],
        };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void An_invalid_step_makes_the_whole_scenario_invalid()
    {
        ScenarioDefinition scenario = new()
        {
            Name = "smoke",
            Steps = [CreateStep() with { Path = "" }],
        };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void No_dataset_by_default()
    {
        Assert.Empty(new ScenarioDefinition { Name = "smoke", Steps = [CreateStep()] }.Datasets);
    }

    [Fact]
    public void A_scenario_with_a_valid_dataset_is_valid()
    {
        ScenarioDefinition scenario = new() { Name = "smoke", Steps = [CreateStep()], Datasets = [CreateDataSet()] };

        scenario.Validate();
    }

    [Fact]
    public void Duplicate_dataset_names_are_rejected()
    {
        ScenarioDefinition scenario = new()
        {
            Name = "smoke",
            Steps = [CreateStep()],
            Datasets = [CreateDataSet("users"), CreateDataSet("users")],
        };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void An_invalid_dataset_makes_the_whole_scenario_invalid()
    {
        ScenarioDefinition scenario = new()
        {
            Name = "smoke",
            Steps = [CreateStep()],
            Datasets = [CreateDataSet() with { Path = "" }],
        };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void A_check_name_colliding_with_another_checks_name_is_rejected()
    {
        HttpStepDefinition step = CreateStep() with
        {
            Checks = [new CheckRule { Name = "has-token", Regex = "a" }, new CheckRule { Name = "has-token", Regex = "b" }],
        };
        ScenarioDefinition scenario = new() { Name = "smoke", Steps = [step] };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void A_check_name_colliding_with_a_step_name_is_rejected()
    {
        HttpStepDefinition step = CreateStep("checkout") with
        {
            Checks = [new CheckRule { Name = "checkout", Regex = "a" }],
        };
        ScenarioDefinition scenario = new() { Name = "smoke", Steps = [step] };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void A_check_name_colliding_with_a_different_steps_name_is_rejected()
    {
        HttpStepDefinition login = CreateStep("login");
        HttpStepDefinition checkout = CreateStep("checkout") with
        {
            Checks = [new CheckRule { Name = "login", Regex = "a" }],
        };
        ScenarioDefinition scenario = new() { Name = "smoke", Steps = [login, checkout] };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void Two_steps_with_the_same_name_in_different_groups_do_not_collide()
    {
        ScenarioDefinition scenario = new()
        {
            Name = "smoke",
            Steps = [CreateStep("pay") with { Group = "checkout" }, CreateStep("pay") with { Group = "refund" }],
        };

        scenario.Validate();
    }

    [Fact]
    public void Two_steps_with_the_same_qualified_name_are_rejected()
    {
        ScenarioDefinition scenario = new()
        {
            Name = "smoke",
            Steps = [CreateStep("pay") with { Group = "checkout" }, CreateStep("pay") with { Group = "checkout" }],
        };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void No_tags_by_default()
    {
        Assert.Empty(new ScenarioDefinition { Name = "smoke", Steps = [CreateStep()] }.Tags);
    }

    [Fact]
    public void A_scenario_with_valid_tags_is_valid()
    {
        ScenarioDefinition scenario = new()
        {
            Name = "smoke",
            Steps = [CreateStep()],
            Tags = new Dictionary<string, string> { ["region"] = "eu-west", ["version"] = "v2" },
        };

        scenario.Validate();
    }

    [Fact]
    public void A_tag_with_a_blank_value_is_rejected()
    {
        ScenarioDefinition scenario = new()
        {
            Name = "smoke",
            Steps = [CreateStep()],
            Tags = new Dictionary<string, string> { ["region"] = " " },
        };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }

    [Fact]
    public void The_same_metric_name_can_appear_in_two_steps_with_the_same_kind()
    {
        HttpStepDefinition addItem = CreateStep("add-item") with
        {
            Metrics = [new MetricRule { Name = "orders_total", Kind = CustomMetricKind.Counter }],
        };
        HttpStepDefinition pay = CreateStep("pay") with
        {
            Metrics = [new MetricRule { Name = "orders_total", Kind = CustomMetricKind.Counter }],
        };
        ScenarioDefinition scenario = new() { Name = "smoke", Steps = [addItem, pay] };

        scenario.Validate();
    }

    [Fact]
    public void The_same_metric_name_with_a_different_kind_in_another_step_is_rejected()
    {
        HttpStepDefinition addItem = CreateStep("add-item") with
        {
            Metrics = [new MetricRule { Name = "orders_total", Kind = CustomMetricKind.Counter }],
        };
        HttpStepDefinition pay = CreateStep("pay") with
        {
            Metrics = [new MetricRule { Name = "orders_total", Kind = CustomMetricKind.Gauge, JsonPath = "$.total" }],
        };
        ScenarioDefinition scenario = new() { Name = "smoke", Steps = [addItem, pay] };

        Assert.Throws<ArgumentException>(scenario.Validate);
    }
}

public sealed class DataSetDefinitionTests
{
    private static DataSetDefinition CreateDataSet() => new() { Name = "users", Path = "users.csv" };

    [Fact]
    public void A_dataset_with_a_name_and_a_path_is_valid() => CreateDataSet().Validate();

    [Fact]
    public void A_blank_name_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateDataSet() with { Name = " " }).Validate());

    [Fact]
    public void A_blank_path_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateDataSet() with { Path = "" }).Validate());

    [Fact]
    public void Circular_is_the_default_strategy() =>
        Assert.Equal(DataSetIterationStrategy.Circular, CreateDataSet().Strategy);
}

public sealed class HttpStepDefinitionTests
{
    private static HttpStepDefinition CreateStep() => new()
    {
        Name = "login",
        Method = "POST",
        Path = "/api/auth/login",
    };

    [Fact]
    public void A_step_with_a_name_a_method_and_a_path_is_valid()
    {
        HttpStepDefinition step = CreateStep();

        step.Validate();
    }

    [Fact]
    public void A_blank_name_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateStep() with { Name = " " }).Validate());

    [Fact]
    public void A_blank_method_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateStep() with { Method = "" }).Validate());

    [Fact]
    public void A_blank_path_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateStep() with { Path = "" }).Validate());

    [Fact]
    public void A_body_without_a_content_type_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateStep() with { Body = "{}", ContentType = "" }).Validate());

    [Fact]
    public void No_expected_status_codes_by_default()
    {
        Assert.Empty(CreateStep().ExpectedStatusCodes);
    }

    [Fact]
    public void No_checks_by_default()
    {
        Assert.Empty(CreateStep().Checks);
    }

    [Fact]
    public void An_invalid_check_makes_the_whole_step_invalid()
    {
        HttpStepDefinition step = CreateStep() with { Checks = [new CheckRule { Name = "has-token" }] };

        Assert.Throws<ArgumentException>(step.Validate);
    }

    [Fact]
    public void No_group_by_default() => Assert.Null(CreateStep().Group);

    [Fact]
    public void QualifiedName_is_just_the_name_without_a_group() =>
        Assert.Equal("login", CreateStep().QualifiedName);

    [Fact]
    public void QualifiedName_prefixes_the_name_with_the_group() =>
        Assert.Equal("checkout/login", (CreateStep() with { Group = "checkout" }).QualifiedName);

    [Fact]
    public void A_blank_group_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateStep() with { Group = " " }).Validate());

    [Fact]
    public void A_group_starting_with_a_slash_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateStep() with { Group = "/checkout" }).Validate());

    [Fact]
    public void A_group_ending_with_a_slash_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateStep() with { Group = "checkout/" }).Validate());

    [Fact]
    public void A_group_with_a_double_slash_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateStep() with { Group = "checkout//payment" }).Validate());

    [Fact]
    public void A_nested_group_is_valid() => (CreateStep() with { Group = "checkout/payment" }).Validate();

    [Fact]
    public void No_metrics_by_default() => Assert.Empty(CreateStep().Metrics);

    [Fact]
    public void An_invalid_metric_makes_the_whole_step_invalid()
    {
        HttpStepDefinition step = CreateStep() with
        {
            Metrics = [new MetricRule { Name = "order_value", Kind = CustomMetricKind.Trend }],
        };

        Assert.Throws<ArgumentException>(step.Validate);
    }

    [Fact]
    public void No_think_time_by_default() => Assert.Null(CreateStep().ThinkTime);

    [Fact]
    public void An_invalid_think_time_makes_the_whole_step_invalid()
    {
        HttpStepDefinition step = CreateStep() with
        {
            ThinkTime = new ThinkTimeDefinition { Min = TimeSpan.FromSeconds(-1) },
        };

        Assert.Throws<ArgumentException>(step.Validate);
    }
}

public sealed class CheckRuleTests
{
    private static CheckRule CreateCheck() => new() { Name = "has-token", JsonPath = "$.token" };

    [Fact]
    public void A_check_with_a_name_and_exactly_one_expression_is_valid() => CreateCheck().Validate();

    [Fact]
    public void A_blank_name_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateCheck() with { Name = " " }).Validate());

    [Fact]
    public void No_expression_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateCheck() with { JsonPath = null }).Validate());

    [Fact]
    public void Two_expressions_at_once_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (CreateCheck() with { Regex = "x" }).Validate());

    [Fact]
    public void An_invalid_regex_syntax_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (new CheckRule { Name = "c", Regex = "(" }).Validate());

    [Fact]
    public void No_expected_value_by_default() => Assert.Null(CreateCheck().Expected);

    [Fact]
    public void Evaluate_passes_when_the_expression_matches_and_no_expected_value_is_set()
    {
        CheckRule check = new() { Name = "has-token", JsonPath = "$.token" };

        Assert.True(check.Evaluate("""{"token":"abc"}"""));
    }

    [Fact]
    public void Evaluate_fails_when_the_expression_does_not_match()
    {
        CheckRule check = new() { Name = "has-token", JsonPath = "$.token" };

        Assert.False(check.Evaluate("""{"other":"abc"}"""));
    }

    [Fact]
    public void Evaluate_passes_when_the_matched_value_equals_the_expected_value()
    {
        CheckRule check = new() { Name = "status-ok", JsonPath = "$.status", Expected = "ok" };

        Assert.True(check.Evaluate("""{"status":"ok"}"""));
    }

    [Fact]
    public void Evaluate_fails_when_the_matched_value_differs_from_the_expected_value()
    {
        CheckRule check = new() { Name = "status-ok", JsonPath = "$.status", Expected = "ok" };

        Assert.False(check.Evaluate("""{"status":"degraded"}"""));
    }

    [Fact]
    public void Evaluate_fails_when_an_expected_value_is_set_but_nothing_matched()
    {
        CheckRule check = new() { Name = "status-ok", JsonPath = "$.status", Expected = "ok" };

        Assert.False(check.Evaluate("""{"other":"x"}"""));
    }

    [Fact]
    public void Evaluate_supports_regex()
    {
        CheckRule check = new() { Name = "has-order-id", Regex = "\"orderId\":\"([^\"]+)\"" };

        Assert.True(check.Evaluate("""{"orderId":"abc-123"}"""));
        Assert.False(check.Evaluate("""{"other":"x"}"""));
    }
}

public sealed class MetricRuleTests
{
    [Fact]
    public void A_counter_without_an_expression_is_valid() =>
        (new MetricRule { Name = "orders_total", Kind = CustomMetricKind.Counter }).Validate();

    [Fact]
    public void A_gauge_without_an_expression_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (new MetricRule { Name = "active_carts", Kind = CustomMetricKind.Gauge }).Validate());

    [Fact]
    public void A_trend_without_an_expression_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (new MetricRule { Name = "order_value", Kind = CustomMetricKind.Trend }).Validate());

    [Fact]
    public void A_rate_without_an_expression_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (new MetricRule { Name = "cache_hit_rate", Kind = CustomMetricKind.Rate }).Validate());

    [Fact]
    public void A_blank_name_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (new MetricRule { Name = " ", Kind = CustomMetricKind.Counter }).Validate());

    [Fact]
    public void A_gauge_with_a_jsonpath_expression_is_valid() =>
        (new MetricRule { Name = "active_carts", Kind = CustomMetricKind.Gauge, JsonPath = "$.cartSize" }).Validate();

    [Fact]
    public void Two_expressions_at_once_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (new MetricRule
        {
            Name = "order_value",
            Kind = CustomMetricKind.Trend,
            JsonPath = "$.total",
            Regex = "x",
        }).Validate());

    [Fact]
    public void An_invalid_regex_syntax_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (new MetricRule { Name = "m", Kind = CustomMetricKind.Trend, Regex = "(" }).Validate());

    [Fact]
    public void Expected_on_a_counter_is_rejected() =>
        Assert.Throws<ArgumentException>(() => (new MetricRule
        {
            Name = "orders_total",
            Kind = CustomMetricKind.Counter,
            Expected = "ok",
        }).Validate());

    [Fact]
    public void Expected_on_a_rate_is_valid() =>
        (new MetricRule
        {
            Name = "status_ok_rate",
            Kind = CustomMetricKind.Rate,
            JsonPath = "$.status",
            Expected = "ok",
        }).Validate();

    [Fact]
    public void Evaluate_a_counter_without_an_expression_always_yields_one()
    {
        MetricRule metric = new() { Name = "orders_total", Kind = CustomMetricKind.Counter };

        Assert.Equal(1d, metric.Evaluate("""{"anything":"x"}"""));
    }

    [Fact]
    public void Evaluate_a_counter_with_an_expression_extracts_the_increment()
    {
        MetricRule metric = new() { Name = "items_sold", Kind = CustomMetricKind.Counter, JsonPath = "$.quantity" };

        Assert.Equal(3d, metric.Evaluate("""{"quantity":3}"""));
    }

    [Fact]
    public void Evaluate_a_gauge_extracts_the_current_value()
    {
        MetricRule metric = new() { Name = "active_carts", Kind = CustomMetricKind.Gauge, JsonPath = "$.cartSize" };

        Assert.Equal(4d, metric.Evaluate("""{"cartSize":4}"""));
    }

    [Fact]
    public void Evaluate_a_trend_extracts_the_observed_value()
    {
        MetricRule metric = new() { Name = "order_value", Kind = CustomMetricKind.Trend, JsonPath = "$.total" };

        Assert.Equal(87.5d, metric.Evaluate("""{"total":87.5}"""));
    }

    [Fact]
    public void Evaluate_a_gauge_or_trend_yields_null_when_the_expression_does_not_match()
    {
        MetricRule metric = new() { Name = "active_carts", Kind = CustomMetricKind.Gauge, JsonPath = "$.cartSize" };

        Assert.Null(metric.Evaluate("""{"other":"x"}"""));
    }

    [Fact]
    public void Evaluate_a_gauge_or_trend_yields_null_when_the_matched_value_is_not_numeric()
    {
        MetricRule metric = new() { Name = "active_carts", Kind = CustomMetricKind.Gauge, JsonPath = "$.cartSize" };

        Assert.Null(metric.Evaluate("""{"cartSize":"not-a-number"}"""));
    }

    [Fact]
    public void Evaluate_a_rate_without_expected_yields_one_when_the_expression_matches()
    {
        MetricRule metric = new() { Name = "has_token_rate", Kind = CustomMetricKind.Rate, JsonPath = "$.token" };

        Assert.Equal(1d, metric.Evaluate("""{"token":"abc"}"""));
    }

    [Fact]
    public void Evaluate_a_rate_without_expected_yields_zero_when_the_expression_does_not_match()
    {
        MetricRule metric = new() { Name = "has_token_rate", Kind = CustomMetricKind.Rate, JsonPath = "$.token" };

        Assert.Equal(0d, metric.Evaluate("""{"other":"x"}"""));
    }

    [Fact]
    public void Evaluate_a_rate_with_expected_yields_one_only_when_the_matched_value_equals_it()
    {
        MetricRule metric = new() { Name = "status_ok_rate", Kind = CustomMetricKind.Rate, JsonPath = "$.status", Expected = "ok" };

        Assert.Equal(1d, metric.Evaluate("""{"status":"ok"}"""));
        Assert.Equal(0d, metric.Evaluate("""{"status":"degraded"}"""));
        Assert.Equal(0d, metric.Evaluate("""{"other":"x"}"""));
    }
}

public sealed class ThinkTimeDefinitionTests
{
    [Fact]
    public void A_non_negative_fixed_duration_is_valid() =>
        (new ThinkTimeDefinition { Min = TimeSpan.FromSeconds(1) }).Validate("browse");

    [Fact]
    public void A_negative_minimum_is_rejected() =>
        Assert.Throws<ArgumentException>(() =>
            (new ThinkTimeDefinition { Min = TimeSpan.FromMilliseconds(-1) }).Validate("browse"));

    [Fact]
    public void A_maximum_below_the_minimum_is_rejected() =>
        Assert.Throws<ArgumentException>(() =>
            (new ThinkTimeDefinition { Min = TimeSpan.FromSeconds(2), Max = TimeSpan.FromSeconds(1) }).Validate("browse"));

    [Fact]
    public void A_maximum_equal_to_the_minimum_is_valid() =>
        (new ThinkTimeDefinition { Min = TimeSpan.FromSeconds(1), Max = TimeSpan.FromSeconds(1) }).Validate("browse");

    [Fact]
    public void No_maximum_by_default() => Assert.Null(new ThinkTimeDefinition { Min = TimeSpan.FromSeconds(1) }.Max);

    [Fact]
    public void Sample_returns_exactly_the_minimum_when_no_maximum_is_set()
    {
        ThinkTimeDefinition thinkTime = new() { Min = TimeSpan.FromMilliseconds(250) };

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(250), thinkTime.Sample());
        }
    }

    [Fact]
    public void Sample_stays_within_the_range_when_a_maximum_is_set()
    {
        ThinkTimeDefinition thinkTime = new() { Min = TimeSpan.FromMilliseconds(100), Max = TimeSpan.FromMilliseconds(200) };

        for (int i = 0; i < 100; i++)
        {
            TimeSpan sample = thinkTime.Sample();
            Assert.InRange(sample, thinkTime.Min, thinkTime.Max.Value);
        }
    }
}