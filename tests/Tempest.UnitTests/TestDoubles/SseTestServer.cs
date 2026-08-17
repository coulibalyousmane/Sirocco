using System.Net;
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
/// Vrai serveur Kestrel HTTP/1.1 en clair, sur un port loopback libre, servant un flux
/// <c>text/event-stream</c> — meme gabarit que <see cref="GrpcEchoTestServer"/>. Le nombre
/// d'evenements et le delai entre deux evenements se pilotent par la requete elle-meme
/// (<c>?count=</c>), pas par la configuration du serveur, pour rester reutilisable d'un test a
/// l'autre sans redemarrer de serveur.
/// </summary>
internal sealed class SseTestServer : IAsyncDisposable
{
    private const int DEFAULT_EVENT_COUNT = 5;

    private readonly WebApplication _app;

    private SseTestServer(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    /// <summary>Adresse de base a laquelle se connecter.</summary>
    public Uri Endpoint { get; }

    public static async Task<SseTestServer> StartAsync(int eventDelayMilliseconds = 0)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(static serverOptions =>
            serverOptions.Listen(IPAddress.Loopback, 0, static listenOptions => listenOptions.Protocols = HttpProtocols.Http1));

        WebApplication app = builder.Build();

        app.MapGet("/events", async (HttpContext http, CancellationToken cancellationToken) =>
        {
            int count = int.TryParse(http.Request.Query["count"], out int parsed) && parsed > 0
                ? parsed
                : DEFAULT_EVENT_COUNT;

            http.Response.ContentType = "text/event-stream";

            for (int i = 0; i < count; i++)
            {
                await http.Response.WriteAsync($"data: {i}\n\n", cancellationToken);
                await http.Response.Body.FlushAsync(cancellationToken);
                if (eventDelayMilliseconds > 0)
                {
                    await Task.Delay(eventDelayMilliseconds, cancellationToken);
                }
            }
        });

        app.MapGet("/wrong-content-type", () => Results.Ok(new { message = "not an event stream" }));

        app.MapGet("/events-fixed-count", async (HttpContext http, CancellationToken cancellationToken) =>
        {
            // Ignore volontairement "?count=" : sert a reproduire un decompte d'evenements qui ne
            // correspond pas a ce qu'attend le workflow, sans dependre d'un alea reseau.
            http.Response.ContentType = "text/event-stream";
            for (int i = 0; i < DEFAULT_EVENT_COUNT; i++)
            {
                await http.Response.WriteAsync($"data: {i}\n\n", cancellationToken);
                await http.Response.Body.FlushAsync(cancellationToken);
            }
        });

        app.MapGet("/never-ends", async (HttpContext http, CancellationToken cancellationToken) =>
        {
            http.Response.ContentType = "text/event-stream";
            await http.Response.WriteAsync("data: 0\n\n", cancellationToken);
            await http.Response.Body.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        });

        await app.StartAsync().ConfigureAwait(false);

        string address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        return new SseTestServer(app, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}