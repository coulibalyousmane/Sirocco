using System.Text;

namespace Tempest.Domain.Metrics;

/// <summary>
/// Rapport complet d'un tir a <b>scenarios concurrents</b> : plusieurs scenarios, chacun avec son
/// propre profil de charge, ses propres etiquettes et ses propres seuils, joues dans le meme
/// processus mais isoles les uns des autres jusque dans leur registre d'etapes — deux scenarios
/// qui declarent tous les deux une etape "login" produisent deux lignes distinctes, jamais une
/// seule fusionnee.
/// </summary>
public sealed record MultiScenarioReport
{
    /// <summary>Un rapport par scenario, dans l'ordre de declaration.</summary>
    public required IReadOnlyList<ScenarioReport> Scenarios { get; init; }

    /// <summary>Vrai si aucun scenario n'a perdu de mesure.</summary>
    public bool IsTrustworthy => Scenarios.All(static scenario => scenario.Report.IsTrustworthy);

    /// <summary>Vrai si les seuils de tous les scenarios sont respectes (vrai par defaut si aucun n'en declare).</summary>
    public bool ThresholdsPassed => Scenarios.All(static scenario => scenario.Thresholds.Passed);

    /// <summary>Rend le rapport de chaque scenario sous forme de tableau lisible en console, l'un apres l'autre.</summary>
    public string ToTable()
    {
        StringBuilder builder = new();

        foreach (ScenarioReport scenario in Scenarios)
        {
            builder.AppendLine($"=== Scenario : {scenario.Name} ===");
            builder.Append(scenario.Report.ToTable());
            builder.AppendLine(scenario.Thresholds.ToTable());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rend un unique document HTML autonome, une <c>section</c> par scenario — jamais un document
    /// par scenario, ce qui imbriquerait des balises <c>html</c>/<c>head</c> invalides.
    /// </summary>
    public string ToHtml()
    {
        const string HEADING = "Rapport Tempest — scenarios concurrents";

        StringBuilder html = new();
        LoadTestReport.AppendHtmlShellStart(html, HEADING);
        html.AppendLine($"<h1>{HEADING}</h1>");

        foreach (ScenarioReport scenario in Scenarios)
        {
            html.AppendLine("<section>");
            scenario.Report.AppendBodyContent(html, scenario.Thresholds, scenario.Name);
            html.AppendLine("</section>");
        }

        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }
}