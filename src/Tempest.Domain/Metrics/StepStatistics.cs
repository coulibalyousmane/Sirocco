namespace Tempest.Domain.Metrics;

/// <summary>
/// Statistiques agregees d'une etape.
/// <para>
/// Les <b>deux</b> distributions de latence sont conservees cote a cote, et c'est
/// intentionnel : <see cref="Response"/> mesure depuis l'instant de depart theorique,
/// <see cref="Service"/> depuis l'envoi reel. Leur ecart <i>est</i> le coordinated omission.
/// Publier la seconde seule reviendrait a livrer le chiffre flatteur en cachant le vrai.
/// </para>
/// </summary>
public sealed record StepStatistics
{
    private const double MICROSECONDS_PER_MILLISECOND = 1_000d;

    /// <summary>Nom lisible de l'etape.</summary>
    public required string Name { get; init; }

    /// <summary>Identifiant de l'etape.</summary>
    public required StepId Step { get; init; }

    /// <summary>Nombre total de mesures.</summary>
    public required long Count { get; init; }

    /// <summary>Nombre de mesures en succes.</summary>
    public required long SuccessCount { get; init; }

    /// <summary>Nombre d'iterations abandonnees faute d'avoir pu demarrer a l'heure.</summary>
    public required long DroppedCount { get; init; }

    /// <summary>Repartition detaillee par issue, indexee par <see cref="RequestOutcome"/>.</summary>
    public required IReadOnlyList<long> CountByOutcome { get; init; }

    /// <summary>Volume total recu, en octets.</summary>
    public required long BytesReceived { get; init; }

    /// <summary>Dette d'ordonnancement maximale observee sur l'etape, en microsecondes.</summary>
    public required long MaxSchedulingDelayMicroseconds { get; init; }

    /// <summary>Dette d'ordonnancement maximale, en millisecondes.</summary>
    public double MaxSchedulingDelayMilliseconds => MaxSchedulingDelayMicroseconds / MICROSECONDS_PER_MILLISECOND;

    /// <summary>
    /// Temps de reponse corrige du <i>coordinated omission</i> : c'est la distribution a
    /// publier et a comparer a un SLO.
    /// </summary>
    public required LatencySnapshot Response { get; init; }

    /// <summary>
    /// Temps de service brut, hors attente dans la file de l'injecteur : ce que mesurerait
    /// un outil naif.
    /// </summary>
    public required LatencySnapshot Service { get; init; }

    /// <summary>Nombre de mesures qui ne sont pas des succes.</summary>
    public long FailureCount => Count - SuccessCount;

    /// <summary>Proportion d'echecs, entre 0 et 1.</summary>
    public double ErrorRate => Count == 0L ? 0d : FailureCount / (double)Count;

    /// <summary>
    /// Ecart entre le 99e centile corrige et le 99e centile brut, en millisecondes.
    /// <para>
    /// Un ecart significatif signifie que l'injecteur ou la cible ont sature : les chiffres
    /// bruts sous-estiment alors ce que l'utilisateur final a reellement subi.
    /// </para>
    /// </summary>
    public double CoordinatedOmissionP99Milliseconds => Response.P99Milliseconds - Service.P99Milliseconds;

    /// <summary>Etape sans aucune mesure.</summary>
    public static StepStatistics Empty(StepId step, string name) => new()
    {
        Name = name,
        Step = step,
        Count = 0L,
        SuccessCount = 0L,
        DroppedCount = 0L,
        CountByOutcome = [],
        BytesReceived = 0L,
        MaxSchedulingDelayMicroseconds = 0L,
        Response = LatencySnapshot.Empty,
        Service = LatencySnapshot.Empty,
    };

    /// <inheritdoc />
    public override string ToString() =>
        $"{Name} : {Count} mesures, {ErrorRate:P2} d'echecs, p99 corrige {Response.P99Milliseconds:F2} ms " +
        $"(brut {Service.P99Milliseconds:F2} ms)";
}