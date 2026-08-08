namespace Tempest.Domain.Metrics;

/// <summary>
/// Identifiant numérique d'une métrique personnalisée. Même raisonnement que <see cref="StepId"/> :
/// un index dense résolu une seule fois via <see cref="CustomMetricRegistry"/>, jamais un nom
/// textuel, pour que <see cref="CustomMetricResult"/> reste une structure non managée.
/// </summary>
/// <param name="Value">Index dense, attribué séquentiellement par le registre.</param>
public readonly record struct CustomMetricId(int Value)
{
    /// <summary>Identifiant non attribué.</summary>
    public static readonly CustomMetricId None = new(-1);

    /// <summary>Indique si l'identifiant a été attribué par un registre.</summary>
    public bool IsValid => Value >= 0;

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}