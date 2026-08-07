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
/// </summary>
public sealed partial class DeclarativeWorkflow : IWorkflow
{
    private static readonly Regex _placeholderPattern = PlaceholderPattern();

    private readonly ScenarioDefinition _definition;
    private readonly StepId[] _stepIds;
    private readonly Dictionary<string, DataSet> _dataSets = new(StringComparer.Ordinal);

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
    public void RegisterSteps(StepRegistry registry)
    {
        for (int i = 0; i < _definition.Steps.Count; i++)
        {
            _stepIds[i] = registry.Register(_definition.Steps[i].Name);
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
            await ExecuteStepAsync(context, _definition.Steps[i], _stepIds[i], variables, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask ExecuteStepAsync(
        IVirtualUserContext context,
        HttpStepDefinition step,
        StepId stepId,
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

                if (step.Extract.Count > 0)
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