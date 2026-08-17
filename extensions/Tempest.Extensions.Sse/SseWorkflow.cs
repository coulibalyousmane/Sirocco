using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;

namespace Tempest.Extensions.Sse;

/// <summary>
/// Deuxieme protocole de reference de la roadmap phase 6 : contrairement a
/// <c>Tempest.Extensions.Sql</c>, qui validait le contrat de plugin contre un protocole reellement
/// different de HTTP, celui-ci le valide contre un <b>usage</b> different d'
/// <see cref="IVirtualUserContext.HttpClient"/> — une reponse en flux continu
/// (<c>text/event-stream</c>) lue evenement par evenement au fil de l'eau, plutot que l'aller-retour
/// requete/reponse unique de tout le reste du depot (<c>DynamicCheckoutWorkflow</c>,
/// <c>Tempest.SamplePlugin</c>...).
/// <para>
/// Consequence directe : contrairement au plugin SQL, celui-ci n'ignore pas <c>--target-url</c> —
/// il l'utilise normalement via le <see cref="IVirtualUserContext.HttpClient"/> partage, dont
/// l'adresse de base est deja celle de la cible. Seul le chemin relatif (et le nombre d'evenements
/// attendus) se configure par variable d'environnement, comme <c>Tempest.SamplePlugin</c>.
/// </para>
/// <para>
/// Limite assumee : le nombre d'evenements attendu est fixe par iteration (parametre <c>count</c>
/// de la requete) plutot que decouvert dynamiquement — un flux qui ne se termine jamais serait
/// borne par <see cref="_timeout"/>, compte comme un <see cref="RequestOutcome.Timeout"/>, mais
/// aucun scenario de reference ne l'exerce ici.
/// </para>
/// </summary>
public sealed class SseWorkflow : IWorkflow
{
    private const string PATH_ENVIRONMENT_VARIABLE = "TEMPEST_SSE_PLUGIN_PATH";
    private const string EVENT_COUNT_ENVIRONMENT_VARIABLE = "TEMPEST_SSE_PLUGIN_EVENT_COUNT";
    private const string TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE = "TEMPEST_SSE_PLUGIN_TIMEOUT_SECONDS";
    private const string DEFAULT_PATH = "/api/events/stream";
    private const int DEFAULT_EVENT_COUNT = 20;
    private const int DEFAULT_TIMEOUT_SECONDS = 10;
    private const string EVENT_STREAM_MEDIA_TYPE = "text/event-stream";
    private const string DATA_LINE_PREFIX = "data:";

    private readonly string _requestUri;
    private readonly int _expectedEventCount;
    private readonly TimeSpan _timeout;

    private StepId _connectStep;
    private StepId _receiveStep;

    public SseWorkflow()
    {
        string path = Environment.GetEnvironmentVariable(PATH_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredPath
            ? configuredPath
            : DEFAULT_PATH;

        _expectedEventCount = Environment.GetEnvironmentVariable(EVENT_COUNT_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredCount
            && int.TryParse(configuredCount, out int parsedCount) && parsedCount > 0
            ? parsedCount
            : DEFAULT_EVENT_COUNT;

        int timeoutSeconds = Environment.GetEnvironmentVariable(TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredTimeout
            && int.TryParse(configuredTimeout, out int parsedTimeout) && parsedTimeout > 0
            ? parsedTimeout
            : DEFAULT_TIMEOUT_SECONDS;
        _timeout = TimeSpan.FromSeconds(timeoutSeconds);

        // Le nombre d'evenements voulus voyage dans la requete elle-meme : la cible (reelle ou de
        // test) en a besoin pour savoir quand arreter d'ecrire, ce plugin pour savoir quand arreter
        // de lire.
        _requestUri = $"{path}?count={_expectedEventCount}";
    }

    /// <inheritdoc />
    public string Name => "sse-plugin";

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _connectStep = registry.Register("SSE connect");
        _receiveStep = registry.Register("SSE receive events");
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        HttpResponseMessage? response = await ConnectAsync(context, cancellationToken, timeoutSource.Token).ConfigureAwait(false);
        if (response is null)
        {
            return;
        }

        try
        {
            await ReceiveEventsAsync(context, response, cancellationToken, timeoutSource.Token).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async ValueTask<HttpResponseMessage?> ConnectAsync(
        IVirtualUserContext context,
        CancellationToken cancellationToken,
        CancellationToken connectCancellationToken)
    {
        StepScope scope = context.BeginStep(_connectStep);
        HttpResponseMessage? response = null;

        try
        {
            response = await context.HttpClient
                .GetAsync(_requestUri, HttpCompletionOption.ResponseHeadersRead, connectCancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                scope.CompleteHttp((int)response.StatusCode);
                response.Dispose();
                return null;
            }

            if (response.Content.Headers.ContentType?.MediaType != EVENT_STREAM_MEDIA_TYPE)
            {
                // Le transport a reussi mais la cible ne repond pas en flux d'evenements : une
                // assertion metier ratee, pas un incident de transport.
                scope.Fail(RequestOutcome.AssertionFailed, (int)response.StatusCode);
                response.Dispose();
                return null;
            }

            scope.CompleteHttp((int)response.StatusCode);
            return response;
        }
        catch (HttpRequestException)
        {
            response?.Dispose();
            scope.Fail(RequestOutcome.ConnectionError);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            scope.Fail(RequestOutcome.Timeout);
            return null;
        }
    }

    private async ValueTask ReceiveEventsAsync(
        IVirtualUserContext context,
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        CancellationToken readCancellationToken)
    {
        StepScope scope = context.BeginStep(_receiveStep);

        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(readCancellationToken).ConfigureAwait(false);
            using StreamReader reader = new(stream);

            int eventsReceived = 0;
            bool currentEventHasData = false;

            while (true)
            {
                string? line = await reader.ReadLineAsync(readCancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    // Ligne vide = fin d'evenement, convention SSE : ne compter que les evenements
                    // porteurs d'au moins une ligne "data:", pas les commentaires/keep-alive.
                    if (currentEventHasData)
                    {
                        eventsReceived++;
                        currentEventHasData = false;
                    }

                    continue;
                }

                if (line.StartsWith(DATA_LINE_PREFIX, StringComparison.Ordinal))
                {
                    currentEventHasData = true;
                }
            }

            if (eventsReceived != _expectedEventCount)
            {
                scope.Fail(RequestOutcome.AssertionFailed);
                return;
            }

            scope.Success();
        }
        catch (IOException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
        }
        catch (HttpRequestException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
        }
    }
}