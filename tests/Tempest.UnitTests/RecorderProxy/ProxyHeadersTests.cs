using Tempest.RecorderProxy;

namespace Tempest.UnitTests.RecorderProxy;

public sealed class ProxyHeadersTests
{
    [Theory]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("Content-Type")]
    [InlineData("Connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Upgrade")]
    [InlineData("Proxy-Connection")]
    [InlineData("Proxy-Authenticate")]
    [InlineData("Proxy-Authorization")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    public void Hop_by_hop_headers_are_not_forwarded(string headerName)
    {
        Assert.False(ProxyHeaders.ShouldForward(headerName));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Accept")]
    [InlineData("User-Agent")]
    [InlineData("X-Custom-Header")]
    [InlineData("Cookie")]
    public void Ordinary_headers_are_forwarded(string headerName)
    {
        Assert.True(ProxyHeaders.ShouldForward(headerName));
    }

    [Fact]
    public void Header_name_matching_is_case_insensitive()
    {
        Assert.False(ProxyHeaders.ShouldForward("content-type"));
        Assert.False(ProxyHeaders.ShouldForward("HOST"));
    }
}