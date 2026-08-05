using System.Net.WebSockets;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;

namespace Tempest.Scenarios;

/// <summary>
/// Scenario de reference pour le protocole WebSocket : ouvre une connexion, echange un
/// message texte avec la cible, puis ferme proprement — de quoi valider de bout en bout
/// <see cref="IVirtualUserContext.ConnectWebSocketAsync"/> sans logique metier.
/// </summary>
public sealed class WebSocketEchoWorkflow : IWorkflow
{
    /// <summary>Nom du scenario, utilise dans les rapports.</summary>
    public const string WORKFLOW_NAME = "websocket-echo";

    private readonly WebSocketEchoWorkflowOptions _options;

    private StepId _connectStep;
    private StepId _echoStep;

    /// <summary>Cree le scenario.</summary>
    /// <param name="options">Reglages ; valeurs par defaut si omis.</param>
    public WebSocketEchoWorkflow(WebSocketEchoWorkflowOptions? options = null)
    {
        _options = options ?? new WebSocketEchoWorkflowOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public string Name => WORKFLOW_NAME;

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _connectStep = registry.Register(WebSocketEchoSteps.CONNECT);
        _echoStep = registry.Register(WebSocketEchoSteps.ECHO);
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        Uri endpoint = BuildEndpoint(context.HttpClient.BaseAddress);

        WebSocketConnection? connection = await ConnectAsync(context, endpoint, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return;
        }

        await using (connection.ConfigureAwait(false))
        {
            await EchoAsync(context, connection, cancellationToken).ConfigureAwait(false);
            await CloseGracefullyAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<WebSocketConnection?> ConnectAsync(
        IVirtualUserContext context,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_connectStep);

        try
        {
            WebSocketConnection connection = await context
                .ConnectWebSocketAsync(endpoint, configureOptions: null, cancellationToken)
                .ConfigureAwait(false);
            scope.Success();
            return connection;
        }
        catch (WebSocketException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
            return null;
        }
    }

    private async Task EchoAsync(IVirtualUserContext context, WebSocketConnection connection, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_echoStep);
        string payload = $"ping-{context.VirtualUserId}-{context.IterationNumber}";

        try
        {
            await connection.SendTextAsync(payload, cancellationToken).ConfigureAwait(false);
            WebSocketMessage reply = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            if (reply.Type != WebSocketMessageType.Text || reply.Text != payload)
            {
                // Reponse recue mais pas celle attendue : un echec d'assertion, pas de transport.
                scope.Fail(RequestOutcome.AssertionFailed);
                return;
            }

            scope.Success(bytesReceived: reply.ByteCount);
        }
        catch (WebSocketException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
        }
    }

    private static async Task CloseGracefullyAsync(WebSocketConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // Best effort : une fermeture ratee n'est pas une erreur de scenario, la
            // socket sera de toute facon liberee par le Dispose qui suit.
        }
    }

    private Uri BuildEndpoint(Uri? httpBaseAddress)
    {
        if (httpBaseAddress is null)
        {
            throw new InvalidOperationException(
                "Le client HTTP doit avoir une BaseAddress pour en deriver l'URI WebSocket.");
        }

        UriBuilder builder = new(httpBaseAddress)
        {
            Scheme = httpBaseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = _options.EchoPath,
        };

        return builder.Uri;
    }
}