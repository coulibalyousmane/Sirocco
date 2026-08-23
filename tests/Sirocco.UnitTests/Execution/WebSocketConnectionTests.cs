using System.Net.WebSockets;
using System.Text;
using Sirocco.Domain.Execution;
using Sirocco.UnitTests.TestDoubles;

namespace Sirocco.UnitTests.Execution;

/// <summary>
/// Verifie le wrapper contre un vrai socket loopback : envoi/reception, reassemblage des
/// messages fragmentes, et surtout la poignee de main de fermeture dans les deux sens —
/// un mauvais appariement client/serveur sur ce point bloque indefiniment (constate en
/// pratique lors de la conception de cette fonctionnalite).
/// </summary>
public sealed class WebSocketConnectionTests
{
    /// <summary>
    /// Garde-fou contre un blocage indefini (l'appariement client/serveur de la fermeture, que
    /// cette classe verifie, bloque pour toujours quand il est faux), <b>pas</b> une assertion de
    /// latence. Volontairement genereux : a 5 s, c'etait en pratique une borne de performance
    /// deguisee, que la contention CPU de la suite en parallele faisait sauter sur la poignee de
    /// main WebSocket — un faux echec rouge en CI pour une machine chargee, jamais pour un bug.
    /// </summary>
    private static readonly TimeSpan _guardTimeout = TimeSpan.FromSeconds(30);

    private static async Task EchoOnceThenCloseAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8 * 1024];
        WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, cancellationToken)
            .ConfigureAwait(false);

        WebSocketReceiveResult closing = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (closing.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task FragmentedReplyThenCloseAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8 * 1024];
        await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

        await socket.SendAsync(Encoding.UTF8.GetBytes("abc"), WebSocketMessageType.Text, endOfMessage: false, cancellationToken)
            .ConfigureAwait(false);
        await socket.SendAsync(Encoding.UTF8.GetBytes("def"), WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);

        WebSocketReceiveResult closing = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (closing.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task CloseImmediatelyAsync(WebSocket socket, CancellationToken cancellationToken) =>
        socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancellationToken);

    private static async Task<WebSocketConnection> ConnectAsync(LoopbackWebSocketServer server, CancellationToken cancellationToken)
    {
        ClientWebSocket socket = new();
        await socket.ConnectAsync(server.Endpoint, cancellationToken).ConfigureAwait(false);
        return new WebSocketConnection(socket);
    }

    [Fact]
    public async Task Send_and_receive_round_trips_a_text_message()
    {
        await using LoopbackWebSocketServer server = new(EchoOnceThenCloseAsync);
        using CancellationTokenSource cts = new(_guardTimeout);

        await using WebSocketConnection connection = await ConnectAsync(server, cts.Token);

        await connection.SendTextAsync("hello", cts.Token);
        WebSocketMessage reply = await connection.ReceiveAsync(cts.Token);

        Assert.Equal(WebSocketMessageType.Text, reply.Type);
        Assert.Equal("hello", reply.Text);
        Assert.Equal(5, reply.ByteCount);

        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cts.Token);
    }

    [Fact]
    public async Task A_reply_fragmented_across_frames_is_reassembled()
    {
        await using LoopbackWebSocketServer server = new(FragmentedReplyThenCloseAsync);
        using CancellationTokenSource cts = new(_guardTimeout);

        await using WebSocketConnection connection = await ConnectAsync(server, cts.Token);

        await connection.SendTextAsync("trigger", cts.Token);
        WebSocketMessage reply = await connection.ReceiveAsync(cts.Token);

        Assert.Equal(WebSocketMessageType.Text, reply.Type);
        Assert.Equal("abcdef", reply.Text);
        Assert.Equal(6, reply.ByteCount);

        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cts.Token);
    }

    [Fact]
    public async Task A_close_initiated_by_the_peer_is_surfaced_as_a_close_message()
    {
        await using LoopbackWebSocketServer server = new(CloseImmediatelyAsync);
        using CancellationTokenSource cts = new(_guardTimeout);

        await using WebSocketConnection connection = await ConnectAsync(server, cts.Token);

        WebSocketMessage message = await connection.ReceiveAsync(cts.Token);

        Assert.Equal(WebSocketMessageType.Close, message.Type);
        Assert.Equal(WebSocketState.CloseReceived, connection.State);
    }

    [Fact]
    public async Task Closing_the_connection_completes_the_handshake_without_hanging()
    {
        await using LoopbackWebSocketServer server = new(EchoOnceThenCloseAsync);
        using CancellationTokenSource cts = new(_guardTimeout);

        await using WebSocketConnection connection = await ConnectAsync(server, cts.Token);
        await connection.SendTextAsync("hello", cts.Token);
        await connection.ReceiveAsync(cts.Token);

        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cts.Token);

        Assert.Equal(WebSocketState.Closed, connection.State);
    }
}