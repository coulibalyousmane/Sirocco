using System.Net.WebSockets;
using System.Text.Json;
using Bogus;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Server;
using Sirocco.SampleTarget;
using Sirocco.SampleTarget.Contracts;
using Sirocco.SampleTarget.GraphQl;
using Sirocco.SampleTarget.Services;

// Cible de demonstration pour un premier tir reel : pas de logique metier, juste assez de
// comportement realiste (latence, capacite finie, jetons qui expirent) pour que Sirocco ait
// quelque chose de vrai a mesurer plutot qu'un echo instantane.

var builder = WebApplication.CreateBuilder(args);

// Port gRPC dedie, lu avant Build() : Kestrel doit connaitre ses points d'ecoute avant que le
// conteneur ne soit construit, donc avant que SampleTargetOptions ne soit resolu par DI.
int grpcPort = builder.Configuration.GetValue("SampleTarget:GrpcPort", SampleTargetOptions.DEFAULT_GRPC_PORT);

// Port principal (REST, WebSocket) tel que fourni par --urls / ASPNETCORE_URLS / launchSettings.
// Des qu'un point d'ecoute est configure explicitement ci-dessous, Kestrel cesse de se lier
// automatiquement a partir de cette configuration — il faut donc le relier nous-memes.
// Extraction manuelle du port plutot que new Uri(...) : la convention "+" ("toutes les
// interfaces", courante en conteneur — ASPNETCORE_URLS=http://+:5281) n'est pas un hote URI
// valide au sens strict de System.Uri, qui leve UriFormatException — constate en pratique
// au premier demarrage conteneurise, pas suppose.
int mainPort = ExtractPort(builder.Configuration["urls"] ?? "http://localhost:5000");

// Port gRPC separe, HTTP/2 pur : verifie par un vrai demarrage, Kestrel ne multiplexe PAS
// HTTP/1.1 et HTTP/2 sur un meme port sans TLS (ALPN indisponible en clair) — un point
// d'ecoute mixte y reste silencieusement en HTTP/1.1 seul, ce qui casserait gRPC. REST et
// WebSocket restent sur le port principal, en HTTP/1.1.
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(mainPort, static listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
    serverOptions.ListenAnyIP(grpcPort, static listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

builder.Services.AddGrpc();
builder.Services.Configure<SampleTargetOptions>(builder.Configuration.GetSection("SampleTarget"));
builder.Services.AddSingleton(static provider => provider.GetRequiredService<IOptions<SampleTargetOptions>>().Value);
builder.Services.AddSingleton(static provider =>
    new ConcurrencyGate(provider.GetRequiredService<SampleTargetOptions>().MaxConcurrentCheckouts));
builder.Services.AddSingleton(static provider =>
    new TokenStore(TimeSpan.FromSeconds(provider.GetRequiredService<SampleTargetOptions>().TokenLifetimeSeconds)));
builder.Services.AddSingleton(static provider => BuildCatalog(provider.GetRequiredService<SampleTargetOptions>()));

builder.Services.ConfigureHttpJsonOptions(static jsonOptions =>
    jsonOptions.SerializerOptions.TypeInfoResolverChain.Insert(0, SampleTargetJsonContext.Default));

WebApplication app = builder.Build();

SampleTargetOptions options = app.Services.GetRequiredService<SampleTargetOptions>();
TokenStore tokens = app.Services.GetRequiredService<TokenStore>();
ConcurrencyGate gate = app.Services.GetRequiredService<ConcurrencyGate>();
Product[] catalog = app.Services.GetRequiredService<Product[]>();

// Courtier MQTT embarque pour le protocole de reference MQTT (Sirocco.Extensions.Mqtt) : un
// processus .NET ordinaire, aucun serveur Kestrel/ASP.NET requis contrairement a gRPC/WebSocket —
// demarre et arrete avec la cible elle-meme, sans infrastructure externe a installer.
using MqttServer mqttServer = new MqttServerFactory().CreateMqttServer(
    new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(options.MqttPort).Build());
await mqttServer.StartAsync();

// Schema GraphQL reel (protocole de reference Sirocco.Extensions.GraphQl) : construits une fois,
// reutilises par chaque requete comme en production. L'executeur et le serialiseur sont separes
// du schema dans l'API de GraphQL.NET — le schema decrit, l'executeur joue, le serialiseur rend.
SampleGraphQlSchema graphQlSchema = new(catalog);
IDocumentExecuter graphQlExecuter = new DocumentExecuter();
GraphQLSerializer graphQlSerializer = new();

app.UseWebSockets();
app.MapGrpcService<EchoGrpcService>();

app.MapPost("/api/auth/login", async (LoginRequest _, CancellationToken cancellationToken) =>
{
    await SimulateLatencyAsync(options, cancellationToken);
    return Results.Ok(new LoginResponse(tokens.Issue()));
});

app.MapGet("/api/catalog/products", async (CancellationToken cancellationToken) =>
{
    await SimulateLatencyAsync(options, cancellationToken);
    return Results.Ok(catalog);
});

// Page servie par /demo. Auto-suffisante (aucune ressource externe : ni CDN, ni police, ni image
// distante) pour que la mesure ne depende que de la cible, jamais du reseau de quelqu'un d'autre.
const string DEMO_PAGE_HTML = """
    <!doctype html>
    <html lang="fr">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>Sirocco — page de demonstration</title>
      <style>
        body { font-family: system-ui, sans-serif; margin: 0; padding: 24px; }
        #banniere { background: #10263f; color: #fff; padding: 18px; font-size: 18px; }
        #heros { background: #e8eef5; min-height: 360px; padding: 32px; font-size: 34px; line-height: 1.3; }
        .attente { color: #7a8798; font-size: 15px; }
      </style>
    </head>
    <body>
      <div id="conteneur-banniere"></div>
      <main>
        <p class="attente">Chargement du contenu principal...</p>
        <div id="conteneur-heros"></div>
      </main>
      <script>
        // Le bloc principal n'existe PAS au premier rendu : la page peint d'abord une seule ligne
        // (c'est le FCP), puis ce grand bloc arrive et devient le plus grand element peint (le LCP).
        // Sans cette separation, le heros serait deja le plus grand element au premier rendu et LCP
        // vaudrait exactement FCP — un tir vert ne prouverait alors pas que LCP est reellement lu.
        setTimeout(function () {
          var heros = document.createElement('div');
          heros.id = 'heros';
          heros.textContent = 'Contenu principal : le plus grand element peint de cette page.';
          document.getElementById('conteneur-heros').appendChild(heros);
        }, 150);

        // Banniere inseree APRES coup, au-dessus du contenu : tout ce qui suit se decale vers le
        // bas. C'est un vrai glissement de mise en page, donc un CLS non nul a mesurer.
        setTimeout(function () {
          var conteneur = document.getElementById('conteneur-banniere');
          var banniere = document.createElement('div');
          banniere.id = 'banniere';
          banniere.textContent = 'Banniere inseree apres le rendu initial — provoque un glissement.';
          conteneur.appendChild(banniere);
        }, 260);
      </script>
    </body>
    </html>
    """;

// Page HTML reelle pour le protocole de reference navigateur (Sirocco.Extensions.Browser). Elle
// n'est pas decorative : elle est construite pour que les trois Web Vitals mesures soient
// NON NULS, sans quoi un tir vert ne prouverait rien.
//   - TTFB : la latence simulee de la cible s'applique comme a tout autre endpoint.
//   - LCP  : le bloc principal n'apparait qu'apres un court delai cote client, donc le plus grand
//            element peint arrive apres le premier rendu.
//   - CLS  : une banniere est inseree APRES coup au-dessus du contenu, qui se decale donc vers le
//            bas — un vrai glissement de mise en page, exactement ce que CLS quantifie.
app.MapGet("/demo", async (CancellationToken cancellationToken) =>
{
    await SimulateLatencyAsync(options, cancellationToken);
    return Results.Content(DEMO_PAGE_HTML, "text/html; charset=utf-8");
});

app.MapPost("/api/checkout", async (HttpContext http, CheckoutRequest request, CancellationToken cancellationToken) =>
{
    if (!tokens.IsValid(BearerTokenOf(http)))
    {
        return Results.Unauthorized();
    }

    if (!await gate.TryEnterAsync(TimeSpan.FromMilliseconds(options.QueueWaitMilliseconds), cancellationToken))
    {
        // La cible est saturee : elle refuse plutot que de faire attendre indefiniment,
        // comme le ferait un vrai backend a bout de capacite.
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        await SimulateLatencyAsync(options, cancellationToken);

        if (Random.Shared.NextDouble() < options.ErrorRate)
        {
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        decimal total = request.Items.Sum(item => PriceOf(catalog, item.ProductId) * item.Quantity);
        return Results.Ok(new CheckoutResponse(Guid.NewGuid().ToString("N"), total));
    }
    finally
    {
        gate.Exit();
    }
});

app.Map("/ws/echo", async http =>
{
    if (!http.WebSockets.IsWebSocketRequest)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync();
    await EchoLoopAsync(socket, http.RequestAborted);
});

// Point d'ecoute pour le protocole de reference SSE (Sirocco.Extensions.Sse) : le nombre
// d'evenements est pilote par la requete elle-meme, pas par la config du serveur, pour qu'un seul
// point d'ecoute serve indifferemment un tir court ou long.
const int SSE_DEFAULT_EVENT_COUNT = 10;
const int SSE_EVENT_DELAY_MILLISECONDS = 20;

app.MapGet("/api/events/stream", async (HttpContext http, CancellationToken cancellationToken) =>
{
    int count = int.TryParse(http.Request.Query["count"], out int parsed) && parsed > 0
        ? parsed
        : SSE_DEFAULT_EVENT_COUNT;

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    for (int i = 0; i < count; i++)
    {
        await http.Response.WriteAsync($"data: {i}\n\n", cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
        await Task.Delay(SSE_EVENT_DELAY_MILLISECONDS, cancellationToken);
    }
});

app.MapPost("/graphql", async (HttpContext http, CancellationToken cancellationToken) =>
{
    using JsonDocument request = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
    string query = request.RootElement.GetProperty("query").GetString() ?? string.Empty;

    ExecutionResult result = await graphQlExecuter.ExecuteAsync(executionOptions =>
    {
        executionOptions.Schema = graphQlSchema;
        executionOptions.Query = query;
        executionOptions.CancellationToken = cancellationToken;
    });

    http.Response.ContentType = "application/json";
    await http.Response.WriteAsync(graphQlSerializer.Serialize(result), cancellationToken);
});

await app.RunAsync();

static int ExtractPort(string urls)
{
    string first = urls.Split(';')[0];
    int colonIndex = first.LastIndexOf(':');
    return int.Parse(first[(colonIndex + 1)..].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture);
}

static async Task EchoLoopAsync(WebSocket socket, CancellationToken cancellationToken)
{
    const int BUFFER_SIZE = 8 * 1024;
    byte[] buffer = new byte[BUFFER_SIZE];

    while (socket.State == WebSocketState.Open)
    {
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken);
            return;
        }

        await socket.SendAsync(
            buffer.AsMemory(0, result.Count),
            result.MessageType,
            result.EndOfMessage,
            cancellationToken);
    }
}

static string? BearerTokenOf(HttpContext http)
{
    const string BEARER_PREFIX = "Bearer ";
    string header = http.Request.Headers.Authorization.ToString();

    return header.StartsWith(BEARER_PREFIX, StringComparison.Ordinal)
        ? header[BEARER_PREFIX.Length..]
        : null;
}

static decimal PriceOf(Product[] catalog, int productId)
{
    foreach (Product product in catalog)
    {
        if (product.Id == productId)
        {
            return product.Price;
        }
    }

    return 0m;
}

static async Task SimulateLatencyAsync(SampleTargetOptions options, CancellationToken cancellationToken)
{
    int delayMilliseconds = Random.Shared.Next(options.MinLatencyMilliseconds, options.MaxLatencyMilliseconds + 1);
    if (delayMilliseconds > 0)
    {
        await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
    }
}

static Product[] BuildCatalog(SampleTargetOptions options)
{
    Faker faker = new() { Random = new Randomizer(options.RandomSeed) };
    Product[] products = new Product[options.ProductCatalogSize];

    for (int i = 0; i < products.Length; i++)
    {
        products[i] = new Product(i + 1, faker.Commerce.ProductName(), decimal.Parse(faker.Commerce.Price(), System.Globalization.CultureInfo.InvariantCulture));
    }

    return products;
}