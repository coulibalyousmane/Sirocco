using System.Text;

namespace Tempest.Domain.Metrics;

/// <summary>Verdict global de l'evaluation d'un ensemble de <see cref="ThresholdRule"/>.</summary>
public sealed record ThresholdReport
{
    /// <summary>Evaluation de chaque regle, dans l'ordre fourni.</summary>
    public required IReadOnlyList<ThresholdEvaluation> Evaluations { get; init; }

    /// <summary>
    /// Verdict global. Vrai par defaut sur une liste vide : l'absence de seuil configure
    /// n'est pas un echec, juste l'absence de gate.
    /// </summary>
    public bool Passed => Evaluations.All(static evaluation => evaluation.Passed);

    /// <summary>Regles en echec, dans l'ordre fourni.</summary>
    public IEnumerable<ThresholdEvaluation> Failures => Evaluations.Where(static evaluation => !evaluation.Passed);

    /// <summary>Evalue un ensemble de regles contre un rapport.</summary>
    public static ThresholdReport Evaluate(IReadOnlyList<ThresholdRule> rules, LoadTestReport report)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(report);

        ThresholdEvaluation[] evaluations = new ThresholdEvaluation[rules.Count];
        for (int i = 0; i < rules.Count; i++)
        {
            evaluations[i] = rules[i].Evaluate(report);
        }

        return new ThresholdReport { Evaluations = evaluations };
    }

    /// <summary>Rend le verdict sous forme de texte lisible en console ou en journal.</summary>
    public string ToTable()
    {
        if (Evaluations.Count == 0)
        {
            return "Aucun seuil configure.";
        }

        StringBuilder builder = new();
        builder.AppendLine(Passed ? "Seuils : tous respectes." : "Seuils : au moins un echec.");

        foreach (ThresholdEvaluation evaluation in Evaluations)
        {
            builder.AppendLine($"  {evaluation}");
        }

        return builder.ToString();
    }
}