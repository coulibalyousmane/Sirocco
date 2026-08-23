using System.Threading;
using Grpc.Core;
using Grpc.Net.Client;
using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Protos.Echo;

namespace Sirocco.Scenarios;

/// <summary>
/// Scenario de reference pour gRPC unaire : un aller-retour <c>Ping</c>, sans connexion
/// explicite a mesurer separement — contrairement a WebSocket, l'etablissement de la
/// connexion HTTP/2 est transparent et mutualise par le canal, exactement comme le pool de
/// <see cref="HttpClient"/>.
/// </summary>
/// <param name="options">Reglages ; valeurs par defaut si omis.</param>
public sealed class GrpcEchoWorkflow(GrpcEchoWorkflowOptions? options = null) : IWorkflow
{
    /// <summary>Nom du scenario, utilise dans les rapports.</summary>
    public const string WORKFLOW_NAME = "grpc-echo";

    private readonly GrpcEchoWorkflowOptions _options = options ?? new GrpcEchoWorkflowOptions();

    private StepId _pingStep;
    private GrpcChannel? _channel;

    /// <inheritdoc />
    public string Name => WORKFLOW_NAME;

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _pingStep = registry.Register(GrpcEchoSteps.PING);
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_pingStep);
        string payload = $"ping-{context.VirtualUserId}-{context.IterationNumber}";

        try
        {
            EchoService.EchoServiceClient client = new(GetOrCreateChannel(_options.TargetUri ?? context.HttpClient.BaseAddress));
            PingResponse response = await client
                .PingAsync(new PingRequest { Message = payload }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (response.Message != payload)
            {
                // Reponse recue mais pas celle attendue : un echec d'assertion, pas de transport.
                scope.Fail(RequestOutcome.AssertionFailed);
                return;
            }

            scope.Success(bytesReceived: response.CalculateSize());
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            scope.Fail(RequestOutcome.Timeout, (int)ex.StatusCode);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.Unauthenticated)
        {
            scope.Fail(RequestOutcome.ConnectionError, (int)ex.StatusCode);
        }
        catch (RpcException ex)
        {
            scope.Fail(RequestOutcome.HttpError, (int)ex.StatusCode);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
        }
    }

    /// <inheritdoc />
    public async ValueTask TearDownAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            await _channel.ShutdownAsync().ConfigureAwait(false);
            _channel.Dispose();
        }
    }

    /// <summary>
    /// Construit le canal au premier appel puis le reutilise pour tout le tir : comme
    /// <see cref="HttpClient"/>, un <see cref="GrpcChannel"/> multiplexe ses appels sur une
    /// meme connexion HTTP/2 et n'a donc rien a gagner a etre recree par iteration.
    /// </summary>
    private GrpcChannel GetOrCreateChannel(Uri? targetUri)
    {
        if (_channel is not null)
        {
            return _channel;
        }

        if (targetUri is null)
        {
            throw new InvalidOperationException(
                "Aucune adresse gRPC disponible : renseignez GrpcEchoWorkflowOptions.TargetUri, ou donnez " +
                "une BaseAddress au client HTTP.");
        }

        // gRPC en clair (http://, sans TLS) exige ce commutateur explicite : sans lui,
        // SocketsHttpHandler refuse de negocier HTTP/2 en texte clair (h2c).
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        return LazyInitializer.EnsureInitialized(ref _channel, () => GrpcChannel.ForAddress(targetUri))!;
    }
}