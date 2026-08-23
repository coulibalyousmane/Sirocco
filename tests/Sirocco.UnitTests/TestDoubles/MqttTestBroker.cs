using System.Net;
using System.Net.Sockets;
using MQTTnet.Server;

namespace Sirocco.UnitTests.TestDoubles;

/// <summary>
/// Vrai courtier MQTT (<c>MQTTnet.Server</c>) sur un port loopback libre — meme esprit que
/// <see cref="SseTestServer"/>/<see cref="GrpcEchoTestServer"/> : un processus .NET ordinaire,
/// aucune infrastructure externe a installer pour tester <c>MqttWorkflow</c> contre un vrai
/// aller-retour publication/abonnement.
/// <para>
/// Contrairement a Kestrel, <c>MqttServer</c> n'expose pas le port reellement lie quand on demande
/// le port 0 (pas d'equivalent d'<c>IServerAddressesFeature</c>) : le port libre est donc trouve a
/// l'avance via un <see cref="TcpListener"/> jetable, puis redonne au serveur MQTT — une legere
/// fenetre de course avec un autre processus est acceptee, comme pour tout choix de port libre
/// hors resolution native du systeme d'exploitation.
/// </para>
/// </summary>
internal sealed class MqttTestBroker : IAsyncDisposable
{
    private readonly MqttServer _server;

    private MqttTestBroker(MqttServer server, int port)
    {
        _server = server;
        Port = port;
    }

    /// <summary>Port loopback sur lequel le courtier ecoute.</summary>
    public int Port { get; }

    public static async Task<MqttTestBroker> StartAsync()
    {
        int port = GetFreeTcpPort();

        MqttServer server = new MqttServerFactory().CreateMqttServer(
            new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build());

        await server.StartAsync().ConfigureAwait(false);

        return new MqttTestBroker(server, port);
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync().ConfigureAwait(false);
        _server.Dispose();
    }
}