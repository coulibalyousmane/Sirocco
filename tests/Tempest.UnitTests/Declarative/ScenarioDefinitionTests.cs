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
}