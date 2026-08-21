using System.Net;
using System.Text.Json;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.SystemTextJson;
using GraphQL.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tempest.UnitTests.TestDoubles;

/// <summary>
/// Vrai serveur Kestrel HTTP/1.1 en clair, sur un port loopback libre, servant un schema GraphQL
/// minimal (moteur GraphQL.NET reel, pas une simulation par correspondance de chaine) — meme
/// gabarit que <see cref="SseTestServer"/>. Un seul produit connu (id 1) : suffisant pour exercer
/// la requete de lecture, la mutation reussie et la mutation en erreur (identifiant inconnu), sans
/// dupliquer le catalogue complet de <c>Tempest.SampleTarget</c>.
/// </summary>
internal sealed class GraphQlTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private GraphQlTestServer(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    /// <summary>Adresse de base a laquelle se connecter.</summary>
    public Uri Endpoint { get; }

    /// <param name="validProductId">
    /// Seul identifiant de produit connu du catalogue de ce double. Configurable pour pouvoir
    /// forcer deterministement un identifiant hors catalogue dans un test (un identifiant tire
    /// aleatoirement dans <c>[1, N]</c> ne peut pas etre garanti absent autrement que par la
    /// valeur elle-meme du catalogue).
    /// </param>
    public static async Task<GraphQlTestServer> StartAsync(int validProductId = 1)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(static serverOptions =>
            serverOptions.Listen(IPAddress.Loopback, 0, static listenOptions => listenOptions.Protocols = HttpProtocols.Http1));

        WebApplication app = builder.Build();

        ISchema schema = BuildSchema(validProductId);
        IDocumentExecuter executer = new DocumentExecuter();
        GraphQLSerializer serializer = new();

        app.MapPost("/graphql", async (HttpContext http, CancellationToken cancellationToken) =>
        {
            using JsonDocument request = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
            string query = request.RootElement.GetProperty("query").GetString() ?? string.Empty;

            ExecutionResult result = await executer.ExecuteAsync(options =>
            {
                options.Schema = schema;
                options.Query = query;
                options.CancellationToken = cancellationToken;
            });

            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(serializer.Serialize(result), cancellationToken);
        });

        await app.StartAsync().ConfigureAwait(false);

        string address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        return new GraphQlTestServer(app, new Uri(address));
    }

    private static Schema BuildSchema(int validProductId)
    {
        TestProduct[] catalog = [new TestProduct(validProductId, "Widget", 9.99m)];

        ObjectGraphType productType = new() { Name = "Product" };
        productType.Field<IntGraphType>("id").Resolve(context => ((TestProduct)context.Source!).Id);
        productType.Field<StringGraphType>("name").Resolve(context => ((TestProduct)context.Source!).Name);
        productType.Field<FloatGraphType>("price").Resolve(context => ((TestProduct)context.Source!).Price);

        ObjectGraphType orderType = new() { Name = "Order" };
        orderType.Field<StringGraphType>("id").Resolve(context => ((TestOrder)context.Source!).Id);
        orderType.Field<FloatGraphType>("total").Resolve(context => ((TestOrder)context.Source!).Total);

        ObjectGraphType query = new() { Name = "Query" };
        query.AddField(new FieldType
        {
            Name = "products",
            ResolvedType = new ListGraphType(productType),
            Resolver = new FuncFieldResolver<object>(_ => catalog),
        });

        ObjectGraphType mutation = new() { Name = "Mutation" };
        mutation.AddField(new FieldType
        {
            Name = "placeOrder",
            ResolvedType = orderType,
            Arguments = new QueryArguments(
                new QueryArgument<NonNullGraphType<IntGraphType>> { Name = "productId" },
                new QueryArgument<NonNullGraphType<IntGraphType>> { Name = "quantity" }),
            Resolver = new FuncFieldResolver<object?>(context =>
            {
                int productId = context.GetArgument<int>("productId");
                int quantity = context.GetArgument<int>("quantity");
                TestProduct? product = Array.Find(catalog, candidate => candidate.Id == productId);
                if (product is null)
                {
                    context.Errors.Add(new ExecutionError($"Produit introuvable : {productId}"));
                    return null;
                }

                return new TestOrder(Guid.NewGuid().ToString("N"), product.Price * quantity);
            }),
        });

        return new Schema { Query = query, Mutation = mutation };
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record TestProduct(int Id, string Name, decimal Price);

    private sealed record TestOrder(string Id, decimal Total);
}