using System.Net;
using System.Text;

namespace Sirocco.Domain.Metrics;

/// <summary>
/// Ecart d'une etape entre deux rapports : un tir de reference (<see cref="Baseline"/>) et le
/// tir courant (<see cref="Current"/>). L'un des deux peut manquer — une etape ajoutee ou
/// retiree entre les deux tirs n'est pas une erreur, juste un ecart a signaler comme tel.
/// </summary>
public sealed record StepComparison
{
    /// <summary>Nom de l'etape.</summary>
    public required string Name { get; init; }

    /// <summary>Statistiques du tir de reference, ou <see langword="null"/> si l'etape n'existait pas encore.</summary>
    public StepStatistics? Baseline { get; init; }

    /// <summary>Statistiques du tir courant, ou <see langword="null"/> si l'etape a disparu.</summary>
    public StepStatistics? Current { get; init; }

    /// <summary>Ecart de p95 (temps de reponse corrige) en millisecondes, ou <see langword="null"/> si l'un des deux cotes manque.</summary>
    public double? P95DeltaMilliseconds =>
        Baseline is not null && Current is not null
            ? Current.Response.P95Milliseconds - Baseline.Response.P95Milliseconds
            : null;

    /// <summary>
    /// Ecart de p95 en proportion de la reference (0.20 = +20 %), ou <see langword="null"/> si
    /// l'un des deux cotes manque ou si la reference est nulle (division par zero evitee).
    /// </summary>
    public double? P95DeltaPercent =>
        Baseline is not null && Current is not null && Baseline.Response.P95Milliseconds > 0d
            ? (Current.Response.P95Milliseconds - Baseline.Response.P95Milliseconds) / Baseline.Response.P95Milliseconds
            : null;

    /// <summary>Ecart de taux d'erreur en points (0.05 = +5 points), ou <see langword="null"/> si l'un des deux cotes manque.</summary>
    public double? ErrorRateDelta =>
        Baseline is not null && Current is not null
            ? Current.ErrorRate - Baseline.ErrorRate
            : null;
}

/// <summary>
/// Compare deux rapports de tir, etape par etape : la brique qui transforme deux photographies
/// isolees en une question repondable — "a-t-on regresse depuis la reference ?"
/// </summary>
public sealed record LoadTestReportComparison
{
    /// <summary>Ecart de chaque etape, dans l'ordre du tir courant puis les etapes disparues.</summary>
    public required IReadOnlyList<StepComparison> Steps { get; init; }

    /// <summary>
    /// Compare deux rapports. Les etapes sont appariees par nom : une etape absente d'un des
    /// deux cotes est signalee (<see cref="StepComparison.Baseline"/> ou
    /// <see cref="StepComparison.Current"/> a <see langword="null"/>), jamais ignoree en silence.
    /// </summary>
    public static LoadTestReportComparison Compare(LoadTestReport baseline, LoadTestReport current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        Dictionary<string, StepStatistics> baselineByName = new(StringComparer.Ordinal);
        foreach (StepStatistics step in baseline.Steps)
        {
            baselineByName[step.Name] = step;
        }

        Dictionary<string, StepStatistics> currentByName = new(StringComparer.Ordinal);
        foreach (StepStatistics step in current.Steps)
        {
            currentByName[step.Name] = step;
        }

        List<string> orderedNames = [.. current.Steps.Select(static step => step.Name)];
        foreach (StepStatistics step in baseline.Steps)
        {
            if (!currentByName.ContainsKey(step.Name))
            {
                orderedNames.Add(step.Name);
            }
        }

        List<StepComparison> comparisons = new(orderedNames.Count);
        foreach (string name in orderedNames)
        {
            comparisons.Add(new StepComparison
            {
                Name = name,
                Baseline = baselineByName.GetValueOrDefault(name),
                Current = currentByName.GetValueOrDefault(name),
            });
        }

        return new LoadTestReportComparison { Steps = comparisons };
    }

    /// <summary>
    /// Plus grande regression de p95 parmi les etapes presentes des deux cotes, en proportion
    /// de la reference (0.20 = +20 %) — <see langword="null"/> si aucune etape n'est comparable.
    /// Une valeur negative signifie que la pire etape s'est en realite amelioree.
    /// </summary>
    public double? WorstP95RegressionPercent()
    {
        double? worst = null;
        foreach (StepComparison step in Steps)
        {
            if (step.P95DeltaPercent is double delta && (worst is null || delta > worst))
            {
                worst = delta;
            }
        }

        return worst;
    }

    /// <summary>Rend la comparaison sous forme de tableau lisible en console.</summary>
    public string ToTable()
    {
        StringBuilder builder = new();
        builder.AppendLine("Comparaison — reference vs actuel");
        builder.AppendLine(
            $"  {"etape",-24} {"p95 ref",10} {"p95 actuel",11} {"delta p95",11} {"echecs ref",11} {"echecs actuel",14}");

        foreach (StepComparison step in Steps)
        {
            builder.AppendLine(
                $"  {step.Name,-24} {FormatMilliseconds(step.Baseline),10} {FormatMilliseconds(step.Current),11} " +
                $"{FormatPercentDelta(step.P95DeltaPercent),11} {FormatErrorRate(step.Baseline),11} {FormatErrorRate(step.Current),14}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rend la comparaison en page HTML autonome (memes conventions que
    /// <see cref="LoadTestReport.ToHtml"/> : CSS inline, noms d'etape echappes).
    /// </summary>
    public string ToHtml()
    {
        StringBuilder html = new();

        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"fr\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<title>Comparaison Sirocco — reference vs actuel</title>");
        html.AppendLine("""
            <style>
              body { font-family: system-ui, sans-serif; margin: 2rem; color: #1a1a1a; background: #fff; }
              h1 { font-size: 1.4rem; margin-bottom: 1rem; }
              table { border-collapse: collapse; width: 100%; margin: 1rem 0; }
              th, td { border: 1px solid #ddd; padding: 0.5rem 0.75rem; text-align: right; }
              th:first-child, td:first-child { text-align: left; }
              th { background: #f5f5f5; }
              tr:nth-child(even) { background: #fafafa; }
              .regression { color: #cf222e; font-weight: 600; }
              .improvement { color: #1a7f37; font-weight: 600; }
              .missing { color: #999; font-style: italic; }
            </style>
            """);
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<h1>Comparaison — reference vs actuel</h1>");

        html.AppendLine("<table>");
        html.AppendLine(
            "<thead><tr><th>Etape</th><th>p95 ref</th><th>p95 actuel</th><th>&Delta; p95</th>" +
            "<th>Echecs ref</th><th>Echecs actuel</th></tr></thead>");
        html.AppendLine("<tbody>");

        foreach (StepComparison step in Steps)
        {
            string deltaClass = step.P95DeltaPercent switch
            {
                > 0d => "regression",
                < 0d => "improvement",
                _ => "",
            };

            html.AppendLine(FormattableString.Invariant($"""
                <tr>
                  <td>{WebUtility.HtmlEncode(step.Name)}</td>
                  <td>{FormatMilliseconds(step.Baseline)}</td>
                  <td>{FormatMilliseconds(step.Current)}</td>
                  <td class="{deltaClass}">{FormatPercentDelta(step.P95DeltaPercent)}</td>
                  <td>{FormatErrorRate(step.Baseline)}</td>
                  <td>{FormatErrorRate(step.Current)}</td>
                </tr>
                """));
        }

        html.AppendLine("</tbody>");
        html.AppendLine("</table>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    private static string FormatMilliseconds(StepStatistics? step) =>
        step is not null ? FormattableString.Invariant($"{step.Response.P95Milliseconds:F2} ms") : "—";

    private static string FormatErrorRate(StepStatistics? step) =>
        step is not null ? step.ErrorRate.ToString("P1") : "—";

    private static string FormatPercentDelta(double? delta) =>
        delta is double value ? FormattableString.Invariant($"{value:+0.0%;-0.0%;+0.0%}") : "—";
}