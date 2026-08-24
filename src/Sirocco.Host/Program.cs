using System.Text.Json.Serialization;
using OpenTelemetry.Metrics;
using Sirocco.Domain.Metrics;
using Sirocco.Host;
using Sirocco.Host.Configuration;
using Sirocco.Host.Distributed;
using Sirocco.Infrastructure.DependencyInjection;
using Sirocco.Infrastructure.Metrics;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

SiroccoHostOptions siroccoOptions = builder.Configuration.GetSection("Sirocco").Get<SiroccoHostOptions>()
    ?? throw new InvalidOperationException("La section de configuration 'Sirocco' est manquante.");

// Les diagnostics (/report, /thresholds) sont pour un humain ou un script CI : les enums
// doivent s'y lire ("ResponseP95Milliseconds"), pas s'y deviner (3). Vaut pour les trois roles.
builder.Services.ConfigureHttpJsonOptions(static jsonOptions =>
    jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

if (string.Equals(siroccoOptions.Role, SiroccoHostOptions.ROLE_WORKER, StringComparison.OrdinalIgnoreCase))
{
    WorkerOptions workerOptions = builder.Configuration.GetSection("Worker").Get<WorkerOptions>()
        ?? throw new InvalidOperationException("La section de configuration 'Worker' est manquante.");
    workerOptions.Validate();

    // Avant meme de construire l'hote : un worker sans secret partage sert /worker/prepare a
    // n'importe qui, et cette requete porte l'URL de la cible (voir EnsureConfigured).
    ClusterAuthentication.EnsureConfigured(
        SiroccoHostOptions.ROLE_WORKER,
        siroccoOptions.ClusterSharedSecret,
        siroccoOptions.AllowUnauthenticatedClusterControlPlane);

    builder.Services.AddHttpClient(ClusterCertificatePinning.CLUSTER_CLIENT_NAME)
        .ConfigurePrimaryHttpMessageHandler(() => ClusterCertificatePinning.CreateHandler(siroccoOptions.ClusterCertificateThumbprint));
    builder.Services.AddSingleton(workerOptions);
    builder.Services.AddSingleton(siroccoOptions);
    builder.Services.AddSingleton<WorkerCoordinator>();
    builder.Services.AddHostedService<WorkerLivenessHostedService>();

    // Cable des le demarrage, avant meme qu'un tir n'existe : WorkerCoordinator.Prepare()
    // construit son propre SiroccoMeter a la main une fois le tir connu (voir son commentaire),
    // et n'importe quel Meter nomme "Sirocco" cree plus tard est decouvert par ce MeterListener
    // des qu'il apparait — rien a reconfigurer entre le demarrage du process et /worker/prepare.
    builder.Services.AddSiroccoOpenTelemetry(otel => otel.AddPrometheusExporter());

    WebApplication workerApp = builder.Build();

    // Scale-down pilote par l'operateur Kubernetes (StatefulSet reduit) : Kubernetes envoie
    // SIGTERM, ce qui declenche ApplicationStopping avant le SIGKILL de fin de grace — annuler
    // ici plutot que de laisser le process mourir sans rapport soumet quand meme un rapport
    // partiel (voir WorkerCoordinator.Stop()). Sans effet si aucun tir n'est en cours.
    workerApp.Lifetime.ApplicationStopping.Register(() =>
        workerApp.Services.GetRequiredService<WorkerCoordinator>().Stop());

    workerApp.MapPrometheusScrapingEndpoint();

    // Deux appels distincts (prepare, start) plutot qu'un seul : le maitre prepare tous les
    // workers d'abord, puis les demarre tous — c'est ce qui rapproche leurs departs reels,
    // plutot que de laisser chaque "prepare + start en un seul appel" partir des l'arrivee de
    // sa propre requete HTTP, en serie, avec un decalage cumule d'un worker a l'autre.
    workerApp.MapPost("/worker/prepare", (WorkerPrepareRequest request, WorkerCoordinator coordinator) =>
    {
        coordinator.Prepare(request);
        return Results.Ok();
    }).AddEndpointFilter<ClusterAuthenticationFilter>();

    workerApp.MapPost("/worker/start", (WorkerCoordinator coordinator) =>
    {
        coordinator.Start();
        return Results.Accepted();
    }).AddEndpointFilter<ClusterAuthenticationFilter>();

    workerApp.MapGet("/report", (WorkerCoordinator coordinator) =>
        coordinator.Aggregator is { } aggregator
            ? Results.Ok(aggregator.Snapshot(StatisticsScope.Cumulative))
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    workerApp.MapGet("/report/live", (WorkerCoordinator coordinator) =>
        coordinator.Aggregator is { } aggregator
            ? Results.Ok(aggregator.Snapshot(StatisticsScope.Sliding))
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    // Etat brut cumule, pas des centiles deja calcules : c'est ce que le maitre sonde pour
    // rafraichir son tableau de bord combine (voir MasterOrchestrationHostedService), puisque
    // fusionner des centiles deja calcules serait le piege du "centile de centiles".
    workerApp.MapGet("/worker/report/raw", (WorkerCoordinator coordinator, WorkerOptions options) =>
        coordinator.Aggregator is { } aggregator
            ? Results.Ok(aggregator.ExportRaw(options.SelfUrl))
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    workerApp.Run();
}
else if (string.Equals(siroccoOptions.Role, SiroccoHostOptions.ROLE_MASTER, StringComparison.OrdinalIgnoreCase))
{
    MasterOptions masterOptions = builder.Configuration.GetSection("Master").Get<MasterOptions>()
        ?? throw new InvalidOperationException("La section de configuration 'Master' est manquante.");
    masterOptions.Validate();

    // Meme exigence que cote worker, et pour la meme raison : le maitre accepte des
    // enregistrements et des rapports, donc un faux worker ou un rapport falsifie.
    ClusterAuthentication.EnsureConfigured(
        SiroccoHostOptions.ROLE_MASTER,
        siroccoOptions.ClusterSharedSecret,
        siroccoOptions.AllowUnauthenticatedClusterControlPlane);

    builder.Services.AddHttpClient(ClusterCertificatePinning.CLUSTER_CLIENT_NAME)
        .ConfigurePrimaryHttpMessageHandler(() => ClusterCertificatePinning.CreateHandler(siroccoOptions.ClusterCertificateThumbprint));
    builder.Services.AddSingleton(masterOptions);
    builder.Services.AddSingleton(siroccoOptions);
    builder.Services.AddSingleton<MasterCoordinator>();
    builder.Services.AddHostedService<MasterOrchestrationHostedService>();

    // Le maitre n'a pas de MetricsAggregator local (il ne fait que fusionner des rapports deja
    // construits par les workers) : SiroccoMeter est donc cable a la main a partir de
    // MasterCoordinator.Snapshot, pas via AddSiroccoMetrics. MeterActivationHostedService force
    // sa construction au demarrage, exactement comme en mode autonome — sans elle, ce singleton
    // ne serait jamais resolu, donc jamais construit, et /metrics resterait vide en silence.
    builder.Services.AddSingleton(provider =>
        new SiroccoMeter(provider.GetRequiredService<MasterCoordinator>().Snapshot));
    builder.Services.AddHostedService<MeterActivationHostedService>();
    builder.Services.AddSiroccoOpenTelemetry(otel => otel.AddPrometheusExporter());

    WebApplication masterApp = builder.Build();

    masterApp.MapPrometheusScrapingEndpoint();

    masterApp.MapPost("/master/register", (WorkerRegistration registration, MasterCoordinator coordinator) =>
    {
        coordinator.Register(registration.WorkerUrl);
        return Results.Ok();
    }).AddEndpointFilter<ClusterAuthenticationFilter>();

    masterApp.MapPost("/master/report", (WorkerReport report, MasterCoordinator coordinator) =>
    {
        coordinator.SubmitReport(report);
        return Results.Ok();
    }).AddEndpointFilter<ClusterAuthenticationFilter>();

    // Signal de vie continu (WorkerLivenessHostedService), distinct de /master/register (un seul
    // appel, au demarrage) : c'est l'absence prolongee de ces appels qui permet au maitre de
    // detecter un worker perdu en cours de tir plutot que d'attendre indefiniment son rapport.
    masterApp.MapPost("/master/heartbeat", (WorkerRegistration heartbeat, MasterCoordinator coordinator) =>
    {
        coordinator.Heartbeat(heartbeat.WorkerUrl);
        return Results.Ok();
    }).AddEndpointFilter<ClusterAuthenticationFilter>();

    masterApp.MapGet("/report", (MasterCoordinator coordinator) =>
        coordinator.FinalReport is { } report
            ? Results.Ok(report)
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    // Rafraichi en continu pendant le tir par sondage des workers (voir
    // MasterOrchestrationHostedService) — approximatif par nature (intervalle de sondage),
    // contrairement a /report qui reste le verdict final, construit une seule fois a partir
    // des rapports pousses par les workers a la fin de leur tir local.
    masterApp.MapGet("/report/live", (MasterCoordinator coordinator) =>
        coordinator.LiveReport is { } liveReport
            ? Results.Ok(liveReport)
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    masterApp.MapGet("/thresholds", (MasterCoordinator coordinator) =>
        coordinator.FinalThresholds is { } thresholds
            ? Results.Ok(thresholds)
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    masterApp.MapGet("/report.html", (MasterCoordinator coordinator) =>
        coordinator.FinalReport is { } report
            ? Results.Content(report.ToHtml(coordinator.FinalThresholds), "text/html")
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

    masterApp.Run();
}
else
{
    StandaloneHost.Run(builder, siroccoOptions);
}