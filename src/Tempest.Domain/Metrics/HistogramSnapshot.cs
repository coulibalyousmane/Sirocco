namespace Tempest.Domain.Metrics;

/// <summary>
/// Etat brut exportable d'un <see cref="LatencyHistogram"/> : les paniers eux-memes, pas des
/// centiles deja calcules.
/// <para>
/// Sert uniquement au transfert entre process (mode distribue Master/Workers) : deux centiles
/// deja calcules ne se fusionnent jamais correctement ("centile de centiles"), alors que deux
/// histogrammes bruts se fusionnent exactement, panier par panier. C'est pourquoi ce type
/// existe a cote de <see cref="LatencySnapshot"/> plutot qu'a sa place.
/// </para>
/// <para>
/// Collections concretes (<see cref="long"/>[]), pas <c>IReadOnlyList&lt;T&gt;</c> : ce type ne
/// traverse jamais que la frontiere HTTP JSON d'un worker vers le maitre, sans etre manipule
/// ailleurs comme un objet-valeur Domain classique — la protection en lecture seule n'apporte
/// rien ici, alors qu'une interface de lecture seule empecherait la desserialisation directe.
/// </para>
/// </summary>
public sealed record HistogramSnapshot(
    long[] Buckets,
    long TotalCount,
    long SumMicroseconds,
    long MinMicroseconds,
    long MaxMicroseconds)
{
    /// <summary>Distribution vide, pour une etape sans aucune mesure.</summary>
    public static readonly HistogramSnapshot Empty = new([], 0L, 0L, 0L, 0L);
}