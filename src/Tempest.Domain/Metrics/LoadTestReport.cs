using System.Text;

namespace Tempest.Domain.Metrics;

/// <summary>
/// Photographie complete des statistiques d'un tir, toutes etapes confondues.
/// </summary>
public sealed record LoadTestReport
{
    /// <summary>Perimetre temporel couvert par ce rapport.</summary>
    public required StatisticsScope Scope { get; init; }

    /// <summary>Duree couverte par le rapport.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Statistiques detaillees, une entree par etape declaree.</summary>
    public required IReadOnlyList<StepStatistics> Steps { get; init; }

    /// <summary>
    /// Statistiques de l'etape technique <see cref="WellKnownSteps.ITERATION"/>, qui porte
    /// le temps de bout en bout d'un parcours utilisateur.
    /// </summary>
    public required StepStatistics Iteration { get; init; }

    /// <summary>Nombre de mesures perdues faute de place dans le canal.</summary>
    public required long MetricsDropped { get; init; }

    /// <summary>Debit moyen sur la periode, iterations par seconde.</summary>
    public double IterationsPerSecond =>
        Duration > TimeSpan.Zero ? Iteration.Count / Duration.TotalSeconds : 0d;

    /// <summary>
    /// Le rapport porte sur un echantillon incomplet : les centiles publies sont biaises
    /// et ne doivent pas servir de verdict.
    /// </summary>
    public bool IsTrustworthy => MetricsDropped == 0L;

    /// <summary>Rend le rapport sous forme de tableau lisible en console.</summary>
    public string ToTable()
    {
        StringBuilder builder = new();

        builder.AppendLine(
            $"Rapport {Scope} — {Duration.TotalSeconds:F1}s, {IterationsPerSecond:N0} iterations/s");

        if (!IsTrustworthy)
        {
            builder.AppendLine(
                $"  /!\\ {MetricsDropped} mesures perdues : les centiles ci-dessous sont incomplets.");
        }

        builder.AppendLine(
            $"  {"etape",-24} {"n",8} {"echecs",8} {"p50",11} {"p95",11} {"p99",11} {"p99 brut",11}");

        foreach (StepStatistics step in Steps)
        {
            builder.AppendLine(
                $"  {step.Name,-24} {step.Count,8:N0} {step.ErrorRate,7:P1} " +
                $"{step.Response.P50Milliseconds,9:F2}ms {step.Response.P95Milliseconds,9:F2}ms " +
                $"{step.Response.P99Milliseconds,9:F2}ms {step.Service.P99Milliseconds,9:F2}ms");
        }

        return builder.ToString();
    }
}