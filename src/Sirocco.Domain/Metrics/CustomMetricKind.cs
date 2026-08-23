namespace Sirocco.Domain.Metrics;

/// <summary>
/// Nature d'une métrique personnalisée, alimentée depuis un scénario plutôt que par le moteur
/// lui-même — même vocabulaire que k6 (<c>Counter</c>/<c>Gauge</c>/<c>Rate</c>/<c>Trend</c>).
/// </summary>
public enum CustomMetricKind
{
    /// <summary>Somme cumulée, jamais décroissante (nombre de commandes, octets métier envoyés...).</summary>
    Counter,

    /// <summary>Dernière valeur observée (taille de panier courante, nombre de connexions actives...).</summary>
    Gauge,

    /// <summary>Fraction de mesures qui satisfont une condition (taux de cache hit, de succès métier...).</summary>
    Rate,

    /// <summary>Distribution d'une valeur numérique (montant d'une commande, taille d'une réponse métier...).</summary>
    Trend,
}