using System.Net.WebSockets;
using Bogus;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Tempest.SampleTarget;
using Tempest.SampleTarget.Contracts;
using Tempest.SampleTarget.Services;

// Cible de demonstration pour un premier tir reel : pas de logique metier, juste assez de
// comportement realiste (latence, capacite finie, jetons qui expirent) pour que Tempest ait
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

app.Run();

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