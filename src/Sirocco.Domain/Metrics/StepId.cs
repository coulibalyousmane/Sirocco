namespace Sirocco.Domain.Metrics;

/// <summary>
/// Identifiant numerique d'une etape de scenario.
/// <para>
/// On ne transporte jamais le nom textuel d'une etape dans <see cref="MetricResult"/> :
/// une reference <see cref="string"/> ferait de la structure un type "managed",
/// ce qui obligerait le GC a scanner chaque element du buffer de metriques.
/// L'identifiant est resolu une seule fois au demarrage via <see cref="StepRegistry"/>.
/// </para>
/// </summary>
/// <param name="Value">Index dense, attribue sequentiellement par le registre.</param>
public readonly record struct StepId(int Value)
{
    /// <summary>Identifiant non attribue.</summary>
    public static readonly StepId None = new(-1);

    /// <summary>Indique si l'identifiant a ete attribue par un registre.</summary>
    public bool IsValid => Value >= 0;

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}