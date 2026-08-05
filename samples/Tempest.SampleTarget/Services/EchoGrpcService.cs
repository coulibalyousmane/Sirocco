using Grpc.Core;
using Tempest.Protos.Echo;

namespace Tempest.SampleTarget.Services;

/// <summary>Service gRPC unaire de demonstration : echo pur, sans logique metier.</summary>
internal sealed class EchoGrpcService(SampleTargetOptions options) : EchoService.EchoServiceBase
{
    /// <inheritdoc />
    public override async Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
    {
        await SimulateLatencyAsync(options, context.CancellationToken);
        return new PingResponse { Message = request.Message };
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