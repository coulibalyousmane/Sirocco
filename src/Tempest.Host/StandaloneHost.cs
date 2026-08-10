using System.Net;
using OpenTelemetry.Metrics;
using Tempest.Application.DependencyInjection;
using Tempest.Application.Execution;
using Tempest.Application.Metrics;
using Tempest.Domain.Execution;
using Tempest.Domain.Load;
using Tempest.Domain.Metrics;
using Tempest.Host.Configuration;
using Tempest.Infrastructure.DependencyInjection;
using Tempest.Scenarios;

namespace Tempest.Host;

/// <summary>
/// Cable et demarre l'hote en mode autonome (aucun role distribue) : un seul workflow, un seul
/// profil de charge, un seul processus.
/// <para>
/// Extrait de <c>Program.cs</c> pour etre reutilise par <c>Tempest.Cli</c>, qui construit le
/// meme <see cref="TempestHostOptions"/> a partir d'arguments de ligne de commande plutot que
/// de <c>appsettings.json</c> — le cablage reste strictement identique dans les deux cas.
/// </para>
/// </summary>
public static class StandaloneHost
{
    /// <summary>
    /// Construit le workflow, le moteur et les endpoints de diagnostic decrits par
    /// <paramref name="tempestOptions"/>, puis demarre l'hote. Bloque jusqu'a l'arret de l'hote.
    /// </summary>
    public static void Run(WebApplicationBuilder builder, TempestHostOptions tempestOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tempestOptions);

        // Modele ferme (effectif fixe ou en montee) : aucun profil de debit, un ordonnanceur
        // different enregistre en amont — AddTempestEngine garde celui-la (voir sa remarque de
        // classe) plutot que d'en construire un par defaut a partir d'un profil qui n'existe pas
        // dans ce mode. La montee d'utilisateurs est prioritaire sur l'effectif fixe, lui-meme
        // prioritaire sur le profil de debit (voir TempestHostOptions).
        LoadProfile? profile;
        VirtualUserProfile? rampProfile = null;
        int effectiveMaxVirtualUsers = tempestOptions.MaxVirtualUsers;

        if (tempestOptions.IsRampingVus)
        {
            profile = null;
            rampProfile = VirtualUserProfileFactory.FromOptions(tempestOptions);
            effectiveMaxVirtualUsers = rampProfile.PeakVus;
            builder.Services.AddSingleton<ILoadScheduler>(new ClosedModelScheduler(rampProfile.TotalDuration));
        }
        else if (tempestOptions.IsClosedModel)
        {
            profile = null;
            builder.Services.AddSingleton<ILoadScheduler>(new ClosedModelScheduler(tempestOptions.ClosedModelDuration!.Value));
        }
        else
        {
            profile = LoadProfileFactory.FromOptions(tempestOptions);
        }

        // Le scenario code en dur reste le comportement par defaut : un fichier de scenario
        // n'entre en jeu que si l'operateur le renseigne explicitement, et garde la priorite sur
        // le choix fait via Workflow.
        IWorkflow workflow;
        if (!string.IsNullOrWhiteSpace(tempestOptions.ScenarioFile))
        {
            workflow = WorkflowFileLoader.LoadFromFile(tempestOptions.ScenarioFile);
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
        else if (string.Equals(tempestOptions.Workflow, TempestHostOptions.GRPC_STREAM_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            // Meme section de configuration que GrpcEchoWorkflow (TargetUri) : meme besoin, memes
            // reglages, pas de raison d'en dupliquer une deuxieme.
            GrpcEchoWorkflowOptions grpcStreamOptions = builder.Configuration.GetSection("GrpcEcho")
                .Get<GrpcEchoWorkflowOptions>() ?? new GrpcEchoWorkflowOptions();

            workflow = new GrpcStreamEchoWorkflow(grpcStreamOptions);
        }
        else if (string.Equals(tempestOptions.Workflow, TempestHostOptions.GRPC_CLIENT_STREAM_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            GrpcEchoWorkflowOptions grpcClientStreamOptions = builder.Configuration.GetSection("GrpcEcho")
                .Get<GrpcEchoWorkflowOptions>() ?? new GrpcEchoWorkflowOptions();

            workflow = new GrpcClientStreamEchoWorkflow(grpcClientStreamOptions);
        }
        else if (string.Equals(tempestOptions.Workflow, TempestHostOptions.GRPC_BIDI_STREAM_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            GrpcEchoWorkflowOptions grpcBidiStreamOptions = builder.Configuration.GetSection("GrpcEcho")
                .Get<GrpcEchoWorkflowOptions>() ?? new GrpcEchoWorkflowOptions();

            workflow = new GrpcBidiStreamEchoWorkflow(grpcBidiStreamOptions);
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

        builder.Services.AddTempestEngine(
            profile,
            new LoadTestOptions { MaxVirtualUsers = effectiveMaxVirtualUsers, RampProfile = rampProfile });
        builder.Services.AddTempestMetrics();
        builder.Services.AddTempestOpenTelemetry(otel => otel.AddPrometheusExporter());

        builder.Services.AddHostedService<LoadTestHostedService>();

        WebApplication app = builder.Build();

        app.MapPrometheusScrapingEndpoint();

        app.MapGet("/report", (MetricsAggregator aggregator) =>
            Results.Ok(aggregator.Snapshot(StatisticsScope.Cumulative) with { Tags = workflow.Tags, ClosedModel = tempestOptions.IsClosedModel }));

        app.MapGet("/report/live", (MetricsAggregator aggregator) =>
            Results.Ok(aggregator.Snapshot(StatisticsScope.Sliding) with { Tags = workflow.Tags, ClosedModel = tempestOptions.IsClosedModel }));

        app.MapGet("/thresholds", (MetricsAggregator aggregator, TempestHostOptions options) =>
            Results.Ok(ThresholdReport.Evaluate(options.Thresholds, aggregator.Snapshot(StatisticsScope.Cumulative))));

        app.MapGet("/report.html", (MetricsAggregator aggregator, TempestHostOptions options) =>
        {
            LoadTestReport report = aggregator.Snapshot(StatisticsScope.Cumulative) with { Tags = workflow.Tags, ClosedModel = options.IsClosedModel };
            ThresholdReport thresholds = ThresholdReport.Evaluate(options.Thresholds, report);
            return Results.Content(report.ToHtml(thresholds), "text/html");
        });

        app.Run();
    }
}