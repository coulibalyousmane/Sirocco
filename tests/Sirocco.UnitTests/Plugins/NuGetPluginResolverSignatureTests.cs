using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Packaging.Signing;
using NuGet.Versioning;
using Sirocco.Domain.Execution;
using Sirocco.Scenarios.Plugins;

namespace Sirocco.UnitTests.Plugins;

/// <summary>
/// SEC-7 (AUDIT.md) : verifie la politique de signature de <see cref="NuGetPluginResolver"/> contre
/// un vrai <c>.nupkg</c> signe hors ligne avec un vrai certificat auto-signe
/// (<see cref="CreateSelfSignedTestCertificate"/> + <see cref="SigningUtility.SignAsync"/>, la
/// meme API que <c>nuget sign</c>) — jamais un double de la verification NuGet elle-meme.
/// </summary>
public sealed class NuGetPluginResolverSignatureTests
{
    [Fact]
    public async Task A_signed_package_is_accepted_by_default()
    {
        (string feed, string cache) = NuGetPluginResolverTests.CreateDirectories();
        BuildSignedLocalPackage(feed, "Sirocco.Test.SignedPlugin", "1.0.0", "signed");

        string assemblyPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            "Sirocco.Test.SignedPlugin", "1.0.0", [feed], cache);

        IWorkflow workflow = PluginWorkflowLoader.Load(assemblyPath);
        Assert.Equal("signed", workflow.Name);
    }

    [Fact]
    public async Task A_signed_package_whose_content_changed_since_signing_is_rejected()
    {
        (string feed, string cache) = NuGetPluginResolverTests.CreateDirectories();
        string nupkgPath = BuildSignedLocalPackage(feed, "Sirocco.Test.TamperedPlugin", "1.0.0", "tampered");

        // Modifie un octet du .nupkg signe, en dehors de la manoeuvre de signature elle-meme :
        // le contenu ne correspond plus a ce que la signature atteste, exactement le scenario
        // qu'une source compromise ou un cache local altere reproduirait.
        byte[] bytes = await File.ReadAllBytesAsync(nupkgPath);
        bytes[bytes.Length / 3] ^= 0xFF;
        await File.WriteAllBytesAsync(nupkgPath, bytes);

        FormatException ex = await Assert.ThrowsAsync<FormatException>(
            () => NuGetPluginResolver.ResolveAssemblyPathAsync("Sirocco.Test.TamperedPlugin", "1.0.0", [feed], cache));
        Assert.Contains("a change depuis sa signature", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Construit un <c>.nupkg</c> reel (meme helper que <see cref="NuGetPluginResolverTests"/>)
    /// puis le signe pour de vrai avec un certificat auto-signe genere hors ligne
    /// (<see cref="CreateSelfSignedTestCertificate"/>) — <see cref="SigningUtility.SignAsync"/> est
    /// l'API que <c>nuget sign</c> appelle elle-meme, pas une simulation.
    /// </summary>
    private static string BuildSignedLocalPackage(string feedDirectory, string packageId, string version, string workflowName)
    {
        string dllPath = NuGetPluginResolverTests.CompileWorkflowAssembly(workflowName);

        PackageBuilder builder = new()
        {
            Id = packageId,
            Version = NuGetVersion.Parse(version),
            Description = "Paquet de test Sirocco, jamais publie.",
        };
        builder.Authors.Add("Sirocco.UnitTests");
        builder.Files.Add(new PhysicalPackageFile
        {
            SourcePath = dllPath,
            TargetPath = $"lib/net10.0/{Path.GetFileName(dllPath)}",
        });

        string unsignedPath = Path.Combine(feedDirectory, $"{packageId}.{version}.unsigned.nupkg");
        using (FileStream stream = File.Create(unsignedPath))
        {
            builder.Save(stream);
        }

        string signedPath = Path.Combine(feedDirectory, $"{packageId}.{version}.nupkg");
        using X509Certificate2 certificate = CreateSelfSignedTestCertificate();
        AuthorSignPackageRequest signRequest = new(certificate, NuGet.Common.HashAlgorithmName.SHA256) { AllowUntrustedRoot = true };
        X509SignatureProvider signatureProvider = new(timestampProvider: null);
        using (SigningOptions signingOptions = SigningOptions.CreateFromFilePaths(
            unsignedPath, signedPath, overwrite: true, signatureProvider, NullLogger.Instance))
        {
            SigningUtility.SignAsync(signingOptions, signRequest, CancellationToken.None).GetAwaiter().GetResult();
        }

        File.Delete(unsignedPath);
        return signedPath;
    }

    /// <summary>
    /// Certificat auto-signe genere hors ligne pour la duree du test — meme demarche que
    /// <c>ClusterCertificatePinningTests</c> (<see cref="System.Security.Cryptography.X509Certificates.CertificateRequest"/>,
    /// aucun reseau). <see cref="X509KeyUsageFlags.DigitalSignature"/> et l'EKU
    /// <c>codeSigning</c> (1.3.6.1.5.5.7.3.3) sont requis : <see cref="SigningUtility.SignAsync"/>
    /// rejette un certificat qui ne les porte pas.
    /// </summary>
    private static X509Certificate2 CreateSelfSignedTestCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=Sirocco Test Signing", key, System.Security.Cryptography.HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }
}