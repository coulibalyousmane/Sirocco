using Sirocco.Host.Configuration;
using Sirocco.Host.Distributed;

namespace Sirocco.UnitTests.Distributed;

public sealed class ClusterAuthenticationTests
{
    // Le filtre laisse tout passer quand aucun secret n'est configure — etat qu'un role
    // master/worker ne peut plus atteindre sans l'avoir declare (voir EnsureConfigured plus bas).
    [Fact]
    public void No_secret_configured_authorizes_every_request()
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

    // Contre-epreuve du renversement du defaut : c'est ce test qui echouerait si quelqu'un
    // reintroduisait un demarrage ouvert, y compris par inadvertance.
    [Theory]
    [InlineData(SiroccoHostOptions.ROLE_MASTER)]
    [InlineData(SiroccoHostOptions.ROLE_WORKER)]
    public void A_distributed_role_without_a_shared_secret_refuses_to_start(string role)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ClusterAuthentication.EnsureConfigured(role, sharedSecret: null, allowUnauthenticated: false));

        Assert.Contains(role, error.Message, StringComparison.Ordinal);
        Assert.Contains("Sirocco__ClusterSharedSecret", error.Message, StringComparison.Ordinal);

        // Le message doit nommer l'echappatoire : un operateur bloque au demarrage doit pouvoir
        // sortir de la sans aller lire le code.
        Assert.Contains("Sirocco__AllowUnauthenticatedClusterControlPlane", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_shared_secret_is_treated_as_absent() =>
        Assert.Throws<InvalidOperationException>(
            () => ClusterAuthentication.EnsureConfigured(SiroccoHostOptions.ROLE_WORKER, sharedSecret: "", allowUnauthenticated: false));

    [Fact]
    public void A_configured_shared_secret_of_sufficient_length_starts() =>
        ClusterAuthentication.EnsureConfigured(
            SiroccoHostOptions.ROLE_MASTER,
            new string('s', ClusterAuthentication.MINIMUM_SHARED_SECRET_LENGTH),
            allowUnauthenticated: false);

    [Fact]
    public void The_explicit_opt_out_permits_starting_without_a_secret() =>
        ClusterAuthentication.EnsureConfigured(
            SiroccoHostOptions.ROLE_WORKER,
            sharedSecret: null,
            allowUnauthenticated: true);

    [Fact]
    public void A_secret_shorter_than_the_minimum_is_refused()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ClusterAuthentication.EnsureConfigured(
                SiroccoHostOptions.ROLE_WORKER,
                new string('s', ClusterAuthentication.MINIMUM_SHARED_SECRET_LENGTH - 1),
                allowUnauthenticated: false));

        Assert.Contains(ClusterAuthentication.MINIMUM_SHARED_SECRET_LENGTH.ToString(), error.Message, StringComparison.Ordinal);
    }

    // L'echappatoire ne couvre que l'absence de secret : un secret court serait applique tout en
    // etant devinable, donc protege en apparence seulement — le pire des deux etats.
    [Fact]
    public void The_opt_out_does_not_excuse_a_secret_that_is_too_short() =>
        Assert.Throws<InvalidOperationException>(
            () => ClusterAuthentication.EnsureConfigured(
                SiroccoHostOptions.ROLE_WORKER,
                sharedSecret: "court",
                allowUnauthenticated: true));
}