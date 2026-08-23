using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Sirocco.Host.Distributed;

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

    /// <summary>Longueur d'une empreinte SHA-256 en hexadecimal : 32 octets, donc 64 caracteres.</summary>
    private const int SHA256_HEX_LENGTH = 64;

    /// <summary>
    /// Vrai si <paramref name="certificate"/> a la meme empreinte <b>SHA-256</b> que
    /// <paramref name="expectedThumbprint"/>. Les deux empreintes sont normalisees avant
    /// comparaison (separateurs <c>:</c>/espaces retires, casse ignoree) : un operateur peut
    /// coller une empreinte au format <c>openssl</c> (<c>AA:BB:CC</c>) aussi bien que le format
    /// brut renvoye par PowerShell.
    /// <para>
    /// Volontairement <b>pas</b> <see cref="X509Certificate2.Thumbprint"/>, qui est un SHA-1 par
    /// definition : les collisions a prefixe choisi sur SHA-1 sont demontrees depuis 2017, et une
    /// empreinte est precisement ce sur quoi repose toute la confiance ici.
    /// </para>
    /// </summary>
    public static bool ValidateThumbprint(X509Certificate2? certificate, string? expectedThumbprint)
    {
        if (certificate is null || string.IsNullOrEmpty(expectedThumbprint))
        {
            return false;
        }

        string actual = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
        return string.Equals(Normalize(actual), Normalize(expectedThumbprint), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Construit le gestionnaire HTTP du client de cluster. Si <paramref name="expectedThumbprint"/>
    /// est renseigne, la validation du certificat presente par le pair est remplacee par
    /// <see cref="ValidateThumbprint"/> ; sinon la validation par defaut du systeme (chaine de
    /// confiance normale) reste en place, inchangee — utile si l'operateur prefere un certificat
    /// signe par une vraie CA plutot que le certificat partage auto-signe.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Si l'empreinte fournie n'a pas la longueur d'un SHA-256. Echouer au demarrage plutot qu'au
    /// premier appel TLS est deliberé : une empreinte SHA-1 laissee en configuration ne
    /// correspondrait simplement jamais, et le symptome serait des connexions refusees sans motif
    /// lisible plutot qu'un message disant quoi corriger.
    /// </exception>
    public static HttpClientHandler CreateHandler(string? expectedThumbprint)
    {
        HttpClientHandler handler = new();

        if (!string.IsNullOrEmpty(expectedThumbprint))
        {
            string normalized = Normalize(expectedThumbprint);

            if (normalized.Length != SHA256_HEX_LENGTH)
            {
                throw new ArgumentException(
                    $"L'empreinte de certificat de cluster doit etre un SHA-256 ({SHA256_HEX_LENGTH} caracteres "
                    + $"hexadecimaux), or celle fournie en compte {normalized.Length}. Une empreinte de 40 caracteres "
                    + "est un SHA-1 : la recalculer en SHA-256, par exemple avec "
                    + "'openssl x509 -in cert.crt -noout -fingerprint -sha256'.",
                    nameof(expectedThumbprint));
            }

            handler.ServerCertificateCustomValidationCallback =
                (_, certificate, _, _) => ValidateThumbprint(certificate, expectedThumbprint);
        }

        return handler;
    }

    private static string Normalize(string thumbprint) =>
        new([.. thumbprint.Where(char.IsAsciiHexDigit)]);
}