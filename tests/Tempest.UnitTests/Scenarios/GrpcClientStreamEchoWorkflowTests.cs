using Tempest.Application.Execution;
using Tempest.Domain.Metrics;
using Tempest.Scenarios;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.Scenarios;

public sealed class GrpcClientStreamEchoWorkflowTests
{
    private const int MESSAGE_COUNT = 3;

    private static readonly TimeSpan _guardTimeout = TimeSpan.FromSeconds(5);

    private static async Task RunIterationAsync(
        GrpcClientStreamEchoWorkflow workflow,
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
        GrpcClientStreamEchoWorkflow Workflow,
        VirtualUserContext Context,
        CollectingMetricSink Sink,
        StepRegistry Steps)
        CreateHarness(Uri httpBaseAddress, int virtualUserId = 0)
    {
        GrpcClientStreamEchoWorkflow workflow = new(new GrpcEchoWorkflowOptions { MessageCount = MESSAGE_COUNT });

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
    public void RegisterSteps_declares_exactly_the_upload_step()
    {
        StepRegistry registry = new();
        new GrpcClientStreamEchoWorkflow().RegisterSteps(registry);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetId(GrpcClientStreamEchoSteps.UPLOAD, out _));
    }

    [Fact]
    public async Task A_happy_path_iteration_uploads_every_message_and_reports_one_success()
    {
        await using GrpcEchoTestServer server = await GrpcEchoTestServer.StartAsync();

        (GrpcClientStreamEchoWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StepRegistry steps) =
            CreateHarness(server.Endpoint);

        try
        {
            using CancellationTokenSource cts = new(_guardTimeout);
            await RunIterationAsync(workflow, context, iterationIndex: 0, cts.Token);

            Assert.All(sink.Results, m => Assert.True(m.IsSuccess));

            Assert.True(steps.TryGetId(GrpcClientStreamEchoSteps.UPLOAD, out StepId uploadStep));
            MetricResult upload = Assert.Single(sink.For(uploadStep));
            Assert.True(upload.BytesReceived > 0);
        }
        finally
        {
            await workflow.TearDownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Several_iterations_each_upload_their_own_stream_and_reuse_the_channel()
    {
        await using GrpcEchoTestServer server = await GrpcEchoTestServer.StartAsync();

        (GrpcClientStreamEchoWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StepRegistry steps) =
            CreateHarness(server.Endpoint);

        try
        {
            using CancellationTokenSource cts = new(_guardTimeout);
            await RunIterationAsync(workflow, context, iterationIndex: 0, cts.Token);
            await RunIterationAsync(workflow, context, iterationIndex: 1, cts.Token);

            Assert.True(steps.TryGetId(GrpcClientStreamEchoSteps.UPLOAD, out StepId uploadStep));
            Assert.Equal(2, sink.For(uploadStep).Count());
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
        // Rien n'ecoute sur ce port loopback : le canal doit signaler une indisponibilite au
        // plus tard a l'attente de la reponse recapitulative.
        (GrpcClientStreamEchoWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StepRegistry steps) =
            CreateHarness(new Uri("http://127.0.0.1:1/"));

        try
        {
            using CancellationTokenSource cts = new(_guardTimeout);
            Exception? escaped = await Record.ExceptionAsync(() => RunIterationAsync(workflow, context, iterationIndex: 0, cts.Token));

            Assert.Null(escaped);

            Assert.True(steps.TryGetId(GrpcClientStreamEchoSteps.UPLOAD, out StepId uploadStep));
            MetricResult metric = Assert.Single(sink.For(uploadStep));
            Assert.Equal(RequestOutcome.ConnectionError, metric.Outcome);
        }
        finally
        {
            await workflow.TearDownAsync(CancellationToken.None);
        }
    }
}