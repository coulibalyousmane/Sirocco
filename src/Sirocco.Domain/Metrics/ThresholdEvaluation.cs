namespace Sirocco.Domain.Metrics;

/// <summary>Resultat de l'evaluation d'une <see cref="ThresholdRule"/> contre un rapport.</summary>
public sealed record ThresholdEvaluation
{
    /// <summary>Regle evaluee.</summary>
    public required ThresholdRule Rule { get; init; }

    /// <summary>Verdict de la regle.</summary>
    public required bool Passed { get; init; }

    /// <summary>Indique si l'etape ciblee par la regle existait dans le rapport.</summary>
    public required bool StepFound { get; init; }

    /// <summary>Valeur observee, ou <see langword="null"/> si l'etape est introuvable.</summary>
    public double? ActualValue { get; init; }

    /// <summary>
    /// Construit l'evaluation d'une regle dont l'etape n'existe pas dans le rapport.
    /// <para>
    /// Toujours en echec : une regle mal configuree (nom d'etape errone, faute de frappe) doit
    /// se voir dans le verdict, pas disparaitre silencieusement dans un pipeline qui continue
    /// de passer au vert.
    /// </para>
    /// </summary>
    public static ThresholdEvaluation StepNotFound(ThresholdRule rule) => new()
    {
        Rule = rule,
        Passed = false,
        StepFound = false,
        ActualValue = null,
    };

    /// <inheritdoc />
    public override string ToString() =>
        StepFound
            ? $"[{(Passed ? "OK" : "ECHEC")}] {Rule.Describe()} (observe : {ActualValue:F2})"
            : $"[ECHEC] {Rule.Describe()} — etape '{Rule.StepName}' introuvable dans le rapport";
}