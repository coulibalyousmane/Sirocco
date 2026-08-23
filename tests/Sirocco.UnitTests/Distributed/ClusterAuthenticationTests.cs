using Sirocco.Host.Distributed;

namespace Sirocco.UnitTests.Distributed;

public sealed class ClusterAuthenticationTests
{
    [Fact]
    public void No_secret_configured_leaves_the_control_plane_open()
    {
        Assert.True(ClusterAuthentication.IsAuthorized(authorizationHeader: null, sharedSecret: null));
        Assert.True(ClusterAuthentication.IsAuthorized(authorizationHeader: "anything", sharedSecret: null));
        Assert.True(ClusterAuthentication.IsAuthorized(authorizationHeader: null, sharedSecret: ""));
    }

    [Fact]
    public void A_matching_bearer_token_is_authorized() =>
        Assert.True(ClusterAuthentication.IsAuthorized("Bearer top-secret", "top-secret"));

    [Fact]
    public void The_scheme_check_is_case_insensitive() =>
        Assert.True(ClusterAuthentication.IsAuthorized("bearer top-secret", "top-secret"));

    [Fact]
    public void A_missing_header_is_rejected_once_a_secret_is_configured() =>
        Assert.False(ClusterAuthentication.IsAuthorized(authorizationHeader: null, sharedSecret: "top-secret"));

    [Fact]
    public void A_wrong_token_is_rejected() =>
        Assert.False(ClusterAuthentication.IsAuthorized("Bearer wrong", "top-secret"));

    [Fact]
    public void A_token_of_different_length_is_rejected() =>
        Assert.False(ClusterAuthentication.IsAuthorized("Bearer top-secret-but-longer", "top-secret"));

    [Fact]
    public void An_unrecognized_scheme_is_rejected() =>
        Assert.False(ClusterAuthentication.IsAuthorized("Basic top-secret", "top-secret"));

    [Fact]
    public void A_malformed_header_is_rejected() =>
        Assert.False(ClusterAuthentication.IsAuthorized("not-a-valid-header-at-all !!", "top-secret"));

    [Fact]
    public void BuildHeader_returns_null_when_no_secret_is_configured()
    {
        Assert.Null(ClusterAuthentication.BuildHeader(null));
        Assert.Null(ClusterAuthentication.BuildHeader(""));
    }

    [Fact]
    public void BuildHeader_round_trips_with_IsAuthorized()
    {
        System.Net.Http.Headers.AuthenticationHeaderValue? header = ClusterAuthentication.BuildHeader("top-secret");

        Assert.NotNull(header);
        Assert.True(ClusterAuthentication.IsAuthorized(header.ToString(), "top-secret"));
    }
}