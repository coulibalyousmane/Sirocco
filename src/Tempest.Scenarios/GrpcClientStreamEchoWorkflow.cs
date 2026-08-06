using Grpc.Core;
using Grpc.Net.Client;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;
using Tempest.Protos.Echo;

namespace Tempest.Scenarios;

/// <summary>
/// Scenario de reference pour gRPC en streaming client : un flux de messages dont le nombre
/// est decide par le client (<see cref="GrpcEchoWorkflowOptions.MessageCount"/>), puis une
/// unique reponse recapitulative une fois le flux montant ferme.
/// <para>
/// Une seule etape mesure l'appel entier, contrairement a <see cref="GrpcStreamEchoWorkflow"/>
/// qui en mesure une par message recu : cote client, un <c>WriteAsync</c> sur le flux montant
/// n'attend pas de reponse individuelle (il retourne des que le message est mis en tampon), donc
/// aucune latence par message n'existe a mesurer avant la reponse recapitulative finale — c'est
/// elle, et seulement elle, qui marque un evenement observable.
/// </para>
/// </summary>
/// <param name="options">Reglages ; partages avec <see cref="GrpcEchoWorkflow"/>, memes besoins.</param>
public sealed class GrpcClientStreamEchoWorkflow(GrpcEchoWorkflowOptions? options = null) : IWorkflow
{
    /// <summary>Nom du scenario, utilise dans les rapports.</summary>
    public const string WORKFLOW_NAME = "grpc-client-stream-echo";

    private readonly GrpcEchoWorkflowOptions _options = options ?? new GrpcEchoWorkflowOptions();

    private StepId _uploadStep;
    private GrpcChannel? _channel;

    /// <inheritdoc />
    public string Name => WORKFLOW_NAME;

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _uploadStep = registry.Register(GrpcClientStreamEchoSteps.UPLOAD);
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_uploadStep);
        string payload = $"upload-{context.VirtualUserId}-{context.IterationNumber}";

        try
        {
            EchoService.EchoServiceClient client = new(GetOrCreateChannel(_options.TargetUri ?? context.HttpClient.BaseAddress));
            using AsyncClientStreamingCall<ClientStreamMessage, ClientStreamSummary> call =
                client.ClientStreamEcho(cancellationToken: cancellationToken);

            int expectedBytes = 0;
            for (int sequence = 0; sequence < _options.MessageCount; sequence++)
            {
                ClientStreamMessage message = new() { Message = payload, Sequence = sequence };
                await call.RequestStream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                expectedBytes += message.CalculateSize();
            }

            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            ClientStreamSummary summary = await call.ResponseAsync.ConfigureAwait(false);

            if (summary.MessageCount != _options.MessageCount || summary.TotalBytes != expectedBytes)
            {
                scope.Fail(RequestOutcome.AssertionFailed);
                return;
            }

            scope.Success(bytesReceived: summary.CalculateSize());
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            scope.Fail(RequestOutcome.Timeout, (int)ex.StatusCode);
        }
        catch (RpcException ex)
        {
            scope.Fail(RequestOutcome.ConnectionError, (int)ex.StatusCode);
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

    /// <summary>Construit le canal au premier appel puis le reutilise pour tout le tir.</summary>
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