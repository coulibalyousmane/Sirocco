using Grpc.Core;
using Tempest.Protos.Echo;

namespace Tempest.UnitTests.TestDoubles;

/// <summary>Service gRPC de test : echo pur, sans latence simulee.</summary>
internal sealed class FakeEchoService : EchoService.EchoServiceBase
{
    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context) =>
        Task.FromResult(new PingResponse { Message = request.Message });
}