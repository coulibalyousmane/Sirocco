using System.Net;

namespace Sirocco.UnitTests.TestDoubles;

/// <summary>Requete capturee par <see cref="StubHttpMessageHandler"/>, independante du cycle de vie du message d'origine.</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    string Path,
    string? AuthorizationHeader,
    string? Body,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Valeur d'un en-tete requete, ou <see langword="null"/> s'il est absent.</summary>
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>
/// Gestionnaire HTTP de test : route chaque requete vers une reponse programmee par methode
/// et par chemin, sans jamais toucher le reseau.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<(HttpMethod Method, string Path), Func<HttpRequestMessage, HttpResponseMessage>> _routes = [];

    /// <summary>Requetes recues, dans l'ordre d'arrivee.</summary>
    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>Programme une reponse construite dynamiquement pour une methode et un chemin.</summary>
    public StubHttpMessageHandler On(HttpMethod method, string path, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes[(method, path)] = respond;
        return this;
    }

    /// <summary>Programme une reponse JSON fixe pour une methode et un chemin.</summary>
    public StubHttpMessageHandler On(HttpMethod method, string path, HttpStatusCode statusCode, string? jsonBody = null) =>
        On(method, path, _ => new HttpResponseMessage(statusCode)
        {
            Content = jsonBody is null ? null : JsonStringContent(jsonBody),
        });

    /// <summary>Programme une reponse qui echoue au niveau transport, sans jamais atteindre l'application.</summary>
    public StubHttpMessageHandler OnConnectionFailure(HttpMethod method, string path) =>
        On(method, path, _ => throw new HttpRequestException("Cible injoignable (simulee par le test)."));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!.AbsolutePath,
            request.Headers.Authorization?.ToString(),
            body,
            headers));

        return _routes.TryGetValue((request.Method, request.RequestUri.AbsolutePath), out var respond)
            ? respond(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static StringContent JsonStringContent(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");
}