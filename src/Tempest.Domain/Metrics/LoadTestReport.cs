using System.Net;
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

    /// <summary>
    /// Rend le rapport en une page HTML autonome (CSS et contenu inline, aucune ressource
    /// externe) : ouvrable directement dans un navigateur, sans serveur ni connexion reseau.
    /// <para>
    /// Les noms d'etape viennent en definitive d'un fichier de scenario declaratif — potentiellement
    /// ecrit par quelqu'un d'autre que l'operateur qui ouvre ce rapport — donc echappes via
    /// <see cref="WebUtility.HtmlEncode(string?)"/> avant insertion, comme toute donnee qui
    /// traverse une frontiere de confiance.
    /// </para>
    /// </summary>
    /// <param name="thresholds">Verdict des seuils a inclure, si des seuils ont ete configures.</param>
    public string ToHtml(ThresholdReport? thresholds = null)
    {
        StringBuilder html = new();

        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"fr\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine($"<title>Rapport Tempest — {WebUtility.HtmlEncode(Scope.ToString())}</title>");
        html.AppendLine("""
            <style>
              body { font-family: system-ui, sans-serif; margin: 2rem; color: #1a1a1a; background: #fff; }
              h1 { font-size: 1.4rem; margin-bottom: 0.25rem; }
              .subtitle { color: #555; margin-top: 0; }
              .warning { background: #fff3cd; border: 1px solid #ffe69c; border-radius: 6px; padding: 0.75rem 1rem; margin: 1rem 0; }
              table { border-collapse: collapse; width: 100%; margin: 1rem 0; }
              th, td { border: 1px solid #ddd; padding: 0.5rem 0.75rem; text-align: right; }
              th:first-child, td:first-child { text-align: left; }
              th { background: #f5f5f5; }
              tr:nth-child(even) { background: #fafafa; }
              .pass { color: #1a7f37; font-weight: 600; }
              .fail { color: #cf222e; font-weight: 600; }
              .verdict { font-size: 1.1rem; margin-top: 1.5rem; }
            </style>
            """);
        html.AppendLine("</head>");
        html.AppendLine("<body>");

        html.AppendLine($"<h1>Rapport Tempest — {WebUtility.HtmlEncode(Scope.ToString())}</h1>");
        html.AppendLine(FormattableString.Invariant(
            $"<p class=\"subtitle\">{Duration.TotalSeconds:F1} s — {IterationsPerSecond:N0} iterations/s</p>"));

        if (!IsTrustworthy)
        {
            html.AppendLine(
                $"<div class=\"warning\">/!\\ {MetricsDropped:N0} mesures perdues : les centiles ci-dessous sont incomplets.</div>");
        }

        html.AppendLine("<table>");
        html.AppendLine("<thead><tr><th>Etape</th><th>n</th><th>Echecs</th><th>p50</th><th>p95</th><th>p99</th><th>p99 brut</th></tr></thead>");
        html.AppendLine("<tbody>");

        foreach (StepStatistics step in Steps)
        {
            html.AppendLine(FormattableString.Invariant($"""
                <tr>
                  <td>{WebUtility.HtmlEncode(step.Name)}</td>
                  <td>{step.Count:N0}</td>
                  <td>{step.ErrorRate:P1}</td>
                  <td>{step.Response.P50Milliseconds:F2} ms</td>
                  <td>{step.Response.P95Milliseconds:F2} ms</td>
                  <td>{step.Response.P99Milliseconds:F2} ms</td>
                  <td>{step.Service.P99Milliseconds:F2} ms</td>
                </tr>
                """));
        }

        html.AppendLine("</tbody>");
        html.AppendLine("</table>");

        if (thresholds is not null)
        {
            AppendThresholds(html, thresholds);
        }

        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    private static void AppendThresholds(StringBuilder html, ThresholdReport thresholds)
    {
        if (thresholds.Evaluations.Count == 0)
        {
            html.AppendLine("<p class=\"verdict\">Aucun seuil configure.</p>");
            return;
        }

        html.AppendLine(
            $"""<p class="verdict {(thresholds.Passed ? "pass" : "fail")}">Seuils : {(thresholds.Passed ? "tous respectes." : "au moins un echec.")}</p>""");

        html.AppendLine("<ul>");
        foreach (ThresholdEvaluation evaluation in thresholds.Evaluations)
        {
            html.AppendLine(
                $"""<li class="{(evaluation.Passed ? "pass" : "fail")}">{WebUtility.HtmlEncode(evaluation.ToString())}</li>""");
        }

        html.AppendLine("</ul>");
    }
}