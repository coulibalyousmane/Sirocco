namespace Tempest.RecorderProxy;

/// <summary>
/// Filtre les en-tetes de bout en bout (RFC 7230 §6.1) qui n'ont pas de sens a retransmettre
/// tels quels d'un cote a l'autre du proxy : soit geres par la pile HTTP elle-meme (longueur du
/// corps, connexion), soit propres a la connexion TCP locale plutot qu'a la requete logique.
/// </summary>
public static class ProxyHeaders
{
    private static readonly HashSet<string> _hopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Content-Type",
        "Connection",
        "Keep-Alive",
        "Transfer-Encoding",
        "Upgrade",
        "Proxy-Connection",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
    };

    /// <summary>
    /// Indique si l'en-tete doit etre retransmis tel quel. <c>Content-Type</c> est exclu ici
    /// aussi : il se porte sur le corps (<c>HttpContent.Headers.ContentType</c>), jamais sur les
    /// en-tetes generaux de la requete/reponse.
    /// </summary>
    public static bool ShouldForward(string headerName) => !_hopByHop.Contains(headerName);
}