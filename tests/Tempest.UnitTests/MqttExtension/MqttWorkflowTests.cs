using System.Net;
using System.Net.Sockets;
using Tempest.Application.Execution;
using Tempest.Domain.Metrics;
using Tempest.Extensions.Mqtt;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.MqttExtension;

/// <summary>
/// Verifie <see cref="MqttWorkflow"/> contre un vrai courtier MQTT en boucle locale
/// (<see cref="MqttTestBroker"/>) — le protocole de reference MQTT de la roadmap phase 6 n'a de
/// sens teste que contre un vrai aller-retour publication/abonnement, pas un double qui court-
/// circuite le courtier.
/// </summary>
public sealed class MqttWorkflowTests
{
    private const string HOST_ENVIRONMENT_VARIABLE = "TEMPEST_MQTT_PLUGIN_HOST";
    private const string PORT_ENVIRONMENT_VARIABLE = "TEMPEST_MQTT_PLUGIN_PORT";
    private const string TOPIC_PREFIX_ENVIRONMENT_VARIABLE = "TEMPEST_MQTT_PLUGIN_TOPIC_PREFIX";
    private const string TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE = "TEMPEST_MQTT_PLUGIN_TIMEOUT_SECONDS";

    [Fact]
    public void RegisterSteps_declares_exactly_the_two_named_steps()
    {
        StepRegistry registry = new();
        new MqttWorkflow().RegisterSteps(registry);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGetId("MQTT connect", out _));
        Assert.True(registry.TryGetId("MQTT publish/receive", out _));
    }

    [Fact]
    public async Task ExecuteAsync_completes_both_steps_successfully_on_a_real_round_trip()
    {
        await using MqttTestBroker broker = await MqttTestBroker.StartAsync();
        MqttWorkflow workflow = CreateWorkflow(host: "localhost", broker.Port);
        (VirtualUserContext context, CollectingMetricSink sink, StepId iterationStep) = CreateHarness(workflow);

        await RunIterationAsync(workflow, context);

        // 3 mesures publiees : l'etape technique __iteration (EndIteration) plus les deux etapes
        // du scenario — seules ces deux dernieres sont l'objet de ce test.
        Assert.Equal(3, sink.Results.Count);
        Assert.All(
            sink.Results.Where(result => result.Step != iterationStep),
            static result => Assert.Equal(RequestOutcome.Success, result.Outcome));
    }

    [Fact]
    public async Task A_refused_connection_fails_the_connect_step_as_a_connection_error()
    {
        // Aucun courtier ne demarre : le port choisi n'a jamais eu d'ecouteur, la connexion TCP
        // elle-meme est refusee avant tout echange MQTT.
        int refusedPort = GetFreeTcpPort();
        MqttWorkflow workflow = CreateWorkflow(host: "localhost", refusedPort);
        (VirtualUserContext context, CollectingMetricSink sink, _) = CreateHarness(workflow);

        await RunIterationAsync(workflow, context);

        MetricResult connectResult = sink.Results.First();
        Assert.Equal(RequestOutcome.ConnectionError, connectResult.Outcome);
    }

    [Fact]
    public async Task A_broker_that_never_acknowledges_the_connection_times_out()
    {
        // Un TcpListener brut accepte la connexion mais ne repond jamais au CONNECT MQTT :
        // reproduit un courtier qui decroche sans dependre d'un alea reseau. La tache d'accept
        // doit rester referencee (pas "_ = ...") : sans ca, le TcpClient accepte n'a plus aucune
        // reference une fois la tache terminee et peut etre finalise par le GC en plein test,
        // ce qui reinitialise la connexion (RST) et fait echouer l'assertion avec
        // ConnectionError au lieu de Timeout — flaky vu en CI, pas reproduit systematiquement.
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            MqttWorkflow workflow = CreateWorkflow(host: "localhost", port, timeoutSeconds: 1);
            (VirtualUserContext context, CollectingMetricSink sink, _) = CreateHarness(workflow);

            await RunIterationAsync(workflow, context);

            MetricResult connectResult = sink.Results.First();
            Assert.Equal(RequestOutcome.Timeout, connectResult.Outcome);
        }
        finally
        {
            listener.Stop();

            if (acceptTask.IsCompletedSuccessfully)
            {
                (await acceptTask).Dispose();
            }
        }
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static MqttWorkflow CreateWorkflow(string host, int port, int? timeoutSeconds = null)
    {
        Environment.SetEnvironmentVariable(HOST_ENVIRONMENT_VARIABLE, host);
        Environment.SetEnvironmentVariable(PORT_ENVIRONMENT_VARIABLE, port.ToString());
        Environment.SetEnvironmentVariable(TOPIC_PREFIX_ENVIRONMENT_VARIABLE, $"tempest/tests/{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE, timeoutSeconds?.ToString());
        try
        {
            return new MqttWorkflow();
        }
        finally
        {
            Environment.SetEnvironmentVariable(HOST_ENVIRONMENT_VARIABLE, null);
            Environment.SetEnvironmentVariable(PORT_ENVIRONMENT_VARIABLE, null);
            Environment.SetEnvironmentVariable(TOPIC_PREFIX_ENVIRONMENT_VARIABLE, null);
            Environment.SetEnvironmentVariable(TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE, null);
        }
    }

    /// <summary>Meme reproduction du cycle de vie que <c>VirtualUserWorker</c>, voir DynamicCheckoutWorkflowTests.</summary>
    private static async Task RunIterationAsync(MqttWorkflow workflow, VirtualUserContext context)
    {
        ExecutionToken token = new(IterationIndex: 0, ScheduledTicks: 0L);
        context.BeginIteration(in token, startedTicks: 0L, CancellationToken.None);
        await workflow.ExecuteAsync(context, CancellationToken.None).ConfigureAwait(false);
        context.EndIteration(startedTicks: 0L, RequestOutcome.Success);
    }

    private static (VirtualUserContext Context, CollectingMetricSink Sink, StepId IterationStep) CreateHarness(MqttWorkflow workflow)
    {
        // HttpClient jamais utilise par MqttWorkflow : place tenante requise par VirtualUserContext.
        HttpClient client = new() { BaseAddress = new Uri("https://unused.example") };

        StepRegistry registry = new();
        StepId iterationStep = registry.Register(WellKnownSteps.ITERATION);
        workflow.RegisterSteps(registry);
        registry.Seal();

        CollectingMetricSink sink = new();
        VirtualUserContext context = new(virtualUserId: 0, client, sink, iterationStep);

        return (context, sink, iterationStep);
    }
}