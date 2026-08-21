using System.Security.Cryptography.X509Certificates;

namespace Tempest.Host.Distributed;

/// <summary>
/// Epinglage par empreinte du certificat TLS partage entre les trois roles du control plane
/// distribue (maitre, workers).
/// <para>
/// Meme philosophie que <see cref="ClusterAuthentication"/> : un secret partage simple (ici, un
/// seul certificat auto-signe installe sur les trois roles) plutot qu'une PKI complete par
/// noeud — assumee comme simplification, une vraie infrastructure de certificats par noeud
/// (via cert-manager, par exemple) trouvera naturellement sa place dans le chantier Kubernetes
/// suivant.
/// </para>
/// </summary>
public static class ClusterCertificatePinning
{
    /// <summary>Nom du client HTTP nomme utilise pour tous les appels entre maitre et workers.</summary>
    public const string CLUSTER_CLIENT_NAME = "cluster";

    /// <summary>
    /// Vrai si <paramref name="certificate"/> a la meme empreinte que
    /// <paramref name="expectedThumbprint"/>. Les deux empreintes sont normalisees avant
    /// comparaison (separateurs <c>:</c>/espaces retires, casse ignoree) : un operateur peut
    /// coller une empreinte au format <c>openssl</c> (<c>AA:BB:CC</c>) ou PowerShell aussi bien
    /// que le format brut de <see cref="X509Certificate2.Thumbprint"/>.
    /// </summary>
    public static bool ValidateThumbprint(X509Certificate2? certificate, string? expectedThumbprint) =>
        certificate is not null
        && !string.IsNullOrEmpty(expectedThumbprint)
        && string.Equals(Normalize(certificate.Thumbprint), Normalize(expectedThumbprint), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Construit le gestionnaire HTTP du client de cluster. Si <paramref name="expectedThumbprint"/>
    /// est renseigne, la validation du certificat presente par le pair est remplacee par
    /// <see cref="ValidateThumbprint"/> ; sinon la validation par defaut du systeme (chaine de
    /// confiance normale) reste en place, inchangee — utile si l'operateur prefere un certificat
    /// signe par une vraie CA plutot que le certificat partage auto-signe.
    /// </summary>
    public static HttpClientHandler CreateHandler(string? expectedThumbprint)
    {
        HttpClientHandler handler = new();

        if (!string.IsNullOrEmpty(expectedThumbprint))
        {
            handler.ServerCertificateCustomValidationCallback =
                (_, certificate, _, _) => ValidateThumbprint(certificate, expectedThumbprint);
        }

        return handler;
    }

    private static string Normalize(string thumbprint) =>
        new([.. thumbprint.Where(char.IsAsciiHexDigit)]);
}