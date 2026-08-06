using Grpc.Core;
using Grpc.Net.Client;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;
using Tempest.Protos.Echo;

namespace Tempest.Scenarios;

/// <summary>
/// Scenario de reference pour gRPC bidirectionnel : un flux ouvert une seule fois pour toute
/// l'iteration, sur lequel client et serveur echangent en ping-pong — ecrire un message, attendre
/// son echo, mesurer, recommencer — plutot qu'en pipeline (ecrire plusieurs messages d'avance
/// sans attendre leurs echos).
/// <para>
/// Ce n'est pas une simplification arbitraire : <see cref="IVirtualUserContext"/> et
/// <see cref="StepScope"/> sont documentes comme n'etant touches que par leur propre
/// travailleur, sans aucune synchronisation (c'est ce qui permet au chemin de mesure de
/// n'allouer et ne verrouiller rien). Un pipeline exigerait une tache d'ecriture et une tache
/// de lecture tournant en parallele au sein d'une meme iteration, toutes deux ouvrant/cloturant
/// des <see cref="StepScope"/> sur le meme contexte — cela violerait cette invariante et
/// forcerait une synchronisation qui couterait a <b>tous</b> les scenarios, pas seulement
/// celui-ci. Le ping-pong reste du vrai bidirectionnel au niveau du protocole (un seul flux,
/// deux sens, reutilise pour toute l'iteration) : c'est seulement l'usage qu'en fait ce
/// scenario qui reste sequentiel.
/// </para>
/// </summary>
/// <param name="options">Reglages ; partages avec <see cref="GrpcEchoWorkflow"/>, memes besoins.</param>
public sealed class GrpcBidiStreamEchoWorkflow(GrpcEchoWorkflowOptions? options = null) : IWorkflow
{
    /// <summary>Nom du scenario, utilise dans les rapports.</summary>
    public const string WORKFLOW_NAME = "grpc-bidi-stream-echo";

    private readonly GrpcEchoWorkflowOptions _options = options ?? new GrpcEchoWorkflowOptions();

    private StepId _messageStep;
    private GrpcChannel? _channel;

    /// <inheritdoc />
    public string Name => WORKFLOW_NAME;

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _messageStep = registry.Register(GrpcBidiStreamEchoSteps.MESSAGE);
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        EchoService.EchoServiceClient client = new(GetOrCreateChannel(_options.TargetUri ?? context.HttpClient.BaseAddress));
        using AsyncDuplexStreamingCall<BidiStreamMessage, BidiStreamMessage> call =
            client.BidiStreamEcho(cancellationToken: cancellationToken);
        string payload = $"bidi-{context.VirtualUserId}-{context.IterationNumber}";

        for (int sequence = 0; sequence < _options.MessageCount; sequence++)
        {
            StepScope scope = context.BeginStep(_messageStep);

            try
            {
                await call.RequestStream.WriteAsync(
                    new BidiStreamMessage { Message = payload, Sequence = sequence },
                    cancellationToken).ConfigureAwait(false);

                if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    // Le flux s'est ferme plus tot que prevu : le serveur n'a pas echo ce
                    // message, ce n'est pas une fin normale (celle-ci n'arrive qu'apres la
                    // derniere iteration, cote CompleteAsync ci-dessous).
                    scope.Fail(RequestOutcome.ConnectionError);
                    return;
                }

                BidiStreamMessage echo = call.ResponseStream.Current;
                if (echo.Message != payload || echo.Sequence != sequence)
                {
                    scope.Fail(RequestOutcome.AssertionFailed);
                    return;
                }

                scope.Success(bytesReceived: echo.CalculateSize());
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
            {
                scope.Fail(RequestOutcome.Timeout, (int)ex.StatusCode);
                return;
            }
            catch (RpcException ex)
            {
                scope.Fail(RequestOutcome.ConnectionError, (int)ex.StatusCode);
                return;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                scope.Fail(RequestOutcome.Timeout);
                return;
            }
        }

        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
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