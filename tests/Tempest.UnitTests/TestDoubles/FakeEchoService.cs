using Grpc.Core;
using Tempest.Protos.Echo;

namespace Tempest.UnitTests.TestDoubles;

/// <summary>Service gRPC de test : echo pur, sans latence simulee.</summary>
internal sealed class FakeEchoService : EchoService.EchoServiceBase
{
    /// <summary>Nombre fixe de messages envoyes par <see cref="StreamEcho"/>, pour des tests deterministes.</summary>
    public const int STREAM_MESSAGE_COUNT = 3;

    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context) =>
        Task.FromResult(new PingResponse { Message = request.Message });

    public override async Task StreamEcho(
        StreamEchoRequest request,
        IServerStreamWriter<StreamEchoMessage> responseStream,
        ServerCallContext context)
    {
        for (int sequence = 0; sequence < STREAM_MESSAGE_COUNT; sequence++)
        {
            await responseStream.WriteAsync(new StreamEchoMessage { Message = request.Message, Sequence = sequence });
        }
    }
}