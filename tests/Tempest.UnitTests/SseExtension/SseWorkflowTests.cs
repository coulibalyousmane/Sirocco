using Tempest.Application.Execution;
using Tempest.Domain.Metrics;
using Tempest.Extensions.Sse;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.SseExtension;

/// <summary>
/// Verifie <see cref="SseWorkflow"/> contre un vrai serveur Kestrel en boucle locale
/// (<see cref="SseTestServer"/>) — le protocole de reference SSE de la roadmap phase 6 n'a de sens
/// teste que contre un vrai flux HTTP lu au fil de l'eau, pas un double qui court-circuite la
/// lecture par ligne.
/// </summary>
public sealed class SseWorkflowTests
{
    private const string PATH_ENVIRONMENT_VARIABLE = "TEMPEST_SSE_PLUGIN_PATH";
    private const string EVENT_COUNT_ENVIRONMENT_VARIABLE = "TEMPEST_SSE_PLUGIN_EVENT_COUNT";
    private const string TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE = "TEMPEST_SSE_PLUGIN_TIMEOUT_SECONDS";

    [Fact]
    public void RegisterSteps_declares_exactly_the_two_named_steps()
    {
        StepRegistry registry = new();
        new SseWorkflow().RegisterSteps(registry);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGetId("SSE connect", out _));
        Assert.True(registry.TryGetId("SSE receive events", out _));
    }

    [Fact]
    public async Task ExecuteAsync_completes_both_steps_successfully_when_the_expected_event_count_matches()
    {
        await using SseTestServer server = await SseTestServer.StartAsync();
        SseWorkflow workflow = CreateWorkflow(path: "/events", eventCount: 5);
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
    public async Task An_unmapped_path_fails_the_connect_step_with_the_http_status()
    {
        await using SseTestServer server = await SseTestServer.StartAsync();
        SseWorkflow workflow = CreateWorkflow(path: "/missing", eventCount: 5);
        (VirtualUserContext context, CollectingMetricSink sink, _) = CreateHarness(workflow, server.Endpoint);

        await RunIterationAsync(workflow, context);

        MetricResult connectResult = sink.Results.First();
        Assert.Equal(RequestOutcome.HttpError, connectResult.Outcome);
        Assert.Equal(404, connectResult.StatusCode);
    }

    [Fact]
    public async Task A_response_with_the_wrong_content_type_fails_the_connect_step_as_an_assertion()
    {
        await using SseTestServer server = await SseTestServer.StartAsync();
        SseWorkflow workflow = CreateWorkflow(path: "/wrong-content-type", eventCount: 5);
        (VirtualUserContext context, CollectingMetricSink sink, _) = CreateHarness(workflow, server.Endpoint);

        await RunIterationAsync(workflow, context);

        MetricResult connectResult = sink.Results.First();
        Assert.Equal(RequestOutcome.AssertionFailed, connectResult.Outcome);
    }

    [Fact]
    public async Task A_mismatched_event_count_fails_the_receive_step_as_an_assertion()
    {
        await using SseTestServer server = await SseTestServer.StartAsync();
        // /events-fixed-count ignore le "?count=" envoye par le workflow et sert toujours 5
        // evenements : demander 10 reproduit le desaccord sans dependre d'un alea reseau.
        SseWorkflow workflow = CreateWorkflow(path: "/events-fixed-count", eventCount: 10);
        (VirtualUserContext context, CollectingMetricSink sink, _) = CreateHarness(workflow, server.Endpoint);

        await RunIterationAsync(workflow, context);

        MetricResult receiveResult = sink.Results.Skip(1).First();
        Assert.Equal(RequestOutcome.AssertionFailed, receiveResult.Outcome);
    }

    [Fact]
    public async Task A_stream_that_never_completes_times_out_and_fails_the_receive_step()
    {
        await using SseTestServer server = await SseTestServer.StartAsync();
        SseWorkflow workflow = CreateWorkflow(path: "/never-ends", eventCount: 5, timeoutSeconds: 1);
        (VirtualUserContext context, CollectingMetricSink sink, _) = CreateHarness(workflow, server.Endpoint);

        await RunIterationAsync(workflow, context);

        MetricResult receiveResult = sink.Results.Skip(1).First();
        Assert.Equal(RequestOutcome.Timeout, receiveResult.Outcome);
    }

    private static SseWorkflow CreateWorkflow(string path, int eventCount, int? timeoutSeconds = null)
    {
        Environment.SetEnvironmentVariable(PATH_ENVIRONMENT_VARIABLE, path);
        Environment.SetEnvironmentVariable(EVENT_COUNT_ENVIRONMENT_VARIABLE, eventCount.ToString());
        Environment.SetEnvironmentVariable(TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE, timeoutSeconds?.ToString());
        try
        {
            return new SseWorkflow();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PATH_ENVIRONMENT_VARIABLE, null);
            Environment.SetEnvironmentVariable(EVENT_COUNT_ENVIRONMENT_VARIABLE, null);
            Environment.SetEnvironmentVariable(TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE, null);
        }
    }

    /// <summary>Meme reproduction du cycle de vie que <c>VirtualUserWorker</c>, voir DynamicCheckoutWorkflowTests.</summary>
    private static async Task RunIterationAsync(SseWorkflow workflow, VirtualUserContext context)
    {
        ExecutionToken token = new(IterationIndex: 0, ScheduledTicks: 0L);
        context.BeginIteration(in token, startedTicks: 0L, CancellationToken.None);
        await workflow.ExecuteAsync(context, CancellationToken.None).ConfigureAwait(false);
        context.EndIteration(startedTicks: 0L, RequestOutcome.Success);
    }

    private static (VirtualUserContext Context, CollectingMetricSink Sink, StepId IterationStep) CreateHarness(SseWorkflow workflow, Uri baseAddress)
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