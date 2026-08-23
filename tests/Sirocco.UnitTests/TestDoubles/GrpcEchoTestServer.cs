using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sirocco.UnitTests.TestDoubles;

/// <summary>
/// Vrai serveur Kestrel HTTP/2 en clair, sur un port loopback libre, hebergeant
/// <see cref="FakeEchoService"/>. Contrairement au WebSocket, il n'existe pas d'equivalent
/// "socket brut" pour gRPC (le framing et la negociation HTTP/2 imposent la pile ASP.NET Core
/// complete) : un vrai <see cref="WebApplication"/>, pas un simulacre, est le double le plus
/// leger possible ici.
/// </summary>
internal sealed class GrpcEchoTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private GrpcEchoTestServer(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    /// <summary>Adresse <c>http://</c> a laquelle se connecter (HTTP/2 en clair uniquement).</summary>
    public Uri Endpoint { get; }

    public static async Task<GrpcEchoTestServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(static serverOptions =>
            serverOptions.Listen(IPAddress.Loopback, 0, static listenOptions => listenOptions.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();

        WebApplication app = builder.Build();
        app.MapGrpcService<FakeEchoService>();

        await app.StartAsync().ConfigureAwait(false);

        string address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        return new GrpcEchoTestServer(app, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}