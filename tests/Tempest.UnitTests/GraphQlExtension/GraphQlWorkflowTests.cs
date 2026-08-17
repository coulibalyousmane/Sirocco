using Tempest.Application.Execution;
using Tempest.Domain.Metrics;
using Tempest.Extensions.GraphQl;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.GraphQlExtension;

/// <summary>
/// Verifie <see cref="GraphQlWorkflow"/> contre un vrai serveur GraphQL en boucle locale
/// (<see cref="GraphQlTestServer"/>) — le protocole de reference GraphQL de la roadmap phase 6 n'a
/// de sens teste que contre un vrai moteur d'execution, capable de renvoyer une erreur en HTTP 200,
/// pas un double qui court-circuite le corps de la reponse.
/// </summary>
public sealed class GraphQlWorkflowTests
{
    private const string PRODUCT_ID_MAX_ENVIRONMENT_VARIABLE = "TEMPEST_GRAPHQL_PLUGIN_PRODUCT_ID_MAX";

    [Fact]
    public void RegisterSteps_declares_exactly_the_two_named_steps()
    {
        StepRegistry registry = new();
        new GraphQlWorkflow().RegisterSteps(registry);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGetId("GraphQL query", out _));
        Assert.True(registry.TryGetId("GraphQL mutation", out _));
    }

    [Fact]
    public async Task ExecuteAsync_completes_both_steps_successfully_when_the_product_id_is_valid()
    {
        await using GraphQlTestServer server = await GraphQlTestServer.StartAsync();
        // GraphQlTestServer ne connait que le produit 1 : borner l'identifiant tire a 1 evite tout
        // alea sur la mutation, sans changer la nature du test (le tirage lui-meme reste exerce).
        GraphQlWorkflow workflow = CreateWorkflow(productIdMax: 1);
        (VirtualUserContext context, CollectingMetricSink sink, StepId iterationStep) = CreateHarness(workflow, server.Endpoint);

        await RunIterationAsync(workflow, context);

        // 3 mesures publiees : l'etape technique __iteration (EndIteration) plus les deux etapes
        // du scenario — seules ces deux dernieres sont l'objet de ce test.
        Assert.Equal(3, sink.Results.Count);
        Assert.All(
            sink.Results.Where(result => result.Step != iterationStep),
            static result => Assert.Equal(RequestOutcome.Success, result.Outcome));
    }

    [Fact]
    public async Task A_mutation_error_returned_in_the_body_with_http_200_fails_as_an_assertion()
    {
        // Le catalogue du double ne connait que le produit 999 : un tirage borne a [1, 1] par
        // TEMPEST_GRAPHQL_PLUGIN_PRODUCT_ID_MAX tombe toujours sur un identifiant absent,
        // deterministe sans devoir truquer le tirage aleatoire lui-meme.
        await using GraphQlTestServer server = await GraphQlTestServer.StartAsync(validProductId: 999);
        GraphQlWorkflow workflow = CreateWorkflow(productIdMax: 1);
        (VirtualUserContext context, CollectingMetricSink sink, StepId iterationStep) = CreateHarness(workflow, server.Endpoint);

        await RunIterationAsync(workflow, context);

        MetricResult mutationResult = sink.Results.Where(result => result.Step != iterationStep).Skip(1).First();
        Assert.Equal(RequestOutcome.AssertionFailed, mutationResult.Outcome);
        Assert.Equal(200, mutationResult.StatusCode);
    }

    private static GraphQlWorkflow CreateWorkflow(int productIdMax)
    {
        Environment.SetEnvironmentVariable(PRODUCT_ID_MAX_ENVIRONMENT_VARIABLE, productIdMax.ToString());
        try
        {
            return new GraphQlWorkflow();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PRODUCT_ID_MAX_ENVIRONMENT_VARIABLE, null);
        }
    }

    /// <summary>Meme reproduction du cycle de vie que <c>VirtualUserWorker</c>, voir DynamicCheckoutWorkflowTests.</summary>
    private static async Task RunIterationAsync(GraphQlWorkflow workflow, VirtualUserContext context)
    {
        ExecutionToken token = new(IterationIndex: 0, ScheduledTicks: 0L);
        context.BeginIteration(in token, startedTicks: 0L, CancellationToken.None);
        await workflow.ExecuteAsync(context, CancellationToken.None).ConfigureAwait(false);
        context.EndIteration(startedTicks: 0L, RequestOutcome.Success);
    }

    private static (VirtualUserContext Context, CollectingMetricSink Sink, StepId IterationStep) CreateHarness(GraphQlWorkflow workflow, Uri baseAddress)
    {
        HttpClient client = new() { BaseAddress = baseAddress };

        StepRegistry registry = new();
        StepId iterationStep = registry.Register(WellKnownSteps.ITERATION);
        workflow.RegisterSteps(registry);
        registry.Seal();

        CollectingMetricSink sink = new();
        VirtualUserContext context = new(virtualUserId: 0, client, sink, iterationStep);

        return (context, sink, iterationStep);
    }
}