using System.Net;
using Tempest.Application.Execution;
using Tempest.Domain.Declarative;
using Tempest.Domain.Metrics;
using Tempest.Scenarios;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.Declarative;

public sealed class DeclarativeWorkflowTests
{
    private static HttpStepDefinition Step(
        string name,
        string method = "GET",
        string path = "/api/ping",
        string? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyList<int>? expectedStatusCodes = null,
        IReadOnlyList<ExtractionRule>? extract = null) =>
        new()
        {
            Name = name,
            Method = method,
            Path = path,
            Body = body,
            Headers = headers ?? new Dictionary<string, string>(),
            ExpectedStatusCodes = expectedStatusCodes ?? [],
            Extract = extract ?? [],
        };

    /// <summary>
    /// Assemble un scenario, son contexte et son registre en reproduisant l'ordre
    /// d'enregistrement de <c>TargetRpsLoadEngine.PrepareSteps</c> : l'etape technique
    /// d'iteration en premier, avant celles du scenario.
    /// </summary>
    private static (
        DeclarativeWorkflow Workflow,
        VirtualUserContext Context,
        CollectingMetricSink Sink,
        StubHttpMessageHandler Handler,
        StepRegistry Steps)
        CreateHarness(ScenarioDefinition definition)
    {
        StubHttpMessageHandler handler = new();
        HttpClient client = new(handler) { BaseAddress = new Uri("https://target.example") };

        DeclarativeWorkflow workflow = new(definition);

        StepRegistry registry = new();
        StepId iterationStep = registry.Register(WellKnownSteps.ITERATION);
        workflow.RegisterSteps(registry);
        registry.Seal();

        CollectingMetricSink sink = new();
        VirtualUserContext context = new(virtualUserId: 0, client, sink, iterationStep);

        return (workflow, context, sink, handler, registry);
    }

    private static async Task RunIterationAsync(DeclarativeWorkflow workflow, VirtualUserContext context)
    {
        ExecutionToken token = new(IterationIndex: 0, ScheduledTicks: 0L);
        context.BeginIteration(in token, startedTicks: 0L, CancellationToken.None);
        await workflow.ExecuteAsync(context, CancellationToken.None).ConfigureAwait(false);
        context.EndIteration(startedTicks: 0L, RequestOutcome.Success);
    }

    [Fact]
    public void RegisterSteps_declares_every_step_in_definition_order()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps = [Step("login"), Step("browse"), Step("checkout")],
        };

        StepRegistry registry = new();
        new DeclarativeWorkflow(definition).RegisterSteps(registry);

        Assert.Equal(3, registry.Count);
        Assert.True(registry.TryGetId("login", out StepId login));
        Assert.True(registry.TryGetId("browse", out StepId browse));
        Assert.True(registry.TryGetId("checkout", out StepId checkout));
        Assert.True(login.Value < browse.Value);
        Assert.True(browse.Value < checkout.Value);
    }

    [Fact]
    public async Task Every_step_fires_a_request_in_order_with_its_own_method_and_path()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps =
            [
                Step("login", method: "POST", path: "/api/auth/login"),
                Step("browse", method: "GET", path: "/api/catalog/products"),
            ],
        };

        (DeclarativeWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness(definition);
        handler
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK)
            .On(HttpMethod.Get, "/api/catalog/products", HttpStatusCode.OK);

        await RunIterationAsync(workflow, context);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/auth/login", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("/api/catalog/products", handler.Requests[1].Path);
    }

    [Fact]
    public async Task A_step_with_a_body_sends_it_with_the_configured_content_type()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps = [Step("login", method: "POST", path: "/api/auth/login", body: """{"user":"demo"}""")],
        };

        (DeclarativeWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness(definition);
        handler.On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK);

        await RunIterationAsync(workflow, context);

        Assert.Equal("""{"user":"demo"}""", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Configured_headers_are_attached_to_the_request()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps = [Step("ping", headers: new Dictionary<string, string> { ["X-Client"] = "tempest" })],
        };

        (DeclarativeWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness(definition);
        handler.On(HttpMethod.Get, "/api/ping", HttpStatusCode.OK);

        await RunIterationAsync(workflow, context);

        Assert.Equal("tempest", handler.Requests[0].Header("X-Client"));
    }

    [Fact]
    public async Task Without_expected_status_codes_any_2xx_counts_as_success()
    {
        ScenarioDefinition definition = new() { Name = "smoke", Steps = [Step("ping")] };

        (DeclarativeWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness(definition);
        handler.On(HttpMethod.Get, "/api/ping", HttpStatusCode.Created);

        await RunIterationAsync(workflow, context);

        Assert.True(steps.TryGetId("ping", out StepId pingStep));
        MetricResult metric = Assert.Single(sink.For(pingStep));
        Assert.Equal(RequestOutcome.Success, metric.Outcome);
    }

    [Fact]
    public async Task Without_expected_status_codes_a_non_2xx_is_an_http_error()
    {
        ScenarioDefinition definition = new() { Name = "smoke", Steps = [Step("ping")] };

        (DeclarativeWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness(definition);
        handler.On(HttpMethod.Get, "/api/ping", HttpStatusCode.InternalServerError);

        await RunIterationAsync(workflow, context);

        Assert.True(steps.TryGetId("ping", out StepId pingStep));
        Assert.Equal(RequestOutcome.HttpError, Assert.Single(sink.For(pingStep)).Outcome);
    }

    /// <summary>
    /// Le coeur de la distinction : un 2xx non liste dans les codes attendus est un echec
    /// d'assertion, pas un succes. Le scenario a dit precisement ce qu'il attendait ; le
    /// laisser passer parce que "c'est quand meme un 2xx" reviendrait a ignorer la regle
    /// explicitement configuree.
    /// </summary>
    [Fact]
    public async Task An_explicit_expectation_overrides_the_2xx_heuristic_even_for_a_2xx_response()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps = [Step("ping", expectedStatusCodes: [201])],
        };

        (DeclarativeWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness(definition);
        handler.On(HttpMethod.Get, "/api/ping", HttpStatusCode.OK);

        await RunIterationAsync(workflow, context);

        Assert.True(steps.TryGetId("ping", out StepId pingStep));
        Assert.Equal(RequestOutcome.AssertionFailed, Assert.Single(sink.For(pingStep)).Outcome);
    }

    [Fact]
    public async Task A_response_matching_the_expected_status_list_passes()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps = [Step("ping", expectedStatusCodes: [200, 201])],
        };

        (DeclarativeWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness(definition);
        handler.On(HttpMethod.Get, "/api/ping", HttpStatusCode.Created);

        await RunIterationAsync(workflow, context);

        Assert.True(steps.TryGetId("ping", out StepId pingStep));
        Assert.Equal(RequestOutcome.Success, Assert.Single(sink.For(pingStep)).Outcome);
    }

    [Fact]
    public async Task A_connection_failure_is_reported_without_throwing()
    {
        ScenarioDefinition definition = new() { Name = "smoke", Steps = [Step("ping")] };

        (DeclarativeWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness(definition);
        handler.OnConnectionFailure(HttpMethod.Get, "/api/ping");

        Exception? escaped = await Record.ExceptionAsync(() => RunIterationAsync(workflow, context));

        Assert.Null(escaped);
        Assert.True(steps.TryGetId("ping", out StepId pingStep));
        Assert.Equal(RequestOutcome.ConnectionError, Assert.Single(sink.For(pingStep)).Outcome);
    }

    [Fact]
    public async Task A_value_extracted_by_one_step_is_available_to_the_next_via_placeholder()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps =
            [
                Step(
                    "login",
                    method: "POST",
                    path: "/api/auth/login",
                    extract: [new ExtractionRule { Variable = "token", Regex = "\"token\":\"([^\"]+)\"" }]),
                Step(
                    "checkout",
                    method: "POST",
                    path: "/api/checkout",
                    headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {{token}}" }),
            ],
        };

        (DeclarativeWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness(definition);
        handler
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, """{"token":"tok-123"}""")
            .On(HttpMethod.Post, "/api/checkout", HttpStatusCode.OK);

        await RunIterationAsync(workflow, context);

        Assert.Equal("Bearer tok-123", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task Extraction_also_substitutes_into_the_path_and_the_body()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps =
            [
                Step(
                    "create",
                    method: "POST",
                    path: "/api/orders",
                    extract: [new ExtractionRule { Variable = "orderId", Regex = "\"id\":(\\d+)" }]),
                Step(
                    "confirm",
                    method: "POST",
                    path: "/api/orders/{{orderId}}/confirm",
                    body: """{"orderId":"{{orderId}}"}"""),
            ],
        };

        (DeclarativeWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness(definition);
        handler
            .On(HttpMethod.Post, "/api/orders", HttpStatusCode.OK, """{"id":42}""")
            .On(HttpMethod.Post, "/api/orders/42/confirm", HttpStatusCode.OK);

        await RunIterationAsync(workflow, context);

        Assert.Equal("/api/orders/42/confirm", handler.Requests[1].Path);
        Assert.Equal("""{"orderId":"42"}""", handler.Requests[1].Body);
    }

    [Fact]
    public async Task A_missed_extraction_fails_the_step_even_on_a_2xx_response()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps = [Step("login", extract: [new ExtractionRule { Variable = "token", Regex = "\"token\":\"([^\"]+)\"" }])],
        };

        (DeclarativeWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness(definition);
        handler.On(HttpMethod.Get, "/api/ping", HttpStatusCode.OK, """{"other":"x"}""");

        await RunIterationAsync(workflow, context);

        Assert.True(steps.TryGetId("login", out StepId loginStep));
        Assert.Equal(RequestOutcome.AssertionFailed, Assert.Single(sink.For(loginStep)).Outcome);
    }

    [Fact]
    public async Task A_step_referencing_a_never_extracted_variable_fails_without_sending_a_request()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps = [Step("checkout", headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {{token}}" })],
        };

        (DeclarativeWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness(definition);

        await RunIterationAsync(workflow, context);

        Assert.Empty(handler.Requests);
        Assert.True(steps.TryGetId("checkout", out StepId checkoutStep));
        Assert.Equal(RequestOutcome.AssertionFailed, Assert.Single(sink.For(checkoutStep)).Outcome);
    }

    [Fact]
    public async Task Extracted_variables_do_not_leak_into_the_next_iteration()
    {
        ScenarioDefinition definition = new()
        {
            Name = "smoke",
            Steps =
            [
                Step(
                    "login",
                    method: "POST",
                    path: "/api/auth/login",
                    extract: [new ExtractionRule { Variable = "token", Regex = "\"token\":\"([^\"]+)\"" }]),
                Step(
                    "checkout",
                    method: "POST",
                    path: "/api/checkout",
                    headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {{token}}" }),
            ],
        };

        (DeclarativeWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness(definition);

        // Premiere iteration : login echoue, aucun jeton extrait.
        handler.On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.InternalServerError);
        await RunIterationAsync(workflow, context);

        // Deuxieme iteration : meme si le contexte est reutilise, l'echec de la premiere ne
        // doit pas laisser un jeton perime trainer pour celle-ci — il n'y en a simplement aucun.
        handler.On(HttpMethod.Post, "/api/checkout", HttpStatusCode.OK);
        await RunIterationAsync(workflow, context);

        Assert.True(steps.TryGetId("checkout", out StepId checkoutStep));
        // La deuxieme iteration echoue aussi, faute de jeton — la requete de checkout n'est
        // jamais envoyee, dans les deux iterations.
        Assert.Equal(2, sink.For(checkoutStep).Count());
        Assert.All(sink.For(checkoutStep), m => Assert.Equal(RequestOutcome.AssertionFailed, m.Outcome));
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/checkout");
    }

    [Fact]
    public void An_invalid_definition_is_rejected_at_construction() =>
        Assert.Throws<ArgumentException>(() => new DeclarativeWorkflow(new ScenarioDefinition { Name = "smoke", Steps = [] }));

    [Fact]
    public void A_null_definition_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => new DeclarativeWorkflow(null!));

    [Fact]
    public void Name_reflects_the_definitions_name()
    {
        ScenarioDefinition definition = new() { Name = "custom-name", Steps = [Step("ping")] };

        Assert.Equal("custom-name", new DeclarativeWorkflow(definition).Name);
    }
}