using System.Net;
using OpenTelemetry.Metrics;
using Sirocco.Application.DependencyInjection;
using Sirocco.Application.Execution;
using Sirocco.Application.Metrics;
using Sirocco.Domain.Execution;
using Sirocco.Domain.Load;
using Sirocco.Domain.Metrics;
using Sirocco.Host.Configuration;
using Sirocco.Infrastructure.DependencyInjection;
using Sirocco.Scenarios;
using Sirocco.Scenarios.Plugins;

namespace Sirocco.Host;

/// <summary>
/// Cable et demarre l'hote en mode autonome (aucun role distribue) : un seul workflow, un seul
/// profil de charge, un seul processus — ou, si <see cref="SiroccoHostOptions.Scenarios"/> est
/// renseigne, delegue a <see cref="MultiScenarioHost"/> (voir sa remarque de classe).
/// <para>
/// Extrait de <c>Program.cs</c> pour etre reutilise par <c>Sirocco.Cli</c>, qui construit le
/// meme <see cref="SiroccoHostOptions"/> a partir d'arguments de ligne de commande plutot que
/// de <c>appsettings.json</c> — le cablage reste strictement identique dans les deux cas.
/// </para>
/// </summary>
public static class StandaloneHost
{
    /// <summary>
    /// Construit le workflow, le moteur et les endpoints de diagnostic decrits par
    /// <paramref name="siroccoOptions"/>, puis demarre l'hote. Bloque jusqu'a l'arret de l'hote.
    /// </summary>
    public static void Run(WebApplicationBuilder builder, SiroccoHostOptions siroccoOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(siroccoOptions);

        // Scenarios concurrents : un cablage entierement different (N workflows/ordonnanceurs
        // isoles plutot qu'un singleton de chaque), voir la remarque de classe de MultiScenarioHost.
        if (siroccoOptions.Scenarios.Count > 0)
        {
            MultiScenarioHost.Run(builder, siroccoOptions);
            return;
        }

        // Modele ferme, sous une de ses quatre formes : aucun profil de debit, un ordonnanceur
        // different enregistre en amont — AddSiroccoEngine garde celui-la (voir sa remarque de
        // classe) plutot que d'en construire un par defaut a partir d'un profil qui n'existe pas
        // dans ces modes. Ordre de priorite documente sur SiroccoHostOptions : montee
        // d'utilisateurs, puis effectif fixe a duree, puis iterations par utilisateur, puis
        // iterations partagees, puis enfin le profil de debit (modele ouvert).
        LoadModelPlan plan = BuildLoadModel(
            siroccoOptions.MaxVirtualUsers,
            siroccoOptions.Profile,
            siroccoOptions.ClosedModelDuration,
            siroccoOptions.RampVus,
            siroccoOptions.SharedIterations,
            siroccoOptions.IterationsPerVirtualUser,
            siroccoOptions.MaxRequestsPerSecond);

        builder.Services.AddSingleton(plan.Scheduler);

        // Le scenario code en dur reste le comportement par defaut : un fichier de scenario
        // n'entre en jeu que si l'operateur le renseigne explicitement, et garde la priorite sur
        // le choix fait via Workflow.
        IWorkflow workflow = BuildWorkflow(
            builder,
            siroccoOptions.ScenarioFile,
            siroccoOptions.Workflow,
            siroccoOptions.PluginWorkflowType,
            siroccoOptions.PluginPackageId,
            siroccoOptions.PluginPackageVersion,
            siroccoOptions.PluginPackageSources,
            siroccoOptions.AllowUnsignedPlugins,
            siroccoOptions.AllowedEnvironmentVariables,
            siroccoOptions.AllowAllEnvironmentVariables);

        // Client HTTP unique, partage par tous les utilisateurs virtuels : c'est ce partage — pas
        // un client par requete — qui permet au pool de connexions du SocketsHttpHandler de tenir
        // des dizaines de milliers de RPS sans epuiser les ports ephemeres.
        builder.Services.AddSingleton(_ => BuildHttpClient(siroccoOptions.TargetBaseUrl));

        builder.Services.AddSingleton(siroccoOptions);
        builder.Services.AddSingleton(workflow);

        builder.Services.AddSiroccoEngine(
            plan.Profile,
            new LoadTestOptions
            {
                MaxVirtualUsers = plan.EffectiveMaxVirtualUsers,
                RampProfile = plan.RampProfile,
                IterationsPerVirtualUser = plan.IterationsPerVirtualUser,
                TokenQueueCapacity = plan.TokenQueueCapacity,
            });
        builder.Services.AddSiroccoMetrics();
        builder.Services.AddSiroccoOpenTelemetry(otel => otel.AddPrometheusExporter());

        builder.Services.AddHostedService<LoadTestHostedService>();

        WebApplication app = builder.Build();

        app.MapPrometheusScrapingEndpoint();

        app.MapGet("/report", (MetricsAggregator aggregator) =>
            Results.Ok(aggregator.Snapshot(StatisticsScope.Cumulative) with { Tags = workflow.Tags, ClosedModel = siroccoOptions.IsClosedModel }));

        app.MapGet("/report/live", (MetricsAggregator aggregator) =>
            Results.Ok(aggregator.Snapshot(StatisticsScope.Sliding) with { Tags = workflow.Tags, ClosedModel = siroccoOptions.IsClosedModel }));

        app.MapGet("/thresholds", (MetricsAggregator aggregator, SiroccoHostOptions options) =>
            Results.Ok(ThresholdReport.Evaluate(options.Thresholds, aggregator.Snapshot(StatisticsScope.Cumulative))));

        app.MapGet("/report.html", (MetricsAggregator aggregator, SiroccoHostOptions options) =>
        {
            LoadTestReport report = aggregator.Snapshot(StatisticsScope.Cumulative) with { Tags = workflow.Tags, ClosedModel = options.IsClosedModel };
            ThresholdReport thresholds = ThresholdReport.Evaluate(options.Thresholds, report);
            return Results.Content(report.ToHtml(thresholds), "text/html");
        });

        // Miroir de /report/live, mais en page HTML qui se recharge seule : un JSON ne se lit
        // pas pendant un tir en cours, contrairement a ce tableau de bord (voir sa remarque de
        // classe sur SiroccoHostOptions.LiveDashboardRefreshSeconds).
        app.MapGet("/report/live.html", (MetricsAggregator aggregator, SiroccoHostOptions options) =>
        {
            LoadTestReport report = aggregator.Snapshot(StatisticsScope.Sliding) with { Tags = workflow.Tags, ClosedModel = options.IsClosedModel };
            return Results.Content(report.ToHtml(autoRefreshSeconds: options.LiveDashboardRefreshSeconds), "text/html");
        });

        app.Run();
    }

    /// <summary>
    /// Modele de charge selectionne pour un scenario (le tir entier en mode autonome, un scenario
    /// parmi d'autres en mode scenarios concurrents) : son ordonnanceur, le profil sous-jacent
    /// s'il s'agit du modele ouvert (pour <c>AddSiroccoEngine</c>, voir sa remarque de classe), et
    /// l'effectif/le nombre d'iterations par utilisateur virtuel effectifs qui en decoulent.
    /// </summary>
    internal readonly record struct LoadModelPlan(
        ILoadScheduler Scheduler,
        LoadProfile? Profile,
        VirtualUserProfile? RampProfile,
        int EffectiveMaxVirtualUsers,
        long? IterationsPerVirtualUser,
        int? TokenQueueCapacity = null);

    /// <summary>
    /// Capacite de file imposee aux modeles <b>tires par les utilisateurs virtuels</b> (modele
    /// ferme et nombre d'iterations) : leurs ordonnanceurs horodatent chaque jeton a l'emission
    /// (<c>SiroccoClock.Now</c>) et n'ont donc aucun echeancier theorique. Laisser la file par
    /// defaut (<c>max(vus * 2, 64)</c>) y produirait trois defauts a la fois, constates sur un vrai
    /// tir navigateur (iterations de l'ordre de la seconde) :
    /// <list type="number">
    /// <item><description>
    /// la duree deborde — l'ordonnanceur remplit la file d'un coup, puis les travailleurs doivent
    /// la vider bien apres l'expiration du palier (mesure : 74 iterations en 71,8 s pour
    /// <c>--vus 1 --duration 10s</c>) ;
    /// </description></item>
    /// <item><description>
    /// une dette d'ordonnancement fantome apparait — un jeton horodate a l'emission mais execute
    /// une minute plus tard affiche une minute de retard, alors que rien n'etait en retard ;
    /// </description></item>
    /// <item><description>
    /// et surtout les <b>centiles sont fausses</b> : <c>ResponseTicks</c> se mesure depuis
    /// l'horodatage du jeton, donc cette attente en file entre dans la latence rapportee.
    /// </description></item>
    /// </list>
    /// Le plafond effectif redevient donc l'effectif lui-meme, ce que la remarque de classe de
    /// <see cref="ClosedModelScheduler"/> decrivait deja comme le comportement voulu ("un nouveau
    /// jeton n'est ecrit que lorsqu'un utilisateur virtuel vient de se liberer") — le plancher de
    /// 64 le contredisait des que l'effectif etait inferieur a 32. Le modele ouvert, lui, garde le
    /// defaut : sa file doit absorber une rafale, et la dette qu'elle produit y est le vrai signal
    /// de saturation, pas un artefact.
    /// </summary>
    private static int PullModelTokenQueueCapacity(int maxVirtualUsers) => Math.Max(1, maxVirtualUsers);

    /// <summary>
    /// Choisit l'ordonnanceur decrit par ces champs, dans le meme ordre de priorite que documente
    /// sur <see cref="SiroccoHostOptions"/> — montee d'utilisateurs, puis effectif fixe a duree,
    /// puis iterations par utilisateur, puis iterations partagees, puis enfin le profil de debit
    /// — puis, si <paramref name="maxRequestsPerSecond"/> est renseigne, enveloppe le resultat
    /// dans un <see cref="RateCappedScheduler"/> : le bridage s'applique de la meme facon quel que
    /// soit le modele choisi ci-dessus. Factorise hors de <see cref="Run"/> pour etre reutilise,
    /// un scenario a la fois, par <see cref="MultiScenarioHost"/>.
    /// </summary>
    internal static LoadModelPlan BuildLoadModel(
        int maxVirtualUsers,
        IReadOnlyList<LoadStageOptions> profileStages,
        TimeSpan? closedModelDuration,
        IReadOnlyList<VirtualUserStageOptions> rampStages,
        long? sharedIterations,
        long? iterationsPerVirtualUser,
        double? maxRequestsPerSecond = null)
    {
        LoadModelPlan plan = SelectScheduler(
            maxVirtualUsers, profileStages, closedModelDuration, rampStages, sharedIterations, iterationsPerVirtualUser);

        return maxRequestsPerSecond is { } cap
            ? plan with { Scheduler = new RateCappedScheduler(plan.Scheduler, cap) }
            : plan;
    }

    private static LoadModelPlan SelectScheduler(
        int maxVirtualUsers,
        IReadOnlyList<LoadStageOptions> profileStages,
        TimeSpan? closedModelDuration,
        IReadOnlyList<VirtualUserStageOptions> rampStages,
        long? sharedIterations,
        long? iterationsPerVirtualUser)
    {
        if (rampStages.Count > 0)
        {
            VirtualUserProfile rampProfile = VirtualUserProfileFactory.FromStages(rampStages);
            return new LoadModelPlan(
                new ClosedModelScheduler(rampProfile.TotalDuration), null, rampProfile, rampProfile.PeakVus, null,
                PullModelTokenQueueCapacity(rampProfile.PeakVus));
        }

        if (closedModelDuration is { } duration)
        {
            return new LoadModelPlan(
                new ClosedModelScheduler(duration), null, null, maxVirtualUsers, null,
                PullModelTokenQueueCapacity(maxVirtualUsers));
        }

        if (iterationsPerVirtualUser is { } perVirtualUser)
        {
            return new LoadModelPlan(
                new IterationCountScheduler(maxVirtualUsers * perVirtualUser), null, null, maxVirtualUsers, perVirtualUser,
                PullModelTokenQueueCapacity(maxVirtualUsers));
        }

        if (sharedIterations is { } shared)
        {
            return new LoadModelPlan(
                new IterationCountScheduler(shared), null, null, maxVirtualUsers, null,
                PullModelTokenQueueCapacity(maxVirtualUsers));
        }

        LoadProfile profile = LoadProfileFactory.FromStages(profileStages);
        return new LoadModelPlan(new CoordinatedRateLimiter(profile), profile, null, maxVirtualUsers, null);
    }

    /// <summary>
    /// Construit le workflow decrit par ces champs, dans cet ordre de priorite : un fichier de
    /// scenario declaratif/scripte/plugin (<paramref name="scenarioFile"/>), sinon un plugin
    /// resolu depuis un paquet NuGet (<paramref name="pluginPackageId"/>, voir
    /// <see cref="NuGetPluginResolver"/>), sinon un des scenarios integres selectionnes par nom.
    /// Factorise hors de <see cref="Run"/> pour etre reutilise, un scenario a la fois, par
    /// <see cref="MultiScenarioHost"/>.
    /// </summary>
    internal static IWorkflow BuildWorkflow(
        WebApplicationBuilder builder,
        string? scenarioFile,
        string workflowName,
        string? pluginWorkflowType = null,
        string? pluginPackageId = null,
        string? pluginPackageVersion = null,
        IReadOnlyList<string>? pluginPackageSources = null,
        bool allowUnsignedPlugins = false,
        IReadOnlyList<string>? allowedEnvironmentVariables = null,
        bool allowAllEnvironmentVariables = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!string.IsNullOrWhiteSpace(scenarioFile))
        {
            return WorkflowFileLoader.LoadFromFile(
                scenarioFile,
                pluginWorkflowType,
                new EnvironmentAccessPolicy(allowedEnvironmentVariables ?? [], allowAllEnvironmentVariables));
        }

        if (!string.IsNullOrWhiteSpace(pluginPackageId))
        {
            // Blocage deliberement synchrone, comme ScriptedWorkflowLoader.LoadFromFile : le
            // chargement d'un scenario n'a jamais lieu sur le chemin critique, une seule fois
            // avant le premier tir.
            string assemblyPath = NuGetPluginResolver
                .ResolveAssemblyPathAsync(pluginPackageId, pluginPackageVersion, pluginPackageSources, allowUnsignedPlugins: allowUnsignedPlugins)
                .GetAwaiter()
                .GetResult();

            return PluginWorkflowLoader.Load(assemblyPath, pluginWorkflowType);
        }

        if (string.Equals(workflowName, SiroccoHostOptions.WEBSOCKET_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            WebSocketEchoWorkflowOptions webSocketOptions = builder.Configuration.GetSection("WebSocketEcho")
                .Get<WebSocketEchoWorkflowOptions>() ?? new WebSocketEchoWorkflowOptions();

            return new WebSocketEchoWorkflow(webSocketOptions);
        }

        if (string.Equals(workflowName, SiroccoHostOptions.GRPC_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            GrpcEchoWorkflowOptions grpcOptions = builder.Configuration.GetSection("GrpcEcho")
                .Get<GrpcEchoWorkflowOptions>() ?? new GrpcEchoWorkflowOptions();

            return new GrpcEchoWorkflow(grpcOptions);
        }

        if (string.Equals(workflowName, SiroccoHostOptions.GRPC_STREAM_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            // Meme section de configuration que GrpcEchoWorkflow (TargetUri) : meme besoin, memes
            // reglages, pas de raison d'en dupliquer une deuxieme.
            GrpcEchoWorkflowOptions grpcStreamOptions = builder.Configuration.GetSection("GrpcEcho")
                .Get<GrpcEchoWorkflowOptions>() ?? new GrpcEchoWorkflowOptions();

            return new GrpcStreamEchoWorkflow(grpcStreamOptions);
        }

        if (string.Equals(workflowName, SiroccoHostOptions.GRPC_CLIENT_STREAM_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            GrpcEchoWorkflowOptions grpcClientStreamOptions = builder.Configuration.GetSection("GrpcEcho")
                .Get<GrpcEchoWorkflowOptions>() ?? new GrpcEchoWorkflowOptions();

            return new GrpcClientStreamEchoWorkflow(grpcClientStreamOptions);
        }

        if (string.Equals(workflowName, SiroccoHostOptions.GRPC_BIDI_STREAM_ECHO_WORKFLOW, StringComparison.OrdinalIgnoreCase))
        {
            GrpcEchoWorkflowOptions grpcBidiStreamOptions = builder.Configuration.GetSection("GrpcEcho")
                .Get<GrpcEchoWorkflowOptions>() ?? new GrpcEchoWorkflowOptions();

            return new GrpcBidiStreamEchoWorkflow(grpcBidiStreamOptions);
        }

        DynamicCheckoutWorkflowOptions checkoutOptions = builder.Configuration.GetSection("DynamicCheckout")
            .Get<DynamicCheckoutWorkflowOptions>() ?? new DynamicCheckoutWorkflowOptions();

        return new DynamicCheckoutWorkflow(checkoutOptions);
    }

    /// <summary>
    /// Construit un client HTTP configure pour tenir des dizaines de milliers de RPS (pool de
    /// connexions dimensionne, decompression desactivee). Factorise hors de <see cref="Run"/> pour
    /// etre reutilise, un scenario a la fois — jamais partage entre deux scenarios d'un meme tir
    /// a scenarios concurrents, contrairement au client unique du tir simple.
    /// </summary>
    internal static HttpClient BuildHttpClient(string targetBaseUrl) =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4_096,
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            BaseAddress = new Uri(targetBaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
}