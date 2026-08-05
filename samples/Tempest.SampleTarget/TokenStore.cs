using System.Collections.Concurrent;

namespace Tempest.SampleTarget;

/// <summary>
/// Registre des jetons emis par la connexion, avec expiration. La cible peut ainsi exiger
/// de temps en temps une reconnexion — exactement ce que le rafraichissement de session
/// d'un scenario client bien ecrit doit savoir absorber sans faire echouer l'iteration.
/// </summary>
internal sealed class TokenStore(TimeSpan lifetime)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiresAt = new();

    /// <summary>Emet un nouveau jeton, valide pendant la duree de vie configuree.</summary>
    public string Issue()
    {
        string token = Guid.NewGuid().ToString("N");
        _expiresAt[token] = DateTimeOffset.UtcNow + lifetime;
        return token;
    }

    /// <summary>Indique si le jeton existe et n'a pas expire.</summary>
    public bool IsValid(string? token) =>
        !string.IsNullOrEmpty(token)
        && _expiresAt.TryGetValue(token, out DateTimeOffset expiresAt)
        && expiresAt > DateTimeOffset.UtcNow;
}