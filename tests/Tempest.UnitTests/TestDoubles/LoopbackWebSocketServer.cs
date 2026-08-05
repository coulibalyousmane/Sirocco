using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Tempest.UnitTests.TestDoubles;

/// <summary>
/// Serveur WebSocket minimal pour les tests, base sur <see cref="HttpListener"/> plutot que
/// sur un hote ASP.NET Core complet : une <see cref="ClientWebSocket"/> n'a pas d'equivalent
/// du <c>HttpMessageHandler</c> injectable d'<see cref="HttpClient"/>, un vrai socket loopback
/// est donc necessaire pour exercer <see cref="Tempest.Domain.Execution.WebSocketConnection"/>.
/// </summary>
internal sealed class LoopbackWebSocketServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    /// <summary>Demarre le serveur sur un port loopback libre.</summary>
    /// <param name="handleConnection">Traite une connexion acceptee jusqu'a sa fermeture.</param>
    public LoopbackWebSocketServer(Func<WebSocket, CancellationToken, Task> handleConnection)
    {
        int port = GetFreePort();
        Endpoint = new Uri($"ws://127.0.0.1:{port}/");

        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        _acceptLoop = AcceptLoopAsync(handleConnection, _cts.Token);
    }

    /// <summary>Adresse <c>ws://</c> a laquelle se connecter.</summary>
    public Uri Endpoint { get; }

    private async Task AcceptLoopAsync(Func<WebSocket, CancellationToken, Task> handleConnection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
                await handleConnection(wsContext.WebSocket, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Arret normal via DisposeAsync.
        }
        catch (HttpListenerException)
        {
            // Le listener a ete ferme pendant un GetContextAsync en cours.
        }
        catch (ObjectDisposedException)
        {
            // Idem, course possible entre Close() et la boucle d'acceptation.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Close();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
            // La boucle d'acceptation gere deja ses propres exceptions d'arret ; ce qui
            // remonterait ici serait inattendu, mais ne doit pas empecher le nettoyage.
        }

        _cts.Dispose();
    }

    private static int GetFreePort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}