using Grpc.Core;
using Tempest.Protos.Echo;

namespace Tempest.SampleTarget.Services;

/// <summary>Service gRPC de demonstration (unaire et streaming serveur) : echo pur, sans logique metier.</summary>
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

    private static async Task SimulateLatencyAsync(SampleTargetOptions options, CancellationToken cancellationToken)
    {
        int delayMilliseconds = Random.Shared.Next(options.MinLatencyMilliseconds, options.MaxLatencyMilliseconds + 1);
        if (delayMilliseconds > 0)
        {
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }
}