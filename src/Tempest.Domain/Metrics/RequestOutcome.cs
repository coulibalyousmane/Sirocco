namespace Tempest.Domain.Metrics;

/// <summary>
/// Issue d'une etape executee par un utilisateur virtuel.
/// Encode sur 4 octets pour rester dans une structure sans reference.
/// </summary>
public enum RequestOutcome
{
    /// <summary>L'etape a repondu et toutes les assertions sont passees.</summary>
    Success = 0,

    /// <summary>Reponse recue mais une assertion metier a echoue (statut, contenu, temps).</summary>
    AssertionFailed = 1,

    /// <summary>Reponse recue avec un code d'erreur HTTP (4xx / 5xx).</summary>
    HttpError = 2,

    /// <summary>Le delai d'attente de la requete a expire.</summary>
    Timeout = 3,

    /// <summary>Echec au niveau transport : DNS, TCP, TLS, socket reset.</summary>
    ConnectionError = 4,

    /// <summary>Exception non prevue levee par le scenario.</summary>
    ScenarioError = 5,

    /// <summary>L'etape a ete annulee par l'arret du test.</summary>
    Cancelled = 6,

    /// <summary>
    /// L'iteration n'a jamais pu demarrer : l'injecteur etait sature et le jeton
    /// a expire avant d'etre consomme. Signal fort de "coordinated omission".
    /// </summary>
    Dropped = 7,
}