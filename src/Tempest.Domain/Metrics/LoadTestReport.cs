using System.Globalization;
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

    /// <summary>
    /// Etiquettes du scenario qui a produit ce rapport (ex. <c>région: eu-west</c>), reportees
    /// depuis <see cref="Execution.IWorkflow.Tags"/> — une metadonnee d'affichage, jamais utilisee
    /// pour l'agregation des metriques ci-dessus. Vide par defaut.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Metriques personnalisees du scenario (compteur/jauge/taux/tendance), agregees separement
    /// du tableau d'etapes ci-dessus — voir <see cref="CustomMetricSnapshot"/>. Vide par defaut.
    /// </summary>
    public IReadOnlyList<CustomMetricSnapshot> CustomMetrics { get; init; } = [];

    /// <summary>
    /// Ce tir a utilise le modele ferme (nombre fixe d'utilisateurs virtuels) plutot que le
    /// modele ouvert (debit cible). <see langword="false"/> par defaut.
    /// <para>
    /// En modele ferme, chaque jeton porte l'instant de sa propre emission, pas un instant
    /// planifie a l'avance : il n'y a donc pas de correction du <i>coordinated omission</i>, et
    /// les chiffres de ce rapport ne sont pas comparables a un tir en modele ouvert — d'ou la
    /// mise en garde explicite rendue par <see cref="ToTable"/> et <see cref="ToHtml"/>.
    /// </para>
    /// </summary>
    public bool ClosedModel { get; init; }

    /// <summary>
    /// Trajectoire du tir : un <see cref="TimeSeriesSample"/> par intervalle de releve, du debut a
    /// la fin — la difference entre un rapport qui ne dit que l'etat final et un qui montre
    /// comment on y est arrive. Vide par defaut : seul <c>Tempest.Host.LoadTestHostedService</c>
    /// la remplit aujourd'hui, pas <c>MultiScenarioRunner</c> (voir sa remarque de classe).
    /// </summary>
    public IReadOnlyList<TimeSeriesSample> TimeSeries { get; init; } = [];

    /// <summary>
    /// Workers (mode distribue) declares perdus en cours de tir plutot que d'avoir rapporte
    /// normalement — voir <c>Tempest.Host.Distributed.MasterCoordinator.MarkDeadIfStale</c>. Vide
    /// par defaut : sans effet en mode autonome, et un tir distribue nominal ou tous les workers
    /// rapportent laisse aussi ce champ vide. Non vide, il signale que ce rapport ne reflete
    /// qu'un sous-ensemble des workers dispatches — fusion partielle honnete, pas un rapport
    /// silencieusement incomplet.
    /// </summary>
    public IReadOnlyList<string> LostWorkers { get; init; } = [];

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

        if (Tags.Count > 0)
        {
            builder.AppendLine($"  etiquettes : {FormatTags()}");
        }

        if (!IsTrustworthy)
        {
            builder.AppendLine(
                $"  /!\\ {MetricsDropped} mesures perdues : les centiles ci-dessous sont incomplets.");
        }

        if (ClosedModel)
        {
            builder.AppendLine(
                "  /!\\ Modele ferme : pas de correction du coordinated omission, chiffres non comparables a un tir en modele ouvert.");
        }

        if (LostWorkers.Count > 0)
        {
            builder.AppendLine(
                $"  /!\\ {LostWorkers.Count} worker(s) perdu(s) en cours de tir : {string.Join(", ", LostWorkers)}.");
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

        if (TimeSeries.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  serie temporelle (fenetre glissante)");
            builder.AppendLine(
                $"  {"t",7} {"it/s",8} {"vus actifs",10} {"echecs",8} {"p50",9} {"p95",9} {"p99",9} {"dette max",10}");

            foreach (TimeSeriesSample sample in TimeSeries)
            {
                builder.AppendLine(
                    $"  {sample.ElapsedSeconds,6:F1}s {sample.IterationsPerSecond,8:N0} {sample.ActiveVirtualUsers,10:N0} " +
                    $"{sample.ErrorRate,7:P1} {sample.ResponseP50Milliseconds,7:F2}ms {sample.ResponseP95Milliseconds,7:F2}ms " +
                    $"{sample.ResponseP99Milliseconds,7:F2}ms {sample.MaxSchedulingDelayMilliseconds,8:F2}ms");
            }
        }

        if (CustomMetrics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  metriques personnalisees");
            builder.AppendLine($"  {"metrique",-24} {"type",-8} {"n",8}   valeur");

            foreach (CustomMetricSnapshot metric in CustomMetrics)
            {
                builder.AppendLine(
                    $"  {metric.Name,-24} {metric.Kind.ToString().ToLowerInvariant(),-8} {metric.Count,8:N0}   {FormatCustomMetricValue(metric, CultureInfo.CurrentCulture)}");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formate la valeur d'une metrique personnalisee selon son type : la somme pour un
    /// compteur, la derniere valeur pour une jauge, la fraction pour un taux, et les trois bornes
    /// utiles pour une tendance — sans centiles, voir <see cref="CustomMetricSnapshot"/>.
    /// </summary>
    private static string FormatCustomMetricValue(CustomMetricSnapshot metric, IFormatProvider culture) => metric.Kind switch
    {
        CustomMetricKind.Counter => metric.Sum.ToString("F2", culture),
        CustomMetricKind.Gauge => metric.Last.ToString("F2", culture),
        CustomMetricKind.Rate => metric.Mean.ToString("P1", culture),
        CustomMetricKind.Trend =>
            $"min {metric.Min.ToString("F2", culture)} / moy {metric.Mean.ToString("F2", culture)} / max {metric.Max.ToString("F2", culture)}",
        _ => string.Empty,
    };

    private string FormatTags()
    {
        StringBuilder tags = new();
        bool first = true;
        foreach ((string key, string value) in Tags)
        {
            if (!first)
            {
                tags.Append(", ");
            }

            tags.Append(key).Append('=').Append(value);
            first = false;
        }

        return tags.ToString();
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
    /// <param name="autoRefreshSeconds">
    /// Si renseigne, la page se recharge elle-meme a cet intervalle — c'est ce qui transforme ce
    /// rapport statique en tableau de bord temps reel (voir <c>StandaloneHost</c>,
    /// <c>/report/live.html</c>). <see langword="null"/> par defaut : comportement inchange, la
    /// page ne se rafraichit jamais seule.
    /// </param>
    public string ToHtml(ThresholdReport? thresholds = null, int? autoRefreshSeconds = null)
    {
        string heading = $"Rapport Tempest — {Scope}";

        StringBuilder html = new();
        AppendHtmlShellStart(html, heading, autoRefreshSeconds);
        AppendBodyContent(html, thresholds, heading);
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    /// <summary>
    /// Ouvre un document HTML autonome (doctype, <c>head</c>, style, balise <c>body</c>) — extrait
    /// de <see cref="ToHtml"/> pour etre reutilise par <see cref="MultiScenarioReport.ToHtml"/>, qui
    /// a besoin d'un unique document pour plusieurs rapports plutot que d'un par scenario (des
    /// <c>&lt;html&gt;</c>/<c>&lt;head&gt;</c> imbriques ne seraient pas un document valide).
    /// </summary>
    internal static void AppendHtmlShellStart(StringBuilder html, string titleText, int? autoRefreshSeconds = null)
    {
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"fr\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");

        if (autoRefreshSeconds is { } seconds)
        {
            html.AppendLine(FormattableString.Invariant($"<meta http-equiv=\"refresh\" content=\"{seconds}\">"));
        }

        html.AppendLine($"<title>{WebUtility.HtmlEncode(titleText)}</title>");
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
              section + section { margin-top: 2.5rem; border-top: 1px solid #ddd; padding-top: 1.5rem; }
            </style>
            """);
        html.AppendLine("</head>");
        html.AppendLine("<body>");
    }

    /// <summary>
    /// Rend le titre, les etiquettes, les mises en garde, le tableau des etapes, les metriques
    /// personnalisees et les seuils — tout ce qui, dans <see cref="ToHtml"/>, se trouve entre la
    /// balise <c>body</c> et sa fermeture. Extrait pour etre imbrique dans une <c>section</c> par
    /// <see cref="MultiScenarioReport.ToHtml"/>, un scenario a la fois.
    /// </summary>
    /// <param name="heading">
    /// Titre affiche (<c>h1</c>) : le perimetre du rapport pour un tir simple, le nom du scenario
    /// pour un tir a scenarios concurrents.
    /// </param>
    internal void AppendBodyContent(StringBuilder html, ThresholdReport? thresholds, string heading)
    {
        html.AppendLine($"<h1>{WebUtility.HtmlEncode(heading)}</h1>");
        html.AppendLine(FormattableString.Invariant(
            $"<p class=\"subtitle\">{Duration.TotalSeconds:F1} s — {IterationsPerSecond:N0} iterations/s</p>"));

        if (Tags.Count > 0)
        {
            html.AppendLine($"<p class=\"subtitle\">etiquettes : {WebUtility.HtmlEncode(FormatTags())}</p>");
        }

        if (!IsTrustworthy)
        {
            html.AppendLine(
                $"<div class=\"warning\">/!\\ {MetricsDropped:N0} mesures perdues : les centiles ci-dessous sont incomplets.</div>");
        }

        if (ClosedModel)
        {
            html.AppendLine(
                "<div class=\"warning\">/!\\ Modele ferme : pas de correction du coordinated omission, chiffres non comparables a un tir en modele ouvert.</div>");
        }

        if (LostWorkers.Count > 0)
        {
            html.AppendLine(
                $"<div class=\"warning\">/!\\ {LostWorkers.Count} worker(s) perdu(s) en cours de tir : {WebUtility.HtmlEncode(string.Join(", ", LostWorkers))}.</div>");
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

        AppendHistograms(html);

        if (TimeSeries.Count > 0)
        {
            html.AppendLine("<h2>Serie temporelle</h2>");

            string chart = BuildTimeSeriesChart(TimeSeries);
            if (chart.Length > 0)
            {
                html.AppendLine(chart);
            }

            html.AppendLine("<table>");
            html.AppendLine(
                "<thead><tr><th>t</th><th>it/s</th><th>VUs actifs</th><th>Echecs</th><th>p50</th><th>p95</th><th>p99</th><th>Dette max</th></tr></thead>");
            html.AppendLine("<tbody>");

            foreach (TimeSeriesSample sample in TimeSeries)
            {
                html.AppendLine(FormattableString.Invariant($"""
                    <tr>
                      <td>{sample.ElapsedSeconds:F1} s</td>
                      <td>{sample.IterationsPerSecond:N0}</td>
                      <td>{sample.ActiveVirtualUsers:N0}</td>
                      <td>{sample.ErrorRate:P1}</td>
                      <td>{sample.ResponseP50Milliseconds:F2} ms</td>
                      <td>{sample.ResponseP95Milliseconds:F2} ms</td>
                      <td>{sample.ResponseP99Milliseconds:F2} ms</td>
                      <td>{sample.MaxSchedulingDelayMilliseconds:F2} ms</td>
                    </tr>
                    """));
            }

            html.AppendLine("</tbody>");
            html.AppendLine("</table>");
        }

        if (CustomMetrics.Count > 0)
        {
            html.AppendLine("<h2>Metriques personnalisees</h2>");
            html.AppendLine("<table>");
            html.AppendLine("<thead><tr><th>Metrique</th><th>Type</th><th>n</th><th>Valeur</th></tr></thead>");
            html.AppendLine("<tbody>");

            foreach (CustomMetricSnapshot metric in CustomMetrics)
            {
                html.AppendLine(FormattableString.Invariant($"""
                    <tr>
                      <td>{WebUtility.HtmlEncode(metric.Name)}</td>
                      <td>{metric.Kind.ToString().ToLowerInvariant()}</td>
                      <td>{metric.Count:N0}</td>
                      <td>{FormatCustomMetricValue(metric, CultureInfo.InvariantCulture)}</td>
                    </tr>
                    """));
            }

            html.AppendLine("</tbody>");
            html.AppendLine("</table>");
        }

        if (thresholds is not null)
        {
            AppendThresholds(html, thresholds);
        }
    }

    // Dimensions fixes du graphe : coherentes avec la largeur des tableaux ci-dessus (viewBox
    // SVG, donc redimensionne proprement par le navigateur quel que soit l'ecran).
    private const int HISTOGRAM_CHART_WIDTH = 700;
    private const int HISTOGRAM_CHART_HEIGHT = 140;

    /// <summary>
    /// Rend, pour chaque etape ayant au moins une mesure, la distribution de son temps de
    /// reponse corrige — le graphe que <c>ToTable</c> ne peut pas offrir, et que les seuls
    /// centiles du tableau ci-dessus ne montrent pas : une distribution bimodale et un p95 propre
    /// se ressemblent en centiles, jamais en histogramme.
    /// </summary>
    private void AppendHistograms(StringBuilder html)
    {
        List<(string Name, string Svg)> charts = [];
        foreach (StepStatistics step in Steps)
        {
            string svg = BuildHistogramChart(step.ResponseHistogram);
            if (svg.Length > 0)
            {
                charts.Add((step.Name, svg));
            }
        }

        if (charts.Count == 0)
        {
            return;
        }

        html.AppendLine("<h2>Distribution des temps de reponse</h2>");
        foreach ((string name, string svg) in charts)
        {
            html.AppendLine($"<h3>{WebUtility.HtmlEncode(name)}</h3>");
            html.AppendLine(svg);
        }
    }

    /// <summary>
    /// Rend un histogramme en barres a partir des paniers bruts d'un <see cref="HistogramSnapshot"/>.
    /// <para>
    /// Les 3072 paniers de <see cref="LatencyHistogram"/> sont bien trop fins pour un graphe
    /// lisible : on les regroupe par octave (<see cref="LatencyHistogram.PRECISION_BITS"/> bits,
    /// soit le decoupage natif de l'histogramme lui-meme, pas une resolution inventee ici), puis
    /// on ne garde que les octaves qui couvrent reellement la distribution observee — le reste du
    /// tableau resterait a zero et n'ajouterait que du vide au graphe.
    /// </para>
    /// </summary>
    private static string BuildHistogramChart(HistogramSnapshot histogram)
    {
        if (histogram.TotalCount == 0L)
        {
            return string.Empty;
        }

        const int groupBits = LatencyHistogram.PRECISION_BITS;
        int groupCount = LatencyHistogram.BucketCount >> groupBits;
        long[] groupCounts = new long[groupCount];

        for (int i = 0; i < histogram.Buckets.Length; i++)
        {
            groupCounts[i >> groupBits] += histogram.Buckets[i];
        }

        int first = 0;
        while (first < groupCount && groupCounts[first] == 0L)
        {
            first++;
        }

        int last = groupCount - 1;
        while (last > first && groupCounts[last] == 0L)
        {
            last--;
        }

        int visibleCount = last - first + 1;
        long maxCount = 0L;
        for (int i = first; i <= last; i++)
        {
            maxCount = Math.Max(maxCount, groupCounts[i]);
        }

        double barWidth = HISTOGRAM_CHART_WIDTH / (double)visibleCount;

        StringBuilder svg = new();
        svg.Append(FormattableString.Invariant(
            $"""<svg viewBox="0 0 {HISTOGRAM_CHART_WIDTH} {HISTOGRAM_CHART_HEIGHT + 22}" width="100%">"""));

        for (int i = 0; i < visibleCount; i++)
        {
            int group = first + i;
            long count = groupCounts[group];
            double height = count / (double)maxCount * HISTOGRAM_CHART_HEIGHT;
            double x = i * barWidth;
            double y = HISTOGRAM_CHART_HEIGHT - height;
            double upperBoundMs = LatencyHistogram.UpperBoundOf(((group + 1) << groupBits) - 1) / 1_000d;

            svg.Append(FormattableString.Invariant($"""
                <rect x="{x:F1}" y="{y:F1}" width="{Math.Max(barWidth - 1d, 0.5d):F1}" height="{Math.Max(height, 0.5d):F1}" fill="#0969da"><title>jusqu'a {upperBoundMs:F1} ms : {count:N0}</title></rect>
                """));
        }

        svg.Append(FormattableString.Invariant($"""
            <text x="0" y="{HISTOGRAM_CHART_HEIGHT + 16}" font-size="11" fill="#555">{histogram.MinMicroseconds / 1_000d:F1} ms</text>
            <text x="{HISTOGRAM_CHART_WIDTH}" y="{HISTOGRAM_CHART_HEIGHT + 16}" text-anchor="end" font-size="11" fill="#555">{histogram.MaxMicroseconds / 1_000d:F1} ms</text>
            """));

        svg.Append("</svg>");
        return svg.ToString();
    }

    private const int TIME_SERIES_CHART_WIDTH = 700;
    private const int TIME_SERIES_CHART_HEIGHT = 160;

    /// <summary>
    /// Rend la trajectoire du tir en graphe : debit et dette d'ordonnancement superposes sur le
    /// meme axe des temps, chacun mis a l'echelle de son propre maximum (leurs unites n'ont rien
    /// de comparable).
    /// <para>
    /// C'est le graphe que ni <c>ToTable</c> ni un outil qui ne corrige pas le
    /// <i>coordinated omission</i> ne peuvent produire : une dette qui grimpe pendant que le
    /// debit stagne ou chute est la signature visuelle d'un injecteur ou d'une cible saturee —
    /// invisible dans un simple tableau de centiles.
    /// </para>
    /// </summary>
    private static string BuildTimeSeriesChart(IReadOnlyList<TimeSeriesSample> series)
    {
        if (series.Count < 2)
        {
            return string.Empty;
        }

        double maxElapsed = series[^1].ElapsedSeconds;
        double maxThroughput = 0d;
        double maxDebt = 0d;
        foreach (TimeSeriesSample sample in series)
        {
            maxThroughput = Math.Max(maxThroughput, sample.IterationsPerSecond);
            maxDebt = Math.Max(maxDebt, sample.MaxSchedulingDelayMilliseconds);
        }

        StringBuilder throughputPoints = new();
        StringBuilder debtPoints = new();

        foreach (TimeSeriesSample sample in series)
        {
            double x = maxElapsed <= 0d ? 0d : sample.ElapsedSeconds / maxElapsed * TIME_SERIES_CHART_WIDTH;
            double throughputY = maxThroughput <= 0d
                ? TIME_SERIES_CHART_HEIGHT
                : TIME_SERIES_CHART_HEIGHT - (sample.IterationsPerSecond / maxThroughput * TIME_SERIES_CHART_HEIGHT);
            double debtY = maxDebt <= 0d
                ? TIME_SERIES_CHART_HEIGHT
                : TIME_SERIES_CHART_HEIGHT - (sample.MaxSchedulingDelayMilliseconds / maxDebt * TIME_SERIES_CHART_HEIGHT);

            throughputPoints.Append(FormattableString.Invariant($"{x:F1},{throughputY:F1} "));
            debtPoints.Append(FormattableString.Invariant($"{x:F1},{debtY:F1} "));
        }

        return FormattableString.Invariant($"""
            <svg viewBox="0 0 {TIME_SERIES_CHART_WIDTH} {TIME_SERIES_CHART_HEIGHT + 26}" width="100%">
              <polyline points="{throughputPoints}" fill="none" stroke="#0969da" stroke-width="2" />
              <polyline points="{debtPoints}" fill="none" stroke="#cf222e" stroke-width="2" />
              <text x="0" y="{TIME_SERIES_CHART_HEIGHT + 20}" font-size="11" fill="#0969da">— debit (max {maxThroughput:F0} it/s)</text>
              <text x="{TIME_SERIES_CHART_WIDTH}" y="{TIME_SERIES_CHART_HEIGHT + 20}" text-anchor="end" font-size="11" fill="#cf222e">— dette d'ordonnancement (max {maxDebt:F1} ms)</text>
            </svg>
            """);
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