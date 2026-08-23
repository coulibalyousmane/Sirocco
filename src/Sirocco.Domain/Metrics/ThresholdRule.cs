namespace Sirocco.Domain.Metrics;

/// <summary>
/// Regle de succes/echec evaluee en fin de tir : une grandeur, une comparaison, une limite.
/// <para>
/// C'est la brique qui transforme un rapport en verdict binaire pour un pipeline CI/CD : sans
/// seuil, un pipeline ne peut que journaliser des chiffres, jamais echouer automatiquement sur
/// une regression de performance.
/// </para>
/// </summary>
public sealed record ThresholdRule
{
    /// <summary>Etape sur laquelle porte le seuil (voir <see cref="WellKnownSteps.ITERATION"/> pour le parcours complet).</summary>
    public required string StepName { get; init; }

    /// <summary>Grandeur mesuree.</summary>
    public required ThresholdMetric Metric { get; init; }

    /// <summary>Comparaison appliquee entre la valeur observee et <see cref="Limit"/>.</summary>
    public required ThresholdComparison Comparison { get; init; }

    /// <summary>Limite a respecter.</summary>
    public required double Limit { get; init; }

    /// <summary>Libelle facultatif, pour un rapport plus lisible qu'une description auto-generee.</summary>
    public string? Name { get; init; }

    /// <summary>Regle portant sur l'etape technique d'iteration, le parcours complet.</summary>
    public static ThresholdRule OnIteration(
        ThresholdMetric metric,
        ThresholdComparison comparison,
        double limit,
        string? name = null) =>
        new()
        {
            StepName = WellKnownSteps.ITERATION,
            Metric = metric,
            Comparison = comparison,
            Limit = limit,
            Name = name,
        };

    /// <summary>Regle portant sur une etape nommee du scenario.</summary>
    public static ThresholdRule OnStep(
        string stepName,
        ThresholdMetric metric,
        ThresholdComparison comparison,
        double limit,
        string? name = null) =>
        new()
        {
            StepName = stepName,
            Metric = metric,
            Comparison = comparison,
            Limit = limit,
            Name = name,
        };

    /// <summary>
    /// Evalue la regle contre un rapport.
    /// <para>
    /// Une etape introuvable est un <b>echec</b>, pas un succes par defaut : voir
    /// <see cref="ThresholdEvaluation.StepNotFound"/>.
    /// </para>
    /// </summary>
    public ThresholdEvaluation Evaluate(LoadTestReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        StepStatistics? step = FindStep(report);
        if (step is null)
        {
            return ThresholdEvaluation.StepNotFound(this);
        }

        double actual = ExtractValue(step);

        return new ThresholdEvaluation
        {
            Rule = this,
            StepFound = true,
            ActualValue = actual,
            Passed = Satisfies(actual),
        };
    }

    /// <summary>Description lisible de la regle, pour les journaux et les rapports.</summary>
    public string Describe() => Name ?? $"{StepName}: {Metric} {SymbolOf(Comparison)} {Limit}";

    private StepStatistics? FindStep(LoadTestReport report)
    {
        foreach (StepStatistics step in report.Steps)
        {
            if (string.Equals(step.Name, StepName, StringComparison.Ordinal))
            {
                return step;
            }
        }

        return null;
    }

    private double ExtractValue(StepStatistics step) => Metric switch
    {
        ThresholdMetric.ResponseP50Milliseconds => step.Response.P50Milliseconds,
        ThresholdMetric.ResponseP75Milliseconds => step.Response.P75Milliseconds,
        ThresholdMetric.ResponseP90Milliseconds => step.Response.P90Milliseconds,
        ThresholdMetric.ResponseP95Milliseconds => step.Response.P95Milliseconds,
        ThresholdMetric.ResponseP99Milliseconds => step.Response.P99Milliseconds,
        ThresholdMetric.ResponseP999Milliseconds => step.Response.P999Milliseconds,
        ThresholdMetric.ResponseMaxMilliseconds => step.Response.MaxMilliseconds,
        ThresholdMetric.ResponseMeanMilliseconds => step.Response.MeanMilliseconds,
        ThresholdMetric.ErrorRate => step.ErrorRate,
        ThresholdMetric.SchedulingDelayMaxMilliseconds => step.MaxSchedulingDelayMilliseconds,
        ThresholdMetric.Count => step.Count,

        // Garde defensive : Metric et Comparison sont des donnees deja construites, pas des
        // parametres de cette methode — une valeur hors domaine signale un enum etendu sans
        // mettre a jour ce commutateur, pas un appel invalide. ArgumentOutOfRangeException
        // n'aurait ici aucun parametre reel a designer.
        _ => throw new InvalidOperationException($"Grandeur de seuil inconnue : {Metric}."),
    };

    private bool Satisfies(double actual) => Comparison switch
    {
        ThresholdComparison.LessThan => actual < Limit,
        ThresholdComparison.LessThanOrEqual => actual <= Limit,
        ThresholdComparison.GreaterThan => actual > Limit,
        ThresholdComparison.GreaterThanOrEqual => actual >= Limit,
        _ => throw new InvalidOperationException($"Comparaison de seuil inconnue : {Comparison}."),
    };

    private static string SymbolOf(ThresholdComparison comparison) => comparison switch
    {
        ThresholdComparison.LessThan => "<",
        ThresholdComparison.LessThanOrEqual => "<=",
        ThresholdComparison.GreaterThan => ">",
        ThresholdComparison.GreaterThanOrEqual => ">=",
        _ => "?",
    };
}