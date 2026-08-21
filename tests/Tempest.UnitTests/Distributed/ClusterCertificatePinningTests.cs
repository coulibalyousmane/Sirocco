using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tempest.Host.Distributed;

namespace Tempest.UnitTests.Distributed;

public sealed class ClusterCertificatePinningTests
{
    [Fact]
    public void The_certificate_s_own_thumbprint_is_accepted()
    {
        using X509Certificate2 certificate = CreateSelfSignedCertificate();

        Assert.True(ClusterCertificatePinning.ValidateThumbprint(certificate, certificate.Thumbprint));
    }

    [Fact]
    public void A_different_certificate_s_thumbprint_is_rejected()
    {
        using X509Certificate2 expected = CreateSelfSignedCertificate();
        using X509Certificate2 presented = CreateSelfSignedCertificate();

        Assert.False(ClusterCertificatePinning.ValidateThumbprint(presented, expected.Thumbprint));
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
        string withColons = string.Join(':', Chunk(certificate.Thumbprint.ToLowerInvariant(), 2));

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
        using HttpClientHandler handler = ClusterCertificatePinning.CreateHandler(accepted.Thumbprint);

        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
        Assert.True(handler.ServerCertificateCustomValidationCallback!(null!, accepted, null, System.Net.Security.SslPolicyErrors.None));
        Assert.False(handler.ServerCertificateCustomValidationCallback!(null!, rejected, null, System.Net.Security.SslPolicyErrors.None));
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=tempest-cluster-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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