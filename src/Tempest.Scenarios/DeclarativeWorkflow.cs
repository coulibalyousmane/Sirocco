using System.Text;
using System.Text.RegularExpressions;
using Tempest.Domain.Data;
using Tempest.Domain.Declarative;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;
using Tempest.Scenarios.Data;

namespace Tempest.Scenarios;

/// <summary>
/// Scenario pilote par une <see cref="ScenarioDefinition"/> plutot que par du C# ecrit a la
/// main : c'est la brique qui permet de faire evoluer un parcours HTTP sans recompiler.
/// <para>
/// Une etape peut extraire une valeur de sa reponse (<see cref="HttpStepDefinition.Extract"/>)
/// et une etape suivante la reutiliser via <c>{{nom}}</c> — corrélation limitee au
/// <b>vocabulaire</b> Regex/XPath, sans branchement ni boucle. Les variables extraites sont
/// locales a une iteration : elles ne survivent pas a la suivante. Les jeux de donnees
/// (<see cref="ScenarioDefinition.Datasets"/>) suivent la meme voie de substitution, sous
/// <c>{{nom.colonne}}</c> : une ligne est choisie une fois par iteration et vaut pour toutes
/// les etapes de cette iteration.
/// </para>
/// <para>
/// Un check (<see cref="CheckRule"/>) est une assertion logique sur la reponse d'une etape,
/// rapportee sur sa <b>propre</b> etape du rapport — jamais sur celle de la requete HTTP dont
/// il derive, qui garde l'issue que <see cref="HttpStepDefinition.ExpectedStatusCodes"/> lui
/// donne, que le check reussisse ou non.
/// </para>
/// <para>
/// Une metrique personnalisee (<see cref="MetricRule"/>) alimente un compteur/jauge/taux/tendance
/// depuis la reponse d'une etape, agrege separement du tableau d'etapes
/// (<see cref="Metrics.LoadTestReport.CustomMetrics"/>) — contrairement a un check, ce n'est pas
/// une assertion et elle ne devient pas sa propre etape.
/// </para>
/// <para>
/// Un <see cref="HttpStepDefinition.ThinkTime"/> (optionnel) suspend l'utilisateur virtuel apres
/// cette etape, avant la suivante — un temps de reflexion, jamais mesure comme latence d'etape.
/// Sans effet sur le moteur : un utilisateur virtuel qui dort ne fait que retarder son prochain
/// jeton pris dans le canal, exactement comme le ferait une reponse HTTP lente.
/// </para>
/// <para>
/// Le <see cref="HttpStepDefinition.Group"/> d'une etape (optionnel) est prefixe a son nom pour
/// former <see cref="HttpStepDefinition.QualifiedName"/>, le nom effectivement enregistre : deux
/// etapes de meme nom dans deux groupes differents restent deux lignes distinctes du rapport
/// (<c>"checkout/pay"</c>, <c>"refund/pay"</c>), sans qu'aucun StepId ne devienne conceptuellement
/// different d'un autre. Le rapport affiche ce nom qualifie tel quel, sans tenter de le
/// reinterpreter comme une arborescence : un nom d'etape est une chaine libre, et y detecter une
/// hierarchie a l'affichage romprait pour toute etape dont le nom contient un '/' sans intention
/// de groupe. Les <see cref="ScenarioDefinition.Tags"/> du scenario sont exposees via
/// <see cref="Tags"/> et reportees telles quelles dans le rapport final, sans jamais entrer dans
/// l'agregation.
/// </para>
/// </summary>
public sealed partial class DeclarativeWorkflow : IWorkflow
{
    private static readonly Regex _placeholderPattern = PlaceholderPattern();

    private readonly ScenarioDefinition _definition;
    private readonly StepId[] _stepIds;
    private readonly Dictionary<string, DataSet> _dataSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StepId> _checkStepIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CustomMetricId> _metricIds = new(StringComparer.Ordinal);

    /// <summary>Cree le scenario a partir d'une description deja validee.</summary>
    /// <param name="definition">Description du scenario.</param>
    public DeclarativeWorkflow(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();

        _definition = definition;
        _stepIds = new StepId[definition.Steps.Count];
    }

    /// <inheritdoc />
    public string Name => _definition.Name;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Tags => _definition.Tags;

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        for (int i = 0; i < _definition.Steps.Count; i++)
        {
            HttpStepDefinition step = _definition.Steps[i];
            _stepIds[i] = registry.Register(step.QualifiedName);

            // Chaque check devient sa propre etape dans le rapport, distincte de celle-ci :
            // ScenarioDefinition.Validate() garantit deja qu'aucun nom ne collisionne.
            foreach (CheckRule check in step.Checks)
            {
                _checkStepIds[check.Name] = registry.Register(check.Name);
            }
        }
    }

    /// <inheritdoc />
    public void RegisterMetrics(CustomMetricRegistry registry)
    {
        foreach (HttpStepDefinition step in _definition.Steps)
        {
            foreach (MetricRule metric in step.Metrics)
            {
                _metricIds[metric.Name] = registry.Register(metric.Name, metric.Kind);
            }
        }
    }

    /// <summary>Charge les jeux de donnees du scenario, une seule fois avant le premier tir.</summary>
    public ValueTask SetUpAsync(CancellationToken cancellationToken)
    {
        foreach (DataSetDefinition dataset in _definition.Datasets)
        {
            _dataSets[dataset.Name] = DataSetLoader.LoadFromFile(dataset.Path, dataset.Strategy);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        // Une seule allocation par utilisateur virtuel pour tout le tir : reutilisee (et videe)
        // a chaque iteration, comme le fait CheckoutSession dans DynamicCheckoutWorkflow.
        Dictionary<string, string> variables = (Dictionary<string, string>)(context.State ??=
            new Dictionary<string, string>(StringComparer.Ordinal));
        variables.Clear();

        foreach ((string name, DataSet dataSet) in _dataSets)
        {
            foreach ((string column, string value) in dataSet.Pick(context))
            {
                variables[$"{name}.{column}"] = value;
            }
        }

        for (int i = 0; i < _definition.Steps.Count; i++)
        {
            HttpStepDefinition step = _definition.Steps[i];
            await ExecuteStepAsync(context, step, _stepIds[i], _checkStepIds, _metricIds, variables, cancellationToken).ConfigureAwait(false);

            // Hors de la portee de l'etape (scope.Complete deja appele) : une pause n'est jamais
            // une latence de requete, seulement un delai avant la suivante.
            if (step.ThinkTime is { } thinkTime)
            {
                await Task.Delay(thinkTime.Sample(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask ExecuteStepAsync(
        IVirtualUserContext context,
        HttpStepDefinition step,
        StepId stepId,
        Dictionary<string, StepId> checkStepIds,
        Dictionary<string, CustomMetricId> metricIds,
        Dictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(stepId);

        HttpRequestMessage? request = TryBuildRequest(step, variables);
        if (request is null)
        {
            // Une variable referencee par {{nom}} n'a pas ete extraite avant cette etape :
            // erreur de configuration du scenario, pas un echec de transport a mesurer comme tel.
            scope.Fail(RequestOutcome.AssertionFailed);
            return;
        }

        using (request)
        {
            HttpResponseMessage response;
            try
            {
                response = await context.HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
                int statusCode = (int)response.StatusCode;
                long bytesReceived = response.Content.Headers.ContentLength.GetValueOrDefault();

                RequestOutcome outcome = step.ExpectedStatusCodes.Count == 0
                    ? StepScope.ClassifyHttp(statusCode)
                    : step.ExpectedStatusCodes.Contains(statusCode) ? RequestOutcome.Success : RequestOutcome.AssertionFailed;

                if (step.Extract.Count > 0 || step.Checks.Count > 0 || step.Metrics.Count > 0)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    foreach (ExtractionRule rule in step.Extract)
                    {
                        if (rule.TryExtract(body, out string? value))
                        {
                            variables[rule.Variable] = value!;
                        }
                        else if (outcome == RequestOutcome.Success)
                        {
                            // Extraction manquee : le scenario attendait une valeur que la
                            // reponse n'a pas fournie, ce n'est pas un succes silencieux.
                            outcome = RequestOutcome.AssertionFailed;
                        }
                    }

                    // Chaque check est rapporte sur sa propre etape, jamais sur "outcome" : un
                    // check qui echoue ne fait jamais echouer CETTE requete HTTP, seulement sa
                    // propre ligne du rapport (voir CheckRule).
                    foreach (CheckRule check in step.Checks)
                    {
                        StepScope checkScope = context.BeginStep(checkStepIds[check.Name]);
                        checkScope.Complete(
                            check.Evaluate(body) ? RequestOutcome.Success : RequestOutcome.AssertionFailed,
                            statusCode,
                            bytesReceived: 0);
                    }

                    // Une metrique personnalisee n'est ni une etape ni une assertion : une
                    // extraction manquee ou non numerique n'enregistre simplement rien cette
                    // fois-ci (voir MetricRule.Evaluate), sans jamais toucher "outcome".
                    foreach (MetricRule metric in step.Metrics)
                    {
                        if (metric.Evaluate(body) is { } value)
                        {
                            context.RecordCustomMetric(metricIds[metric.Name], value);
                        }
                    }
                }

                scope.Complete(outcome, statusCode, bytesReceived);
            }
        }
    }

    private static HttpRequestMessage? TryBuildRequest(HttpStepDefinition step, IReadOnlyDictionary<string, string> variables)
    {
        if (!TrySubstitute(step.Path, variables, out string path))
        {
            return null;
        }

        HttpRequestMessage request = new(new HttpMethod(step.Method), path);

        foreach ((string name, string value) in step.Headers)
        {
            if (!TrySubstitute(value, variables, out string substitutedValue))
            {
                request.Dispose();
                return null;
            }

            request.Headers.TryAddWithoutValidation(name, substitutedValue);
        }

        if (step.Body is not null)
        {
            if (!TrySubstitute(step.Body, variables, out string body))
            {
                request.Dispose();
                return null;
            }

            request.Content = new StringContent(body, Encoding.UTF8, step.ContentType);
        }

        return request;
    }

    /// <summary>
    /// Remplace chaque <c>{{nom}}</c> ou <c>{{jeu.colonne}}</c> par la variable correspondante.
    /// Renvoie <see langword="false"/> si au moins un nom reference n'a pas ete extrait — le
    /// gabarit est alors laisse tel quel dans <paramref name="result"/>, sans etre envoye.
    /// </summary>
    private static bool TrySubstitute(string template, IReadOnlyDictionary<string, string> variables, out string result)
    {
        if (!template.Contains("{{", StringComparison.Ordinal))
        {
            result = template;
            return true;
        }

        bool allResolved = true;
        result = _placeholderPattern.Replace(template, match =>
        {
            if (variables.TryGetValue(match.Groups[1].Value, out string? value))
            {
                return value;
            }

            allResolved = false;
            return match.Value;
        });

        return allResolved;
    }

    [GeneratedRegex(@"\{\{([\w.]+)\}\}")]
    private static partial Regex PlaceholderPattern();
}