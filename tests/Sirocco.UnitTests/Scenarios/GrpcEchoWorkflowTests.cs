using Sirocco.Application.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Scenarios;
using Sirocco.UnitTests.TestDoubles;

namespace Sirocco.UnitTests.Scenarios;

public sealed class GrpcEchoWorkflowTests
{
    // Garde-fou contre un blocage indefini, pas une borne de latence : genereux a dessein, car a
    // 5 s la contention CPU de la suite en parallele le faisait sauter sur le demarrage d'un vrai
    // serveur gRPC, produisant un echec rouge sans aucun bug derriere.
    private static readonly TimeSpan _guardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reproduit exactement ce que fait <c>VirtualUserWorker</c> : ouverture, execution du
    /// scenario, cloture avec l'issue que le scenario a laisse remonter par exception.
    /// </summary>
    private static async Task RunIterationAsync(
        GrpcEchoWorkflow workflow,
        VirtualUserContext context,
        long iterationIndex,
        CancellationToken cancellationToken)
    {
        ExecutionToken token = new(iterationIndex, ScheduledTicks: 0L);
        context.BeginIteration(in token, startedTicks: 0L, cancellationToken);
        await workflow.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        context.EndIteration(startedTicks: 0L, RequestOutcome.Success);
    }

    private static (
        GrpcEchoWorkflow Workflow,
        VirtualUserContext Context,
        CollectingMetricSink Sink,
        StepRegistry Steps)
        CreateHarness(Uri httpBaseAddress, int virtualUserId = 0)
    {
        GrpcEchoWorkflow workflow = new();

        StepRegistry registry = new();
        StepId iterationStep = registry.Register(WellKnownSteps.ITERATION);
        workflow.RegisterSteps(registry);
        registry.Seal();

        CollectingMetricSink sink = new();
        HttpClient client = new() { BaseAddress = httpBaseAddress };
        VirtualUserContext context = new(virtualUserId, client, sink, iterationStep);

        return (workflow, context, sink, registry);
    }

    [Fact]
    public void RegisterSteps_declares_exactly_the_ping_step()
    {
        StepRegistry registry = new();
        new GrpcEchoWorkflow().RegisterSteps(registry);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetId(GrpcEchoSteps.PING, out _));
    }

    [Fact]
    public async Task A_happy_path_iteration_pings_successfully()
    {
        await using GrpcEchoTestServer server = await GrpcEchoTestServer.StartAsync();

        (GrpcEchoWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StepRegistry steps) =
            CreateHarness(server.Endpoint);

        try
        {
            using CancellationTokenSource cts = new(_guardTimeout);
            await RunIterationAsync(workflow, context, iterationIndex: 0, cts.Token);

            Assert.All(sink.Results, m => Assert.True(m.IsSuccess));

            Assert.True(steps.TryGetId(GrpcEchoSteps.PING, out StepId pingStep));
            MetricResult pingMetric = Assert.Single(sink.For(pingStep));
            Assert.True(pingMetric.BytesReceived > 0);
        }
        finally
        {
            await workflow.TearDownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Several_iterations_reuse_the_same_channel()
    {
        await using GrpcEchoTestServer server = await GrpcEchoTestServer.StartAsync();

        (GrpcEchoWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StepRegistry steps) =
            CreateHarness(server.Endpoint);

        try
        {
            using CancellationTokenSource cts = new(_guardTimeout);
            await RunIterationAsync(workflow, context, iterationIndex: 0, cts.Token);
            await RunIterationAsync(workflow, context, iterationIndex: 1, cts.Token);

            Assert.True(steps.TryGetId(GrpcEchoSteps.PING, out StepId pingStep));
            Assert.Equal(2, sink.For(pingStep).Count());
            Assert.All(sink.Results, m => Assert.True(m.IsSuccess));
        }
        finally
        {
            await workflow.TearDownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_connection_failure_is_reported_without_throwing()
    {
        // Rien n'ecoute sur ce port loopback : le canal doit signaler une indisponibilite.
        (GrpcEchoWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StepRegistry steps) =
            CreateHarness(new Uri("http://127.0.0.1:1/"));

        try
        {
            using CancellationTokenSource cts = new(_guardTimeout);
            Exception? escaped = await Record.ExceptionAsync(() => RunIterationAsync(workflow, context, iterationIndex: 0, cts.Token));

            Assert.Null(escaped);

            Assert.True(steps.TryGetId(GrpcEchoSteps.PING, out StepId pingStep));
            MetricResult pingMetric = Assert.Single(sink.For(pingStep));
            Assert.Equal(RequestOutcome.ConnectionError, pingMetric.Outcome);
        }
        finally
        {
            await workflow.TearDownAsync(CancellationToken.None);
        }
    }
}