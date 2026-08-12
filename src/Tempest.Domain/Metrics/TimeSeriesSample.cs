namespace Tempest.Domain.Metrics;

/// <summary>
/// Photographie instantanee de l'etape technique <see cref="WellKnownSteps.ITERATION"/>, relevee
/// periodiquement pendant le tir plutot qu'une seule fois a la fin — la trajectoire, pas
/// seulement l'etat final.
/// <para>
/// Les valeurs viennent toutes d'un <see cref="StatisticsScope.Sliding"/> : chaque point reflete
/// donc la fenetre glissante recente au moment du releve, pas le cumul depuis le debut du tir,
/// exactement comme <c>/report/live</c>.
/// </para>
/// </summary>
public sealed record TimeSeriesSample
{
    /// <summary>Temps ecoule depuis le debut du releve, en secondes.</summary>
    public required double ElapsedSeconds { get; init; }

    /// <summary>Debit instantane (fenetre glissante), en iterations par seconde.</summary>
    public required double IterationsPerSecond { get; init; }

    /// <summary>
    /// Nombre d'utilisateurs virtuels actifs a cet instant — voir <see cref="Execution.ActiveVirtualUserGauge"/>.
    /// Constant sur toute la duree d'un tir a effectif fixe, variable en modele ferme a montee
    /// d'utilisateurs.
    /// </summary>
    public required int ActiveVirtualUsers { get; init; }

    /// <summary>Proportion d'echecs sur la fenetre glissante, entre 0 et 1.</summary>
    public required double ErrorRate { get; init; }

    /// <summary>Mediane du temps de reponse corrige, en millisecondes.</summary>
    public required double ResponseP50Milliseconds { get; init; }

    /// <summary>95e centile du temps de reponse corrige, en millisecondes.</summary>
    public required double ResponseP95Milliseconds { get; init; }

    /// <summary>99e centile du temps de reponse corrige, en millisecondes.</summary>
    public required double ResponseP99Milliseconds { get; init; }

    /// <summary>
    /// Dette d'ordonnancement maximale observee sur la fenetre glissante, en millisecondes — voir
    /// <see cref="StepStatistics.MaxSchedulingDelayMilliseconds"/>.
    /// </summary>
    public required double MaxSchedulingDelayMilliseconds { get; init; }
}