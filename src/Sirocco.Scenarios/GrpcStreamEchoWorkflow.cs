using System.Threading;
using Grpc.Core;
using Grpc.Net.Client;
using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Protos.Echo;

namespace Sirocco.Scenarios;

/// <summary>
/// Scenario de reference pour gRPC en streaming serveur : un appel, un flux de messages dont
/// le nombre est decide par le serveur — le client lit jusqu'a ce que le flux se termine, sans
/// en dicter la longueur.
/// <para>
/// Chaque message recu est mesure comme sa <b>propre</b> etape (<see cref="GrpcStreamEchoSteps.MESSAGE"/>),
/// via un <see cref="StepScope"/> frais ouvert juste avant d'attendre le message suivant : la
/// latence rapportee est celle de l'attente entre deux messages, pas celle de l'appel entier.
/// Pas d'etape "connexion" separee, pour la meme raison que <see cref="GrpcEchoWorkflow"/> :
/// l'etablissement HTTP/2 est transparent et mutualise par le canal.
/// </para>
/// <para>
/// Limite volontaire de cette version : streaming serveur seulement — streaming client et
/// bidirectionnel restent un chantier separe.
/// </para>
/// </summary>
/// <param name="options">Reglages ; partages avec <see cref="GrpcEchoWorkflow"/>, memes besoins.</param>
public sealed class GrpcStreamEchoWorkflow(GrpcEchoWorkflowOptions? options = null) : IWorkflow
{
    /// <summary>Nom du scenario, utilise dans les rapports.</summary>
    public const string WORKFLOW_NAME = "grpc-stream-echo";

    private readonly GrpcEchoWorkflowOptions _options = options ?? new GrpcEchoWorkflowOptions();

    private StepId _messageStep;
    private GrpcChannel? _channel;

    /// <inheritdoc />
    public string Name => WORKFLOW_NAME;

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _messageStep = registry.Register(GrpcStreamEchoSteps.MESSAGE);
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        EchoService.EchoServiceClient client = new(GetOrCreateChannel(_options.TargetUri ?? context.HttpClient.BaseAddress));
        string payload = $"stream-{context.VirtualUserId}-{context.IterationNumber}";

        using AsyncServerStreamingCall<StreamEchoMessage> call = client.StreamEcho(
            new StreamEchoRequest { Message = payload },
            cancellationToken: cancellationToken);

        int expectedSequence = 0;
        while (true)
        {
            StepScope scope = context.BeginStep(_messageStep);
            bool hasNext;

            try
            {
                hasNext = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
            {
                scope.Fail(RequestOutcome.Timeout, (int)ex.StatusCode);
                return;
            }
            catch (RpcException ex)
            {
                // Le flux s'est rompu en attendant le prochain message : le client attendait
                // une valeur qui n'est jamais arrivee, ce n'est pas une fin normale de flux.
                scope.Fail(RequestOutcome.ConnectionError, (int)ex.StatusCode);
                return;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                scope.Fail(RequestOutcome.Timeout);
                return;
            }

            if (!hasNext)
            {
                // Fin normale du flux : ce dernier appel n'etait pas un message, rien a publier.
                return;
            }

            StreamEchoMessage current = call.ResponseStream.Current;
            if (current.Message != payload || current.Sequence != expectedSequence)
            {
                scope.Fail(RequestOutcome.AssertionFailed);
                return;
            }

            scope.Success(bytesReceived: current.CalculateSize());
            expectedSequence++;
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