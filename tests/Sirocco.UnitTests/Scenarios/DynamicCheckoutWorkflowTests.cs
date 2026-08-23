using System.Net;
using System.Text.Json;
using Sirocco.Application.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Scenarios;
using Sirocco.Scenarios.Contracts;
using Sirocco.UnitTests.TestDoubles;

namespace Sirocco.UnitTests.Scenarios;

public sealed class DynamicCheckoutWorkflowTests
{
    // Les noms de propriete suivent la politique camelCase declaree explicitement sur
    // CheckoutJsonContext : c'est ce contrat, pas un defaut implicite, que ces litteraux verifient.
    private const string LOGIN_JSON = """{"token":"tok-1"}""";
    private const string PRODUCTS_JSON = """[{"id":1,"name":"Widget","price":9.99},{"id":2,"name":"Gadget","price":19.99}]""";
    private const string EMPTY_PRODUCTS_JSON = "[]";
    private const string CHECKOUT_JSON = """{"orderId":"ord-1","total":9.99}""";

    /// <summary>
    /// Deroule une iteration complete en reproduisant exactement ce que fait
    /// <c>VirtualUserWorker</c> : ouverture, execution du scenario, cloture avec l'issue
    /// que le scenario a laisse remonter par exception (ici, jamais).
    /// </summary>
    private static async Task RunIterationAsync(
        DynamicCheckoutWorkflow workflow,
        VirtualUserContext context,
        long iterationIndex,
        CancellationToken cancellationToken = default)
    {
        ExecutionToken token = new(iterationIndex, ScheduledTicks: 0L);
        context.BeginIteration(in token, startedTicks: 0L, cancellationToken);
        await workflow.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        context.EndIteration(startedTicks: 0L, RequestOutcome.Success);
    }

    /// <summary>
    /// Assemble un scenario, son contexte et son registre d'etapes en reproduisant l'ordre
    /// d'enregistrement de <c>TargetRpsLoadEngine.PrepareSteps</c> : l'etape technique
    /// d'iteration en premier, avant celles du scenario.
    /// </summary>
    private static (
        DynamicCheckoutWorkflow Workflow,
        VirtualUserContext Context,
        CollectingMetricSink Sink,
        StubHttpMessageHandler Handler,
        StepRegistry Steps)
        CreateHarness(int virtualUserId = 0, DynamicCheckoutWorkflowOptions? options = null)
    {
        StubHttpMessageHandler handler = new();
        HttpClient client = new(handler) { BaseAddress = new Uri("https://target.example") };

        DynamicCheckoutWorkflow workflow = new(options);

        StepRegistry registry = new();
        StepId iterationStep = registry.Register(WellKnownSteps.ITERATION);
        workflow.RegisterSteps(registry);
        registry.Seal();

        CollectingMetricSink sink = new();
        VirtualUserContext context = new(virtualUserId, client, sink, iterationStep);

        return (workflow, context, sink, handler, registry);
    }

    [Fact]
    public void RegisterSteps_declares_exactly_the_three_named_steps()
    {
        StepRegistry registry = new();
        new DynamicCheckoutWorkflow().RegisterSteps(registry);

        Assert.Equal(3, registry.Count);
        Assert.True(registry.TryGetId(DynamicCheckoutSteps.LOGIN, out _));
        Assert.True(registry.TryGetId(DynamicCheckoutSteps.BROWSE, out _));
        Assert.True(registry.TryGetId(DynamicCheckoutSteps.CHECKOUT, out _));
    }

    [Fact]
    public async Task SetUpAsync_generates_the_account_pool_without_any_network_call()
    {
        (DynamicCheckoutWorkflow workflow, _, _, StubHttpMessageHandler handler, _) = CreateHarness(
            options: new DynamicCheckoutWorkflowOptions { UserPoolSize = 10 });

        await workflow.SetUpAsync(CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_happy_path_iteration_logs_in_browses_then_checks_out_in_order()
    {
        (DynamicCheckoutWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness();

        handler
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH, HttpStatusCode.OK, LOGIN_JSON)
            .On(HttpMethod.Get, DynamicCheckoutWorkflowOptions.DEFAULT_PRODUCTS_PATH, HttpStatusCode.OK, PRODUCTS_JSON)
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_CHECKOUT_PATH, HttpStatusCode.OK, CHECKOUT_JSON);

        await workflow.SetUpAsync(CancellationToken.None);
        await RunIterationAsync(workflow, context, iterationIndex: 0);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH, handler.Requests[0].Path);
        Assert.Equal(DynamicCheckoutWorkflowOptions.DEFAULT_PRODUCTS_PATH, handler.Requests[1].Path);
        Assert.Equal(DynamicCheckoutWorkflowOptions.DEFAULT_CHECKOUT_PATH, handler.Requests[2].Path);

        Assert.All(sink.Results, m => Assert.True(m.IsSuccess));

        Assert.True(steps.TryGetId(WellKnownSteps.ITERATION, out StepId iterationStep));
        Assert.Single(sink.For(iterationStep));
    }

    [Fact]
    public async Task The_checkout_request_carries_the_token_obtained_at_login()
    {
        (DynamicCheckoutWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness();

        handler
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH, HttpStatusCode.OK, LOGIN_JSON)
            .On(HttpMethod.Get, DynamicCheckoutWorkflowOptions.DEFAULT_PRODUCTS_PATH, HttpStatusCode.OK, PRODUCTS_JSON)
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_CHECKOUT_PATH, HttpStatusCode.OK, CHECKOUT_JSON);

        await workflow.SetUpAsync(CancellationToken.None);
        await RunIterationAsync(workflow, context, iterationIndex: 0);

        CapturedRequest checkout = handler.Requests[2];
        Assert.Equal("Bearer tok-1", checkout.AuthorizationHeader);
    }

    [Fact]
    public async Task The_cart_only_references_products_the_catalog_actually_returned()
    {
        (DynamicCheckoutWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness(
            options: new DynamicCheckoutWorkflowOptions { MaxCartItems = 5 });

        handler
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH, HttpStatusCode.OK, LOGIN_JSON)
            .On(HttpMethod.Get, DynamicCheckoutWorkflowOptions.DEFAULT_PRODUCTS_PATH, HttpStatusCode.OK, PRODUCTS_JSON)
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_CHECKOUT_PATH, HttpStatusCode.OK, CHECKOUT_JSON);

        await workflow.SetUpAsync(CancellationToken.None);
        await RunIterationAsync(workflow, context, iterationIndex: 0);

        CheckoutRequest cart = JsonSerializer.Deserialize(
            handler.Requests[2].Body!,
            CheckoutJsonContext.Default.CheckoutRequest)!;

        // Le catalogue stub ne connait que les produits 1 et 2 : au plus deux lignes distinctes.
        Assert.InRange(cart.Items.Count, 1, 2);
        int[] knownProductIds = [1, 2];
        Assert.All(cart.Items, item => Assert.Contains(item.ProductId, knownProductIds));
    }

    [Fact]
    public async Task A_second_iteration_reuses_the_cached_token_instead_of_logging_in_again()
    {
        (DynamicCheckoutWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness();

        handler
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH, HttpStatusCode.OK, LOGIN_JSON)
            .On(HttpMethod.Get, DynamicCheckoutWorkflowOptions.DEFAULT_PRODUCTS_PATH, HttpStatusCode.OK, PRODUCTS_JSON)
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_CHECKOUT_PATH, HttpStatusCode.OK, CHECKOUT_JSON);

        await workflow.SetUpAsync(CancellationToken.None);
        await RunIterationAsync(workflow, context, iterationIndex: 0);
        await RunIterationAsync(workflow, context, iterationIndex: 1);

        int loginCalls = handler.Requests.Count(r => r.Path == DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH);
        Assert.Equal(1, loginCalls);

        // 3 appels a la premiere iteration (login + browse + checkout), 2 a la seconde
        // (le jeton en cache dispense de se reconnecter).
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task A_401_at_checkout_forces_a_fresh_login_on_the_next_iteration()
    {
        (DynamicCheckoutWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness();

        handler
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH, HttpStatusCode.OK, LOGIN_JSON)
            .On(HttpMethod.Get, DynamicCheckoutWorkflowOptions.DEFAULT_PRODUCTS_PATH, HttpStatusCode.OK, PRODUCTS_JSON)
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_CHECKOUT_PATH, HttpStatusCode.Unauthorized);

        await workflow.SetUpAsync(CancellationToken.None);
        await RunIterationAsync(workflow, context, iterationIndex: 0);
        await RunIterationAsync(workflow, context, iterationIndex: 1);

        int loginCalls = handler.Requests.Count(r => r.Path == DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH);
        Assert.Equal(2, loginCalls);
    }

    [Fact]
    public async Task A_failed_login_aborts_the_iteration_before_any_other_call()
    {
        (DynamicCheckoutWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness();

        handler.On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH, HttpStatusCode.InternalServerError);

        await workflow.SetUpAsync(CancellationToken.None);
        await RunIterationAsync(workflow, context, iterationIndex: 0);

        Assert.Single(handler.Requests);

        Assert.True(steps.TryGetId(WellKnownSteps.ITERATION, out StepId iterationStep));
        MetricResult iterationMetric = Assert.Single(sink.For(iterationStep));

        // Le scenario n'a pas leve : c'est l'echec de l'etape login qui doit se voir remonter
        // jusqu'a l'issue de l'iteration, sans quoi le rapport afficherait un succes.
        Assert.Equal(RequestOutcome.HttpError, iterationMetric.Outcome);
    }

    [Fact]
    public async Task A_failed_browse_skips_checkout_but_keeps_the_session_token()
    {
        (DynamicCheckoutWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness();

        handler
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH, HttpStatusCode.OK, LOGIN_JSON)
            .On(HttpMethod.Get, DynamicCheckoutWorkflowOptions.DEFAULT_PRODUCTS_PATH, HttpStatusCode.ServiceUnavailable);

        await workflow.SetUpAsync(CancellationToken.None);
        await RunIterationAsync(workflow, context, iterationIndex: 0);

        Assert.Equal(2, handler.Requests.Count);

        // La session garde son jeton : la prochaine iteration ne devrait pas se reconnecter.
        handler.On(HttpMethod.Get, DynamicCheckoutWorkflowOptions.DEFAULT_PRODUCTS_PATH, HttpStatusCode.OK, PRODUCTS_JSON);
        handler.On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_CHECKOUT_PATH, HttpStatusCode.OK, CHECKOUT_JSON);

        await RunIterationAsync(workflow, context, iterationIndex: 1);

        int loginCalls = handler.Requests.Count(r => r.Path == DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH);
        Assert.Equal(1, loginCalls);
    }

    [Fact]
    public async Task An_empty_catalog_skips_checkout()
    {
        (DynamicCheckoutWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness();

        handler
            .On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH, HttpStatusCode.OK, LOGIN_JSON)
            .On(HttpMethod.Get, DynamicCheckoutWorkflowOptions.DEFAULT_PRODUCTS_PATH, HttpStatusCode.OK, EMPTY_PRODUCTS_JSON);

        await workflow.SetUpAsync(CancellationToken.None);
        await RunIterationAsync(workflow, context, iterationIndex: 0);

        Assert.DoesNotContain(handler.Requests, r => r.Path == DynamicCheckoutWorkflowOptions.DEFAULT_CHECKOUT_PATH);
    }

    [Fact]
    public async Task A_connection_failure_is_reported_without_throwing()
    {
        (DynamicCheckoutWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StubHttpMessageHandler handler, StepRegistry steps) =
            CreateHarness();

        handler.OnConnectionFailure(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH);

        await workflow.SetUpAsync(CancellationToken.None);
        Exception? escaped = await Record.ExceptionAsync(() => RunIterationAsync(workflow, context, iterationIndex: 0));

        Assert.Null(escaped);

        Assert.True(steps.TryGetId(DynamicCheckoutSteps.LOGIN, out StepId loginStep));
        MetricResult loginMetric = Assert.Single(sink.For(loginStep));
        Assert.Equal(RequestOutcome.ConnectionError, loginMetric.Outcome);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(4, 0)]
    [InlineData(7, 3)]
    public async Task Virtual_users_are_assigned_accounts_by_wrapping_the_pool(int virtualUserId, int expectedUserIndex)
    {
        (DynamicCheckoutWorkflow workflow, VirtualUserContext context, _, StubHttpMessageHandler handler, _) = CreateHarness(
            virtualUserId,
            new DynamicCheckoutWorkflowOptions { UserPoolSize = 4 });

        handler.On(HttpMethod.Post, DynamicCheckoutWorkflowOptions.DEFAULT_LOGIN_PATH, HttpStatusCode.OK, LOGIN_JSON);
        handler.On(HttpMethod.Get, DynamicCheckoutWorkflowOptions.DEFAULT_PRODUCTS_PATH, HttpStatusCode.OK, EMPTY_PRODUCTS_JSON);

        await workflow.SetUpAsync(CancellationToken.None);
        await RunIterationAsync(workflow, context, iterationIndex: 0);

        CheckoutSession session = (CheckoutSession)context.State!;
        Assert.Equal(expectedUserIndex, session.UserIndex);
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DynamicCheckoutWorkflow(new DynamicCheckoutWorkflowOptions { LoginPath = "" }));
        Assert.Throws<ArgumentException>(() => new DynamicCheckoutWorkflow(new DynamicCheckoutWorkflowOptions { UserPoolSize = 0 }));
        Assert.Throws<ArgumentException>(() => new DynamicCheckoutWorkflow(new DynamicCheckoutWorkflowOptions { MaxCartItems = 0 }));
    }
}