using Grpc.Core;
using Tempest.Protos.Echo;

namespace Tempest.SampleTarget.Services;

/// <summary>Service gRPC de demonstration (unaire et les trois formes de streaming) : echo pur, sans logique metier.</summary>
internal sealed class EchoGrpcService(SampleTargetOptions options) : EchoService.EchoServiceBase
{
    /// <inheritdoc />
    public override async Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
    {
        await SimulateLatencyAsync(options, context.CancellationToken);
        return new PingResponse { Message = request.Message };
    }

    /// <inheritdoc />
    public override async Task StreamEcho(
        StreamEchoRequest request,
        IServerStreamWriter<StreamEchoMessage> responseStream,
        ServerCallContext context)
    {
        for (int sequence = 0; sequence < options.StreamMessageCount; sequence++)
        {
            await SimulateLatencyAsync(options, context.CancellationToken);
            await responseStream.WriteAsync(new StreamEchoMessage { Message = request.Message, Sequence = sequence });
        }
    }

    /// <inheritdoc />
    public override async Task<ClientStreamSummary> ClientStreamEcho(
        IAsyncStreamReader<ClientStreamMessage> requestStream,
        ServerCallContext context)
    {
        int messageCount = 0;
        int totalBytes = 0;

        while (await requestStream.MoveNext(context.CancellationToken))
        {
            await SimulateLatencyAsync(options, context.CancellationToken);
            messageCount++;
            totalBytes += requestStream.Current.CalculateSize();
        }

        return new ClientStreamSummary { MessageCount = messageCount, TotalBytes = totalBytes };
    }

    /// <inheritdoc />
    public override async Task BidiStreamEcho(
        IAsyncStreamReader<BidiStreamMessage> requestStream,
        IServerStreamWriter<BidiStreamMessage> responseStream,
        ServerCallContext context)
    {
        while (await requestStream.MoveNext(context.CancellationToken))
        {
            await SimulateLatencyAsync(options, context.CancellationToken);
            await responseStream.WriteAsync(requestStream.Current);
        }
    }

    private static async Task SimulateLatencyAsync(SampleTargetOptions options, CancellationToken cancellationToken)
    {
        int delayMilliseconds = Random.Shared.Next(options.MinLatencyMilliseconds, options.MaxLatencyMilliseconds + 1);
        if (delayMilliseconds > 0)
        {
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }
}