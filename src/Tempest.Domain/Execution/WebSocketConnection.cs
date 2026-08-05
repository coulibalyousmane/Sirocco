using System.Net.WebSockets;
using System.Text;

namespace Tempest.Domain.Execution;

/// <summary>
/// Connexion WebSocket ouverte par un utilisateur virtuel.
/// <para>
/// Contrairement a <see cref="IVirtualUserContext.HttpClient"/>, une <see cref="ClientWebSocket"/>
/// ne se mutualise pas entre iterations ou utilisateurs virtuels : chaque appel a
/// <see cref="IVirtualUserContext.ConnectWebSocketAsync"/> en cree une nouvelle. C'est au
/// scenario de la conserver dans <see cref="IVirtualUserContext.State"/> s'il veut la garder
/// ouverte au-dela d'une seule etape.
/// </para>
/// </summary>
public sealed class WebSocketConnection : IAsyncDisposable
{
    private const int RECEIVE_BUFFER_SIZE = 8 * 1024;

    private readonly ClientWebSocket _socket;

    /// <summary>Cree le wrapper autour d'une socket deja connectee.</summary>
    /// <param name="socket">Socket ouverte par <see cref="IVirtualUserContext.ConnectWebSocketAsync"/>.</param>
    public WebSocketConnection(ClientWebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
    }

    /// <summary>Etat courant de la connexion.</summary>
    public WebSocketState State => _socket.State;

    /// <summary>Envoie un message texte complet en une seule trame.</summary>
    public Task SendTextAsync(string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] payload = Encoding.UTF8.GetBytes(message);
        return _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <summary>
    /// Recoit le prochain message, en reassemblant les trames si le pair l'a fragmente.
    /// </summary>
    public async Task<WebSocketMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[RECEIVE_BUFFER_SIZE];
        using MemoryStream accumulated = new();

        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            accumulated.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return result.MessageType == WebSocketMessageType.Text
            ? new WebSocketMessage(result.MessageType, Encoding.UTF8.GetString(accumulated.ToArray()), (int)accumulated.Length)
            : new WebSocketMessage(result.MessageType, Text: null, (int)accumulated.Length);
    }

    /// <summary>
    /// Ferme proprement la connexion : poignee de main complete, dans un sens ou dans
    /// l'autre selon qui l'a initiee.
    /// </summary>
    public Task CloseAsync(WebSocketCloseStatus status, string? statusDescription, CancellationToken cancellationToken) =>
        _socket.CloseAsync(status, statusDescription, cancellationToken);

    /// <summary>
    /// Libere la socket. N'effectue <b>aucune</b> poignee de main de fermeture implicite :
    /// un scenario qui omet <see cref="CloseAsync"/> abandonne la connexion abruptement,
    /// ce qui reste un comportement client realiste a mesurer, pas une erreur a masquer.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}