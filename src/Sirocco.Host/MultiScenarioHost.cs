using Sirocco.Application.Execution;
using Sirocco.Domain.Execution;
using Sirocco.Host.Configuration;
using Sirocco.Host.Execution;

namespace Sirocco.Host;

/// <summary>
/// Cable et demarre l'hote en mode <b>scenarios concurrents</b> : N workflows, N ordonnanceurs, N
/// chaines de mesure isolees, un seul processus — appele par <see cref="StandaloneHost.Run"/> des
/// que <see cref="SiroccoHostOptions.Scenarios"/> est renseigne.
/// <para>
/// Un cablage deliberement different du tir simple : celui-ci passe par le conteneur d'injection
/// de dependances (<c>AddSiroccoEngine</c>/<c>AddSiroccoMetrics</c>), qui n'enregistre qu'un
/// singleton de chaque type — impossible d'y loger N <see cref="IWorkflow"/> ou N
/// <c>ILoadScheduler</c> distincts sans services a cles, que ce projet n'utilise pas encore
/// ailleurs. Chaque <see cref="ScenarioRunSpec"/> est donc construit a la main ici, puis confie a
/// <see cref="Execution.MultiScenarioRunner"/>, qui fait de meme pour sa propre chaine de mesure.
/// </para>
/// <para>
/// Limites de cette premiere version, documentees sur <see cref="SiroccoHostOptions.Scenarios"/> :
/// mode distribue non pris en charge, <c>/report/live</c> et <c>/metrics</c> non alimentes — le
/// rapport n'existe qu'une fois le tir termine, jamais en cours de route.
/// </para>
/// </summary>
internal static class MultiScenarioHost
{
    /// <summary>Construit les scenarios, le tir et les endpoints de diagnostic, puis demarre l'hote.</summary>
    internal static void Run(WebApplicationBuilder builder, SiroccoHostOptions siroccoOptions)
    {
        ValidateScenarioNames(siroccoOptions.Scenarios);

        List<ScenarioRunSpec> specs = [.. siroccoOptions.Scenarios.Select(scenario => BuildSpec(builder, siroccoOptions, scenario))];

        builder.Services.AddSingleton(siroccoOptions);
        builder.Services.AddSingleton<IReadOnlyList<ScenarioRunSpec>>(specs);
        builder.Services.AddSingleton<MultiScenarioReportHolder>();
        builder.Services.AddHostedService<MultiScenarioLoadTestHostedService>();

        WebApplication app = builder.Build();

        app.MapGet("/report", (MultiScenarioReportHolder holder) =>
            holder.Report is { } report ? Results.Ok(report) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/thresholds", (MultiScenarioReportHolder holder) =>
            holder.Report is { } report
                ? Results.Ok(report.Scenarios.ToDictionary(static scenario => scenario.Name, static scenario => scenario.Thresholds))
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/report.html", (MultiScenarioReportHolder holder) =>
            holder.Report is { } report
                ? Results.Content(report.ToHtml(), "text/html")
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        app.Run();
    }

    /// <summary>
    /// Chaque scenario doit avoir un nom non vide et unique : c'est ce qui identifie ses
    /// statistiques dans le rapport combine, et un nom duplique y serait ambigu sans que rien ne
    /// l'empeche techniquement (les chaines de mesure restent isolees quoi qu'il arrive).
    /// </summary>
    private static void ValidateScenarioNames(IReadOnlyList<ScenarioOptions> scenarios)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (ScenarioOptions scenario in scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.Name))
            {
                throw new ArgumentException("Chaque scenario concurrent doit avoir un nom non vide.");
            }

            if (!names.Add(scenario.Name))
            {
                throw new ArgumentException(
                    $"Nom de scenario duplique : '{scenario.Name}'. Chaque scenario concurrent doit avoir un nom unique.");
            }
        }
    }

    private static ScenarioRunSpec BuildSpec(WebApplicationBuilder builder, SiroccoHostOptions siroccoOptions, ScenarioOptions scenario)
    {
        StandaloneHost.LoadModelPlan plan = StandaloneHost.BuildLoadModel(
            scenario.MaxVirtualUsers,
            scenario.Profile,
            scenario.ClosedModelDuration,
            scenario.RampVus,
            scenario.SharedIterations,
            scenario.IterationsPerVirtualUser,
            scenario.MaxRequestsPerSecond ?? siroccoOptions.MaxRequestsPerSecond);

        IWorkflow workflow = StandaloneHost.BuildWorkflow(
            builder,
            scenario.ScenarioFile,
            scenario.Workflow,
            scenario.PluginWorkflowType,
            scenario.PluginPackageId,
            scenario.PluginPackageVersion,
            scenario.PluginPackageSources,
            siroccoOptions.AllowUnsignedPlugins);

        string targetBaseUrl = string.IsNullOrWhiteSpace(scenario.TargetBaseUrl) ? siroccoOptions.TargetBaseUrl : scenario.TargetBaseUrl;
        HttpClient httpClient = StandaloneHost.BuildHttpClient(targetBaseUrl);

        return new ScenarioRunSpec
        {
            Name = scenario.Name,
            Workflow = workflow,
            Scheduler = plan.Scheduler,
            HttpClient = httpClient,
            Options = new LoadTestOptions
            {
                MaxVirtualUsers = plan.EffectiveMaxVirtualUsers,
                RampProfile = plan.RampProfile,
                IterationsPerVirtualUser = plan.IterationsPerVirtualUser,
            },
            Thresholds = scenario.Thresholds,
            IsClosedModel = scenario.IsClosedModel,
        };
    }
}