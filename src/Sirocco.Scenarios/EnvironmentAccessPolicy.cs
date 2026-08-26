namespace Sirocco.Scenarios;

/// <summary>
/// Politique d'acces d'un scenario declaratif aux variables d'environnement du processus
/// (<c>{{env.NOM}}</c>, voir la remarque de classe de <see cref="DeclarativeWorkflow"/>).
/// <para>
/// Decidee par l'operateur qui lance le tir, jamais par le scenario lui-meme : une politique
/// portee par le fichier YAML pourrait s'auto-autoriser, ce qui ne serait pas une restriction.
/// Ferme le residu enonce (pas corrige) par SEC-9 dans <c>AUDIT.md</c> : sans cette politique,
/// <c>{{env.NOM}}</c> donnait acces a n'importe quelle variable du processus qui execute le tir,
/// pas seulement a celles que l'operateur voulait exposer.
/// </para>
/// </summary>
public sealed class EnvironmentAccessPolicy
{
    /// <summary>
    /// Aucune variable autorisee — comportement par defaut, avant le tag <c>v0.1.0</c> (SEC-9,
    /// AUDIT.md) : un scenario qui reference <c>{{env.NOM}}</c> sans que l'operateur ait
    /// explicitement autorise ce nom voit son chargement echouer, plutot que de lire silencieusement
    /// n'importe quelle variable du processus.
    /// </summary>
    public static readonly EnvironmentAccessPolicy Denied = new([], allowAll: false);

    private readonly HashSet<string> _allowedNames;
    private readonly bool _allowAll;

    /// <param name="allowedNames">
    /// Noms explicitement autorises. Sans effet si <paramref name="allowAll"/> est vrai.
    /// </param>
    /// <param name="allowAll">
    /// Si vrai, autorise n'importe quelle variable du processus — parite avec <c>__ENV</c> de k6
    /// ou <c>System.getenv</c> de Gatling. A reserver a un processus qui ne detient aucun secret
    /// sans rapport avec le tir lui-meme.
    /// </param>
    public EnvironmentAccessPolicy(IEnumerable<string> allowedNames, bool allowAll)
    {
        ArgumentNullException.ThrowIfNull(allowedNames);

        _allowedNames = new HashSet<string>(allowedNames, StringComparer.Ordinal);
        _allowAll = allowAll;
    }

    /// <summary>Vrai si <paramref name="name"/> peut etre lue par un scenario sous cette politique.</summary>
    public bool Allows(string name) => _allowAll || _allowedNames.Contains(name);
}