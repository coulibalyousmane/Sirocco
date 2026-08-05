using System.Net;
using System.Net.Http.Json;
using Tempest.Application.Execution;
using Tempest.Application.Metrics;
using Tempest.Domain.Execution;
using Tempest.Domain.Load;
using Tempest.Domain.Metrics;
using Tempest.Host.Configuration;
using Tempest.Infrastructure.Metrics;
using Tempest.Scenarios;
using Tempest.Scenarios.Declarative;

namespace Tempest.Host.Distributed;

/// <summary>
/// Etat du tir local d'un worker : construit paresseusement a la reception de
/// <c>/worker/prepare</c>, jamais au demarrage du process.
/// <para>
/// Contrairement au mode autonome, ou tout le graphe du moteur est cable par injection de
/// dependances avant <c>Build()</c>, un worker ne connait ni son profil de charge ni son
/// scenario avant que le maitre ne les lui envoie — ce graphe est donc construit ici, a la
/// main, une fois la preparation recue.
/// </para>
/// </summary>
public sealed class WorkerCoordinator(WorkerOptions options, IHttpClientFactory httpClientFactory, ILogger<WorkerCoordinator> logger)
{
    private TargetRpsLoadEngine? _engine;
    private MetricsProcessor? _metricsProcessor;
    private bool _running;

    /// <summary>Agregateur du tir local, disponible des que <see cref="Prepare"/> a ete appele.</summary>
    public MetricsAggregator? Aggregator { get; private set; }

    /// <summary>Indique que ce worker a recu sa preparation pour le tir en cours.</summary>
    public bool IsPrepared => _engine is not null;

    /// <summary>Construit le moteur local a partir de l'ordre du maitre.</summary>
    /// <exception cref="InvalidOperationException">Ce worker a deja ete prepare pour ce tir.</exception>
    public void Prepare(WorkerPrepareRequest request)
    {
        if (_engine is not null)
        {
            throw new InvalidOperationException("Ce worker a deja recu une preparation pour ce tir.");
        }

        LoadProfile profile = LoadProfileFactory.FromStages(request.Profile);
        IWorkflow workflow = CreateWorkflow(request.Workflow, request.ScenarioFile);

        // Meme reglage que le client HTTP du mode autonome (Tempest.Host/Program.cs) : un seul
        // client partage, pool de connexions dimensionne pour tenir un debit eleve.
        HttpClient targetClient = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4_096,
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            BaseAddress = new Uri(request.TargetBaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

        StepRegistry steps = new();
        ChannelMetricSink sink = new();
        LoadTestOptions engineOptions = new() { MaxVirtualUsers = request.MaxVirtualUsers };
        ILoadScheduler scheduler = new CoordinatedRateLimiter(profile);

        _engine = new TargetRpsLoadEngine(scheduler, workflow, targetClient, sink, engineOptions, steps);

        MetricsAggregator aggregator = new(steps);
        _metricsProcessor = new MetricsProcessor(sink, aggregator);
        Aggregator = aggregator;
    }

    /// <summary>
    /// Demarre le tir en arriere-plan et renvoie immediatement : l'appelant (l'endpoint
    /// <c>/worker/start</c>) ne doit pas attendre la fin du tir pour repondre au maitre.
    /// </summary>
    /// <exception cref="InvalidOperationException">Pas encore prepare, ou deja en cours.</exception>
    public void Start()
    {
        if (_engine is null)
        {
            throw new InvalidOperationException("Ce worker n'a pas encore recu sa preparation.");
        }

        if (_running)
        {
            throw new InvalidOperationException("Ce worker tire deja.");
        }

        _running = true;
        _ = Task.Run(RunAndReportAsync);
    }

    private async Task RunAndReportAsync()
    {
        try
        {
            _metricsProcessor!.Start();

            try
            {
                await _engine!.RunAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await _metricsProcessor.StopAsync().ConfigureAwait(false);
            }

            WorkerReport report = Aggregator!.ExportRaw(options.SelfUrl);
            await SubmitReportAsync(report).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Un tir local qui casse ne doit pas faire disparaitre le process en silence :
            // le maitre attend un rapport qui ne viendra jamais, autant que ce soit visible ici.
            logger.LogError(ex, "Le tir local de ce worker a echoue.");
        }
    }

    private async Task SubmitReportAsync(WorkerReport report)
    {
        try
        {
            HttpClient masterClient = httpClientFactory.CreateClient();
            using HttpResponseMessage response = await masterClient
                .PostAsJsonAsync($"{options.MasterUrl.TrimEnd('/')}/master/report", report)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Impossible de transmettre le rapport au maitre {MasterUrl}.", options.MasterUrl);
        }
    }

    private static IWorkflow CreateWorkflow(string workflow, string? scenarioFile)
    {
        if (!string.IsNullOrWhiteSpace(scenarioFile))
        {
            return new DeclarativeWorkflow(ScenarioDefinitionLoader.LoadFromFile(scenarioFile));
        }

        if (string.Equals(workflow, TempestHostOptions.WEBSOCKET_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            return new WebSocketEchoWorkflow();
        }

        if (string.Equals(workflow, TempestHostOptions.GRPC_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            return new GrpcEchoWorkflow();
        }

        if (string.Equals(workflow, TempestHostOptions.GRPC_STREAM_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            return new GrpcStreamEchoWorkflow();
        }

        return new DynamicCheckoutWorkflow();
    }
}