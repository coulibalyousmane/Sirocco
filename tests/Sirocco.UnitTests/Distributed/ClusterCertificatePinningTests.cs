using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Sirocco.Host.Distributed;

namespace Sirocco.UnitTests.Distributed;

public sealed class ClusterCertificatePinningTests
{
    [Fact]
    public void The_certificate_s_own_sha256_thumbprint_is_accepted()
    {
        using X509Certificate2 certificate = CreateSelfSignedCertificate();

        Assert.True(ClusterCertificatePinning.ValidateThumbprint(certificate, Sha256Thumbprint(certificate)));
    }

    [Fact]
    public void A_different_certificate_s_thumbprint_is_rejected()
    {
        using X509Certificate2 expected = CreateSelfSignedCertificate();
        using X509Certificate2 presented = CreateSelfSignedCertificate();

        Assert.False(ClusterCertificatePinning.ValidateThumbprint(presented, Sha256Thumbprint(expected)));
    }

    // Contre-epreuve du passage de SHA-1 a SHA-256 : la propriete Thumbprint de .NET est un SHA-1,
    // et c'est exactement ce que l'epinglage comparait avant. Ce test echouerait si quelqu'un
    // revenait a X509Certificate2.Thumbprint, y compris par inadvertance.
    [Fact]
    public void The_certificate_s_sha1_thumbprint_is_no_longer_accepted()
    {
        using X509Certificate2 certificate = CreateSelfSignedCertificate();

        Assert.False(ClusterCertificatePinning.ValidateThumbprint(certificate, certificate.Thumbprint));
    }

    [Fact]
    public void A_null_certificate_is_rejected()
    {
        Assert.False(ClusterCertificatePinning.ValidateThumbprint(certificate: null, expectedThumbprint: "AABBCC"));
    }

    [Fact]
    public void A_null_or_empty_expected_thumbprint_rejects_any_certificate()
    {
        using X509Certificate2 certificate = CreateSelfSignedCertificate();

        Assert.False(ClusterCertificatePinning.ValidateThumbprint(certificate, expectedThumbprint: null));
        Assert.False(ClusterCertificatePinning.ValidateThumbprint(certificate, expectedThumbprint: ""));
    }

    [Fact]
    public void Colon_separators_and_case_are_ignored()
    {
        using X509Certificate2 certificate = CreateSelfSignedCertificate();
        string withColons = string.Join(':', Chunk(Sha256Thumbprint(certificate).ToLowerInvariant(), 2));

        Assert.True(ClusterCertificatePinning.ValidateThumbprint(certificate, withColons));
    }

    [Fact]
    public void CreateHandler_without_a_configured_thumbprint_keeps_default_validation()
    {
        using HttpClientHandler handler = ClusterCertificatePinning.CreateHandler(expectedThumbprint: null);

        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void CreateHandler_with_a_configured_thumbprint_pins_the_certificate()
    {
        using X509Certificate2 accepted = CreateSelfSignedCertificate();
        using X509Certificate2 rejected = CreateSelfSignedCertificate();
        using HttpClientHandler handler = ClusterCertificatePinning.CreateHandler(Sha256Thumbprint(accepted));

        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
        Assert.True(handler.ServerCertificateCustomValidationCallback!(null!, accepted, null, System.Net.Security.SslPolicyErrors.None));
        Assert.False(handler.ServerCertificateCustomValidationCallback!(null!, rejected, null, System.Net.Security.SslPolicyErrors.None));
    }

    // Une empreinte SHA-1 laissee en configuration ne correspondrait jamais : le symptome serait
    // des connexions refusees sans motif lisible. On echoue donc au demarrage, avec un message qui
    // dit quoi recalculer.
    [Fact]
    public void CreateHandler_rejects_a_sha1_length_thumbprint_at_startup()
    {
        using X509Certificate2 certificate = CreateSelfSignedCertificate();

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => ClusterCertificatePinning.CreateHandler(certificate.Thumbprint));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.Contains("40", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateHandler_accepts_a_sha256_thumbprint_written_with_separators()
    {
        using X509Certificate2 certificate = CreateSelfSignedCertificate();
        string withColons = string.Join(':', Chunk(Sha256Thumbprint(certificate), 2));

        using HttpClientHandler handler = ClusterCertificatePinning.CreateHandler(withColons);

        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
    }

    private static string Sha256Thumbprint(X509Certificate2 certificate) =>
        Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=sirocco-cluster-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private static IEnumerable<string> Chunk(string value, int size)
    {
        for (int i = 0; i < value.Length; i += size)
        {
            yield return value.Substring(i, Math.Min(size, value.Length - i));
        }
    }
}