namespace Sirocco.Domain.Metrics;

/// <summary>Relation testee entre la valeur observee et la limite d'un <see cref="ThresholdRule"/>.</summary>
public enum ThresholdComparison
{
    /// <summary>La valeur observee doit etre strictement inferieure a la limite.</summary>
    LessThan,

    /// <summary>La valeur observee doit etre inferieure ou egale a la limite.</summary>
    LessThanOrEqual,

    /// <summary>La valeur observee doit etre strictement superieure a la limite.</summary>
    GreaterThan,

    /// <summary>La valeur observee doit etre superieure ou egale a la limite.</summary>
    GreaterThanOrEqual,
}