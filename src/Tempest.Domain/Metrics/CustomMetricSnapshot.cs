namespace Tempest.Domain.Metrics;

/// <summary>
/// Photographie agrégée d'une métrique personnalisée, une entrée par métrique déclarée.
/// <para>
/// Contrairement à <see cref="StepStatistics"/>, une seule forme sert les quatre types
/// (<see cref="CustomMetricKind"/>) : c'est au lecteur (rapport, Prometheus) d'interpréter les
/// champs selon <see cref="Kind"/> — <see cref="Sum"/> pour un compteur, <see cref="Last"/> pour
/// une jauge, <c>Sum / Count</c> pour un taux, <see cref="Min"/>/<see cref="Mean"/>/<see cref="Max"/>
/// pour une tendance. Pas de centiles pour la tendance en v1 : une distribution à plage
/// arbitraire (positive ou négative, sans borne connue à l'avance) ne peut pas réutiliser
/// <see cref="LatencyHistogram"/>, conçu pour une durée non négative bornée — un histogramme
/// dédié resterait à construire si le besoin de centiles se confirmait.
/// </para>
/// </summary>
public sealed record CustomMetricSnapshot
{
    /// <summary>Nom de la métrique, tel que déclaré dans le scénario.</summary>
    public required string Name { get; init; }

    /// <summary>Type de la métrique.</summary>
    public required CustomMetricKind Kind { get; init; }

    /// <summary>Nombre de valeurs enregistrées.</summary>
    public required long Count { get; init; }

    /// <summary>Somme de toutes les valeurs enregistrées.</summary>
    public required double Sum { get; init; }

    /// <summary>Valeur minimale observée (0 si <see cref="Count"/> vaut 0).</summary>
    public required double Min { get; init; }

    /// <summary>Valeur maximale observée (0 si <see cref="Count"/> vaut 0).</summary>
    public required double Max { get; init; }

    /// <summary>Dernière valeur enregistrée (0 si <see cref="Count"/> vaut 0).</summary>
    public required double Last { get; init; }

    /// <summary>Moyenne des valeurs enregistrées (0 si <see cref="Count"/> vaut 0).</summary>
    public double Mean => Count > 0 ? Sum / Count : 0d;

    /// <summary>Photographie vide, pour une métrique déclarée mais jamais alimentée.</summary>
    public static CustomMetricSnapshot Empty(string name, CustomMetricKind kind) => new()
    {
        Name = name,
        Kind = kind,
        Count = 0L,
        Sum = 0d,
        Min = 0d,
        Max = 0d,
        Last = 0d,
    };
}