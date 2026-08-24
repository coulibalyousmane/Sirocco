using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Sirocco.Host.Configuration;

namespace Sirocco.Host.Distributed;

/// <summary>
/// Authentification du control plane distribue par secret partage.
/// <para>
/// Exigee : un role <c>master</c> ou <c>worker</c> sans
/// <see cref="SiroccoHostOptions.ClusterSharedSecret"/> refuse de demarrer
/// (<see cref="EnsureConfigured"/>), sauf declaration explicite par
/// <see cref="SiroccoHostOptions.AllowUnauthenticatedClusterControlPlane"/>. Le control plane de
/// k6 dont ce mecanisme s'inspire n'est, lui, jamais authentifie.
/// </para>
/// </summary>
public static class ClusterAuthentication
{
    private const string SCHEME = "Bearer";

    /// <summary>
    /// Longueur minimale d'un secret partage. Rien ne limite le nombre d'essais cote serveur (pas
    /// de quota, pas de bannissement) : la seule defense contre la devinette est la taille du
    /// secret. Un plancher bas rendrait le garde de <see cref="EnsureConfigured"/> decoratif — un
    /// secret d'un caractere le franchirait.
    /// </summary>
    public const int MINIMUM_SHARED_SECRET_LENGTH = 16;

    /// <summary>
    /// Verifie un en-tete <c>Authorization</c> recu contre le secret configure.
    /// <para>
    /// La comparaison des octets se fait en temps constant
    /// (<see cref="CryptographicOperations.FixedTimeEquals"/>) : le temps de reponse ne doit
    /// rien reveler du contenu du secret a un attaquant qui essaierait de le deviner
    /// caractere par caractere.
    /// </para>
    /// </summary>
    public static bool IsAuthorized(string? authorizationHeader, string? sharedSecret)
    {
        if (string.IsNullOrEmpty(sharedSecret))
        {
            return true;
        }

        if (authorizationHeader is null
            || !AuthenticationHeaderValue.TryParse(authorizationHeader, out AuthenticationHeaderValue? parsed)
            || !string.Equals(parsed.Scheme, SCHEME, StringComparison.OrdinalIgnoreCase)
            || parsed.Parameter is null)
        {
            return false;
        }

        byte[] provided = Encoding.UTF8.GetBytes(parsed.Parameter);
        byte[] expected = Encoding.UTF8.GetBytes(sharedSecret);

        return provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    /// <summary>En-tete <c>Authorization</c> a envoyer pour ce secret, ou <see langword="null"/> si aucun secret n'est configure.</summary>
    public static AuthenticationHeaderValue? BuildHeader(string? sharedSecret) =>
        string.IsNullOrEmpty(sharedSecret) ? null : new AuthenticationHeaderValue(SCHEME, sharedSecret);

    /// <summary>
    /// Verifie au demarrage qu'un role du mode distribue est authentifie, et leve sinon.
    /// <para>
    /// Le defaut historique etait de demarrer ouvert : <see cref="IsAuthorized"/> rend
    /// <see langword="true"/> sans regarder la requete quand aucun secret n'est configure, donc
    /// <c>/worker/prepare</c> et <c>/worker/start</c> acceptaient n'importe quel appelant. Or la
    /// requete de preparation porte l'URL de la cible, le profil de charge et le plafond
    /// d'utilisateurs virtuels : un worker joignable etait un generateur de charge telecommande.
    /// Echouer au demarrage plutot que servir ouvert renverse la charge de la decision — l'ouverture
    /// devient un choix ecrit, pas un defaut herite.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Si aucun secret n'est configure et que l'ouverture n'a pas ete declaree, ou si le secret
    /// configure est plus court que <see cref="MINIMUM_SHARED_SECRET_LENGTH"/>.
    /// </exception>
    public static void EnsureConfigured(string role, string? sharedSecret, bool allowUnauthenticated)
    {
        if (string.IsNullOrEmpty(sharedSecret))
        {
            // L'echappatoire ne couvre que l'absence de secret. Un secret trop court, lui, reste
            // une erreur meme ici : il serait applique (IsAuthorized le comparerait) tout en etant
            // devinable, ce qui est le pire des deux mondes — protege en apparence, pas en fait.
            if (allowUnauthenticated)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Le role '{role}' exige un secret partage de control plane. Sans lui, "
                + "'/worker/prepare' et '/worker/start' acceptent n'importe quel appelant, et la "
                + "requete de preparation porte l'URL de la cible : un worker joignable devient un "
                + "generateur de charge telecommande. Configurez 'Sirocco__ClusterSharedSecret' "
                + $"(au moins {MINIMUM_SHARED_SECRET_LENGTH} caracteres). Si ce control plane est "
                + "reellement confine a un reseau de confiance, declarez-le explicitement avec "
                + "'Sirocco__AllowUnauthenticatedClusterControlPlane=true'.");
        }

        if (sharedSecret.Length < MINIMUM_SHARED_SECRET_LENGTH)
        {
            throw new InvalidOperationException(
                $"Le secret partage de control plane compte {sharedSecret.Length} caractere(s), or "
                + $"au moins {MINIMUM_SHARED_SECRET_LENGTH} sont exiges : rien ne limite le nombre "
                + "d'essais cote serveur, donc la taille du secret est la seule defense contre la "
                + "devinette. Allongez 'Sirocco__ClusterSharedSecret'.");
        }
    }
}

/// <summary>Applique <see cref="ClusterAuthentication"/> a un endpoint minimal API.</summary>
public sealed class ClusterAuthenticationFilter(SiroccoHostOptions options) : IEndpointFilter
{
    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        string? header = context.HttpContext.Request.Headers.Authorization;
        return ClusterAuthentication.IsAuthorized(header, options.ClusterSharedSecret)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Unauthorized());
    }
}