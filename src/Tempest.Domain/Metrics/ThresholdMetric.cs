namespace Tempest.Domain.Metrics;

/// <summary>
/// Grandeur mesurable sur laquelle peut porter un <see cref="ThresholdRule"/>.
/// <para>
/// Volontairement absente de cette liste : toute variante <c>Service*</c> (temps de service
/// brut, non corrige du <i>coordinated omission</i>). Gater un pipeline sur le temps de
/// service reviendrait a faire confiance a la mesure meme que Tempest existe pour corriger.
/// Un seuil ne peut donc porter que sur la latence de reponse deja corrigee, sur le taux
/// d'erreur, sur la dette d'ordonnancement, ou sur le nombre de mesures.
/// </para>
/// </summary>
public enum ThresholdMetric
{
    /// <summary>Mediane du temps de reponse corrige.</summary>
    ResponseP50Milliseconds,

    /// <summary>Troisieme quartile du temps de reponse corrige.</summary>
    ResponseP75Milliseconds,

    /// <summary>Neuvieme decile du temps de reponse corrige.</summary>
    ResponseP90Milliseconds,

    /// <summary>95e centile du temps de reponse corrige.</summary>
    ResponseP95Milliseconds,

    /// <summary>99e centile du temps de reponse corrige.</summary>
    ResponseP99Milliseconds,

    /// <summary>99,9e centile du temps de reponse corrige.</summary>
    ResponseP999Milliseconds,

    /// <summary>Temps de reponse corrige le plus haut observe.</summary>
    ResponseMaxMilliseconds,

    /// <summary>Moyenne du temps de reponse corrige.</summary>
    ResponseMeanMilliseconds,

    /// <summary>Proportion de mesures en echec, entre 0 et 1.</summary>
    ErrorRate,

    /// <summary>Dette d'ordonnancement maximale observee sur l'etape.</summary>
    SchedulingDelayMaxMilliseconds,

    /// <summary>
    /// Nombre de mesures. Sert surtout a garder une regle honnete : combinee a une autre regle,
    /// elle detecte le cas ou une etape n'a tout simplement jamais ete executee — sans quoi un
    /// taux d'erreur a zero sur zero mesure passerait a tort pour un succes.
    /// </summary>
    Count,
}