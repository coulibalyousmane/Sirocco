using Tempest.Domain.Data;
using Tempest.Domain.Declarative;

namespace Tempest.UnitTests.Declarative;

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