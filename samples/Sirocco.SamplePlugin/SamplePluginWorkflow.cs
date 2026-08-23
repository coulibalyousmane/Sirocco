using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;

namespace Sirocco.SamplePlugin;

/// <summary>
/// Plugin de reference du contrat de la roadmap phase 6 : un <see cref="IWorkflow"/> ordinaire,
/// compile dans une assembly independante de ce depot, charge au runtime par
/// <c>PluginWorkflowLoader</c> — pas un scenario integre, pas un scenario scripte. Il n'utilise
/// que <see cref="IWorkflow"/>/<see cref="IVirtualUserContext"/>/<c>StepScope</c>
/// (<c>Sirocco.Domain</c>), exactement ce qu'un protocole tiers reel (SQL, MQTT, GraphQL...)
/// utiliserait.
/// <para>
/// Limite assumee de la premiere version du contrat : Sirocco n'injecte aucune configuration dans
/// un plugin. Celui-ci lit donc son propre reglage (le chemin interroge) depuis une variable
/// d'environnement plutot que depuis <c>appsettings.json</c> — un plugin gere sa propre
/// configuration, voir <c>PluginWorkflowLoader</c>.
/// </para>
/// </summary>
public sealed class SamplePluginWorkflow : IWorkflow
{
    private const string DEFAULT_PATH = "/api/catalog/products";
    private const string PATH_ENVIRONMENT_VARIABLE = "SIROCCO_SAMPLE_PLUGIN_PATH";

    private readonly string _path;
    private StepId _browseStep;

    public SamplePluginWorkflow()
    {
        _path = Environment.GetEnvironmentVariable(PATH_ENVIRONMENT_VARIABLE) is { Length: > 0 } configured
            ? configured
            : DEFAULT_PATH;
    }

    /// <inheritdoc />
    public string Name => "sample-plugin";

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _browseStep = registry.Register($"GET {_path} (plugin)");
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_browseStep);

        HttpResponseMessage response;
        try
        {
            response = await context.HttpClient.GetAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
            return;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
            return;
        }

        using (response)
        {
            scope.CompleteHttp((int)response.StatusCode, response.Content.Headers.ContentLength.GetValueOrDefault());
        }
    }
}