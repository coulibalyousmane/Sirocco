using System.Net.WebSockets;
using Tempest.Application.Execution;
using Tempest.Domain.Metrics;
using Tempest.Scenarios;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.Scenarios;

public sealed class WebSocketEchoWorkflowTests
{
    private static readonly TimeSpan _guardTimeout = TimeSpan.FromSeconds(5);

    private static async Task EchoLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reproduit exactement ce que fait <c>VirtualUserWorker</c> : ouverture, execution du
    /// scenario, cloture avec l'issue que le scenario a laisse remonter par exception.
    /// </summary>
    private static async Task RunIterationAsync(
        WebSocketEchoWorkflow workflow,
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
        WebSocketEchoWorkflow Workflow,
        VirtualUserContext Context,
        CollectingMetricSink Sink,
        StepRegistry Steps)
        CreateHarness(Uri httpBaseAddress, int virtualUserId = 0, WebSocketEchoWorkflowOptions? options = null)
    {
        WebSocketEchoWorkflow workflow = new(options);

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
    public void RegisterSteps_declares_exactly_the_two_named_steps()
    {
        StepRegistry registry = new();
        new WebSocketEchoWorkflow().RegisterSteps(registry);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGetId(WebSocketEchoSteps.CONNECT, out _));
        Assert.True(registry.TryGetId(WebSocketEchoSteps.ECHO, out _));
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WebSocketEchoWorkflow(new WebSocketEchoWorkflowOptions { EchoPath = "" }));
    }

    [Fact]
    public async Task A_happy_path_iteration_connects_exchanges_and_closes_successfully()
    {
        await using LoopbackWebSocketServer server = new(EchoLoopAsync);
        Uri httpBaseAddress = new($"http://{server.Endpoint.Authority}/");

        (WebSocketEchoWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StepRegistry steps) =
            CreateHarness(httpBaseAddress);

        using CancellationTokenSource cts = new(_guardTimeout);
        await RunIterationAsync(workflow, context, iterationIndex: 0, cts.Token);

        Assert.All(sink.Results, m => Assert.True(m.IsSuccess));

        Assert.True(steps.TryGetId(WebSocketEchoSteps.CONNECT, out StepId connectStep));
        Assert.Single(sink.For(connectStep));

        Assert.True(steps.TryGetId(WebSocketEchoSteps.ECHO, out StepId echoStep));
        MetricResult echoMetric = Assert.Single(sink.For(echoStep));
        Assert.True(echoMetric.BytesReceived > 0);
    }

    [Fact]
    public async Task Several_iterations_each_open_their_own_connection()
    {
        await using LoopbackWebSocketServer server = new(EchoLoopAsync);
        Uri httpBaseAddress = new($"http://{server.Endpoint.Authority}/");

        (WebSocketEchoWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StepRegistry steps) =
            CreateHarness(httpBaseAddress);

        using CancellationTokenSource cts = new(_guardTimeout);
        await RunIterationAsync(workflow, context, iterationIndex: 0, cts.Token);
        await RunIterationAsync(workflow, context, iterationIndex: 1, cts.Token);

        Assert.True(steps.TryGetId(WebSocketEchoSteps.ECHO, out StepId echoStep));
        Assert.Equal(2, sink.For(echoStep).Count());
        Assert.All(sink.Results, m => Assert.True(m.IsSuccess));
    }

    [Fact]
    public async Task A_connection_failure_is_reported_without_throwing()
    {
        // Rien n'ecoute sur ce port loopback : la connexion doit etre refusee rapidement.
        (WebSocketEchoWorkflow workflow, VirtualUserContext context, CollectingMetricSink sink, StepRegistry steps) =
            CreateHarness(new Uri("http://127.0.0.1:1/"));

        using CancellationTokenSource cts = new(_guardTimeout);
        Exception? escaped = await Record.ExceptionAsync(() => RunIterationAsync(workflow, context, iterationIndex: 0, cts.Token));

        Assert.Null(escaped);

        Assert.True(steps.TryGetId(WebSocketEchoSteps.CONNECT, out StepId connectStep));
        MetricResult connectMetric = Assert.Single(sink.For(connectStep));
        Assert.Equal(RequestOutcome.ConnectionError, connectMetric.Outcome);

        Assert.True(steps.TryGetId(WebSocketEchoSteps.ECHO, out StepId echoStep));
        Assert.Empty(sink.For(echoStep));
    }
}