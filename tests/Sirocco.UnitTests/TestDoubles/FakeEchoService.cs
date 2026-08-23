using Grpc.Core;
using Sirocco.Protos.Echo;

namespace Sirocco.UnitTests.TestDoubles;

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

    public override async Task<ClientStreamSummary> ClientStreamEcho(
        IAsyncStreamReader<ClientStreamMessage> requestStream,
        ServerCallContext context)
    {
        int messageCount = 0;
        int totalBytes = 0;

        while (await requestStream.MoveNext())
        {
            messageCount++;
            totalBytes += requestStream.Current.CalculateSize();
        }

        return new ClientStreamSummary { MessageCount = messageCount, TotalBytes = totalBytes };
    }

    public override async Task BidiStreamEcho(
        IAsyncStreamReader<BidiStreamMessage> requestStream,
        IServerStreamWriter<BidiStreamMessage> responseStream,
        ServerCallContext context)
    {
        while (await requestStream.MoveNext())
        {
            await responseStream.WriteAsync(requestStream.Current);
        }
    }
}