namespace Tempest.Domain.Metrics;

/// <summary>
/// Photographie immuable d'une distribution de latences, en microsecondes.
/// </summary>
/// <param name="Count">Nombre de mesures agregees.</param>
/// <param name="MinMicroseconds">Valeur la plus basse observee.</param>
/// <param name="MaxMicroseconds">Valeur la plus haute observee.</param>
/// <param name="MeanMicroseconds">Moyenne exacte, calculee sur la somme reelle et non sur les paniers.</param>
/// <param name="P50Microseconds">Mediane.</param>
/// <param name="P75Microseconds">Troisieme quartile.</param>
/// <param name="P90Microseconds">Neuvieme decile.</param>
/// <param name="P95Microseconds">95e centile.</param>
/// <param name="P99Microseconds">99e centile.</param>
/// <param name="P999Microseconds">99,9e centile.</param>
public readonly record struct LatencySnapshot(
    long Count,
    long MinMicroseconds,
    long MaxMicroseconds,
    double MeanMicroseconds,
    long P50Microseconds,
    long P75Microseconds,
    long P90Microseconds,
    long P95Microseconds,
    long P99Microseconds,
    long P999Microseconds)
{
    private const double MICROSECONDS_PER_MILLISECOND = 1_000d;

    /// <summary>Distribution vide.</summary>
    public static readonly LatencySnapshot Empty = default;

    /// <summary>Indique qu'aucune mesure n'a ete agregee.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Mediane, en millisecondes.</summary>
    public double P50Milliseconds => P50Microseconds / MICROSECONDS_PER_MILLISECOND;

    /// <summary>Troisieme quartile, en millisecondes.</summary>
    public double P75Milliseconds => P75Microseconds / MICROSECONDS_PER_MILLISECOND;

    /// <summary>Neuvieme decile, en millisecondes.</summary>
    public double P90Milliseconds => P90Microseconds / MICROSECONDS_PER_MILLISECOND;

    /// <summary>95e centile, en millisecondes.</summary>
    public double P95Milliseconds => P95Microseconds / MICROSECONDS_PER_MILLISECOND;

    /// <summary>99e centile, en millisecondes.</summary>
    public double P99Milliseconds => P99Microseconds / MICROSECONDS_PER_MILLISECOND;

    /// <summary>99,9e centile, en millisecondes.</summary>
    public double P999Milliseconds => P999Microseconds / MICROSECONDS_PER_MILLISECOND;

    /// <summary>Valeur la plus haute, en millisecondes.</summary>
    public double MaxMilliseconds => MaxMicroseconds / MICROSECONDS_PER_MILLISECOND;

    /// <summary>Moyenne, en millisecondes.</summary>
    public double MeanMilliseconds => MeanMicroseconds / MICROSECONDS_PER_MILLISECOND;

    /// <inheritdoc />
    public override string ToString() =>
        IsEmpty
            ? "aucune mesure"
            : $"n={Count} moy={MeanMilliseconds:F2}ms p50={P50Milliseconds:F2}ms " +
              $"p95={P95Milliseconds:F2}ms p99={P99Milliseconds:F2}ms max={MaxMilliseconds:F2}ms";
}