using System.Net;
using System.Text.Json.Serialization;
using OpenTelemetry.Metrics;
using Tempest.Application.DependencyInjection;
using Tempest.Application.Execution;
using Tempest.Application.Metrics;
using Tempest.Domain.Execution;
using Tempest.Domain.Load;
using Tempest.Domain.Metrics;
using Tempest.Host;
using Tempest.Host.Configuration;
using Tempest.Host.Distributed;
using Tempest.Infrastructure.DependencyInjection;
using Tempest.Scenarios;
using Tempest.Scenarios.Declarative;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

TempestHostOptions tempestOptions = builder.Configuration.GetSection("Tempest").Get<TempestHostOptions>()
    ?? throw new InvalidOperationException("La section de configuration 'Tempest' est manquante.");

// Les diagnostics (/report, /thresholds) sont pour un humain ou un script CI : les enums
// doivent s'y lire ("ResponseP95Milliseconds"), pas s'y deviner (3). Vaut pour les trois roles.
builder.Services.ConfigureHttpJsonOptions(static jsonOptions =>
    jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

if (string.Equals(tempestOptions.Role, TempestHostOptions.ROLE_WORKER, StringComparison.OrdinalIgnoreCase))
{
    WorkerOptions workerOptions = builder.Configuration.GetSection("Worker").Get<WorkerOptions>()
        ?? throw new InvalidOperationException("La section de configuration 'Worker' est manquante.");
    workerOptions.Validate();

    builder.Services.AddHttpClient();
    builder.Services.AddSingleton(workerOptions);
    builder.Services.AddSingleton<WorkerCoordinator>();
    builder.Services.AddHostedService<WorkerRegistrationHostedService>();

    WebApplication workerApp = builder.Build();

    // Deux appels distincts (prepare, start) plutot qu'un seul : le maitre prepare tous les
    // workers d'abord, puis les demarre tous — c'est ce qui rapproche leurs departs reels,
    // plutot que de laisser chaque "prepare + start en un seul appel" partir des l'arrivee de
    // sa propre requete HTTP, en serie, avec un decalage cumule d'un worker a l'autre.
    workerApp.MapPost("/worker/prepare", (WorkerPrepareRequest request, WorkerCoordinator coordinator) =>
    {
        coordinator.Prepare(request);
        return Results.Ok();
    });

    workerApp.MapPost("/worker/start", (WorkerCoordinator coordinator) =>
    {
        coordinator.Start();
        return Results.Accepted();
    });

    workerApp.MapGet("/report", (WorkerCoordinator coordinator) =>
        coordinator.Aggregator is { } aggregator
            ? Results.Ok(aggregator.Snapshot(StatisticsScope.Cumulative))
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    workerApp.MapGet("/report/live", (WorkerCoordinator coordinator) =>
        coordinator.Aggregator is { } aggregator
            ? Results.Ok(aggregator.Snapshot(StatisticsScope.Sliding))
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    workerApp.Run();
}
else if (string.Equals(tempestOptions.Role, TempestHostOptions.ROLE_MASTER, StringComparison.OrdinalIgnoreCase))
{
    MasterOptions masterOptions = builder.Configuration.GetSection("Master").Get<MasterOptions>()
        ?? throw new InvalidOperationException("La section de configuration 'Master' est manquante.");
    masterOptions.Validate();

    builder.Services.AddHttpClient();
    builder.Services.AddSingleton(masterOptions);
    builder.Services.AddSingleton(tempestOptions);
    builder.Services.AddSingleton<MasterCoordinator>();
    builder.Services.AddHostedService<MasterOrchestrationHostedService>();

    WebApplication masterApp = builder.Build();

    masterApp.MapPost("/master/register", (WorkerRegistration registration, MasterCoordinator coordinator) =>
    {
        coordinator.Register(registration.WorkerUrl);
        return Results.Ok();
    });

    masterApp.MapPost("/master/report", (WorkerReport report, MasterCoordinator coordinator) =>
    {
        coordinator.SubmitReport(report);
        return Results.Ok();
    });

    masterApp.MapGet("/report", (MasterCoordinator coordinator) =>
        coordinator.FinalReport is { } report
            ? Results.Ok(report)
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    masterApp.MapGet("/thresholds", (MasterCoordinator coordinator) =>
        coordinator.FinalThresholds is { } thresholds
            ? Results.Ok(thresholds)
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    masterApp.Run();
}
else
{
    LoadProfile profile = LoadProfileFactory.FromOptions(tempestOptions);

    // Le scenario code en dur reste le comportement par defaut : un fichier de scenario
    // n'entre en jeu que si l'operateur le renseigne explicitement, et garde la priorite sur
    // le choix fait via Workflow.
    IWorkflow workflow;
    if (!string.IsNullOrWhiteSpace(tempestOptions.ScenarioFile))
    {
        workflow = new DeclarativeWorkflow(ScenarioDefinitionLoader.LoadFromFile(tempestOptions.ScenarioFile));
    }
    else if (string.Equals(tempestOptions.Workflow, TempestHostOptions.WEBSOCKET_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
    {
        WebSocketEchoWorkflowOptions webSocketOptions = builder.Configuration.GetSection("WebSocketEcho")
            .Get<WebSocketEchoWorkflowOptions>() ?? new WebSocketEchoWorkflowOptions();

        workflow = new WebSocketEchoWorkflow(webSocketOptions);
    }
    else if (string.Equals(tempestOptions.Workflow, TempestHostOptions.GRPC_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
    {
        GrpcEchoWorkflowOptions grpcOptions = builder.Configuration.GetSection("GrpcEcho")
            .Get<GrpcEchoWorkflowOptions>() ?? new GrpcEchoWorkflowOptions();

        workflow = new GrpcEchoWorkflow(grpcOptions);
    }
    else
    {
        DynamicCheckoutWorkflowOptions checkoutOptions = builder.Configuration.GetSection("DynamicCheckout")
            .Get<DynamicCheckoutWorkflowOptions>() ?? new DynamicCheckoutWorkflowOptions();

        workflow = new DynamicCheckoutWorkflow(checkoutOptions);
    }

    // Client HTTP unique, partage par tous les utilisateurs virtuels : c'est ce partage — pas
    // un client par requete — qui permet au pool de connexions du SocketsHttpHandler de tenir
    // des dizaines de milliers de RPS sans epuiser les ports ephemeres.
    builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4_096,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        BaseAddress = new Uri(tempestOptions.TargetBaseUrl),
        Timeout = TimeSpan.FromSeconds(30),
    });

    builder.Services.AddSingleton(tempestOptions);
    builder.Services.AddSingleton(workflow);

    builder.Services.AddTempestEngine(profile, new LoadTestOptions { MaxVirtualUsers = tempestOptions.MaxVirtualUsers });
    builder.Services.AddTempestMetrics();
    builder.Services.AddTempestOpenTelemetry(otel => otel.AddPrometheusExporter());

    builder.Services.AddHostedService<LoadTestHostedService>();

    WebApplication app = builder.Build();

    app.MapPrometheusScrapingEndpoint();

    app.MapGet("/report", (MetricsAggregator aggregator) =>
        Results.Ok(aggregator.Snapshot(StatisticsScope.Cumulative)));

    app.MapGet("/report/live", (MetricsAggregator aggregator) =>
        Results.Ok(aggregator.Snapshot(StatisticsScope.Sliding)));

    app.MapGet("/thresholds", (MetricsAggregator aggregator, TempestHostOptions options) =>
        Results.Ok(ThresholdReport.Evaluate(options.Thresholds, aggregator.Snapshot(StatisticsScope.Cumulative))));

    app.Run();
}