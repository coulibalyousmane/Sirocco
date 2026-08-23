using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Sirocco.Host.Configuration;

namespace Sirocco.Host.Distributed;

/// <summary>
/// Authentification du control plane distribue par secret partage.
/// <para>
/// Desactivee par defaut (<see cref="SiroccoHostOptions.ClusterSharedSecret"/> a
/// <see langword="null"/>) : voir la note de ce champ pour le choix de conception (inspire,
/// puis volontairement durci par rapport au control plane de k6, jamais authentifie).
/// </para>
/// </summary>
public static class ClusterAuthentication
{
    private const string SCHEME = "Bearer";

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