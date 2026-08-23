using System.Text;
using System.Text.Json;
using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;

namespace Sirocco.Extensions.GraphQl;

/// <summary>
/// Quatrieme et dernier protocole de reference de la roadmap phase 6 : comme
/// <c>Sirocco.Extensions.Sse</c>, reste au-dessus de HTTP plutot que d'en changer, mais valide un
/// autre aspect du contrat — un point d'entree unique (toujours <c>POST {chemin}</c>) ou le succes
/// ou l'echec se lit dans le corps JSON (champ <c>errors</c>), jamais dans le code de statut HTTP,
/// qui reste 200 meme quand la requete echoue cote metier. Toute etape HTTP du reste du depot
/// (<c>DynamicCheckoutWorkflow</c>, <c>Sirocco.SamplePlugin</c>...) utilise au contraire le code de
/// statut comme seul signal — <see cref="RequestOutcome.HttpError"/> ne suffit donc jamais ici, une
/// reponse GraphQL en erreur doit etre classee <see cref="RequestOutcome.AssertionFailed"/> apres
/// inspection explicite du corps.
/// <para>
/// Deux etapes reelles par iteration : une requete en lecture (<c>GraphQL query</c>, verifie que la
/// liste de produits n'est jamais vide) et une mutation (<c>GraphQL mutation</c>, passe une commande
/// pour un identifiant tire dans <c>[1, SIROCCO_GRAPHQL_PLUGIN_PRODUCT_ID_MAX]</c>) — memes deux
/// natures d'operation (lecture/ecriture) que <c>Sirocco.Extensions.Sql</c>, sous revetement HTTP.
/// </para>
/// <para>
/// Limite assumee, comme les protocoles de reference precedents : aucune configuration injectee par
/// Sirocco, ce plugin lit la sienne (chemin, borne haute des identifiants) depuis des variables
/// d'environnement. Ni variables GraphQL ni alias ne sont geres : les valeurs sont inlinees dans la
/// chaine de requete, la cible de reference n'en a pas besoin pour prouver le contrat.
/// </para>
/// </summary>
public sealed class GraphQlWorkflow : IWorkflow
{
    private const string PATH_ENVIRONMENT_VARIABLE = "SIROCCO_GRAPHQL_PLUGIN_PATH";
    private const string PRODUCT_ID_MAX_ENVIRONMENT_VARIABLE = "SIROCCO_GRAPHQL_PLUGIN_PRODUCT_ID_MAX";
    private const string DEFAULT_PATH = "/graphql";
    private const int DEFAULT_PRODUCT_ID_MAX = 50;

    private readonly string _path;
    private readonly int _productIdMax;

    private StepId _queryStep;
    private StepId _mutationStep;

    public GraphQlWorkflow()
    {
        _path = Environment.GetEnvironmentVariable(PATH_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredPath
            ? configuredPath
            : DEFAULT_PATH;

        _productIdMax = Environment.GetEnvironmentVariable(PRODUCT_ID_MAX_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredMax
            && int.TryParse(configuredMax, out int parsedMax) && parsedMax > 0
            ? parsedMax
            : DEFAULT_PRODUCT_ID_MAX;
    }

    /// <inheritdoc />
    public string Name => "graphql-plugin";

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _queryStep = registry.Register("GraphQL query");
        _mutationStep = registry.Register("GraphQL mutation");
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        await QueryProductsAsync(context, cancellationToken).ConfigureAwait(false);

        int productId = Random.Shared.Next(1, _productIdMax + 1);
        int quantity = Random.Shared.Next(1, 4);
        await PlaceOrderAsync(context, productId, quantity, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask QueryProductsAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_queryStep);

        try
        {
            using HttpResponseMessage response = await PostAsync(context, "{ products { id name price } }", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                scope.CompleteHttp((int)response.StatusCode);
                return;
            }

            using JsonDocument document = await ParseResponseAsync(response, cancellationToken).ConfigureAwait(false);
            if (TryGetGraphQlError(document, out _))
            {
                scope.Fail(RequestOutcome.AssertionFailed, (int)response.StatusCode);
                return;
            }

            JsonElement products = document.RootElement.GetProperty("data").GetProperty("products");
            if (products.ValueKind != JsonValueKind.Array || products.GetArrayLength() == 0)
            {
                // Reponse GraphQL bien formee mais vide : une assertion metier ratee, pas un
                // incident de transport — le catalogue de reference n'est jamais cense l'etre.
                scope.Fail(RequestOutcome.AssertionFailed, (int)response.StatusCode);
                return;
            }

            scope.CompleteHttp((int)response.StatusCode);
        }
        catch (HttpRequestException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
        }
    }

    private async ValueTask PlaceOrderAsync(IVirtualUserContext context, int productId, int quantity, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_mutationStep);

        try
        {
            string query = $"mutation {{ placeOrder(productId: {productId}, quantity: {quantity}) {{ id total }} }}";
            using HttpResponseMessage response = await PostAsync(context, query, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                scope.CompleteHttp((int)response.StatusCode);
                return;
            }

            using JsonDocument document = await ParseResponseAsync(response, cancellationToken).ConfigureAwait(false);
            if (TryGetGraphQlError(document, out _))
            {
                // Le transport a reussi (HTTP 200) mais la mutation a echoue cote metier : c'est
                // precisement ce que ce protocole de reference existe pour verifier.
                scope.Fail(RequestOutcome.AssertionFailed, (int)response.StatusCode);
                return;
            }

            scope.CompleteHttp((int)response.StatusCode);
        }
        catch (HttpRequestException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
        }
    }

    private async ValueTask<HttpResponseMessage> PostAsync(IVirtualUserContext context, string query, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new { query });
        using StringContent content = new(payload, Encoding.UTF8, "application/json");
        return await context.HttpClient.PostAsync(_path, content, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<JsonDocument> ParseResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Une reponse GraphQL porte ses erreurs dans <c>errors</c>, jamais dans le code de statut HTTP
    /// (reste 200 y compris pour une erreur de resolveur) : c'est ce champ, pas
    /// <see cref="HttpResponseMessage.IsSuccessStatusCode"/>, qui distingue succes et echec ici.
    /// </summary>
    private static bool TryGetGraphQlError(JsonDocument document, out string? message)
    {
        if (document.RootElement.TryGetProperty("errors", out JsonElement errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            message = errors[0].TryGetProperty("message", out JsonElement messageElement) ? messageElement.GetString() : null;
            return true;
        }

        message = null;
        return false;
    }
}