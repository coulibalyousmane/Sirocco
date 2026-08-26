using System.Text.Json;
using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;

namespace Sirocco.Extensions.Browser;

/// <summary>
/// Les quatre Web Vitals releves par le navigateur sur une navigation, en millisecondes — sauf
/// <see cref="Cls"/>, qui est un score sans unite.
/// <para>
/// Type separe du workflow, et volontairement pur : c'est la partie testable sans navigateur. La
/// conversion depuis le JSON de la page et la conversion vers une mesure Sirocco sont les deux
/// endroits ou une erreur serait silencieuse (un vital lu sous le mauvais nom vaudrait zero sans
/// jamais echouer), donc les deux endroits qui meritent des tests unitaires.
/// </para>
/// </summary>
public readonly record struct WebVitalsSample(double Lcp, double Fcp, double Ttfb, double Cls)
{
    /// <summary>
    /// Lit l'objet rendu par la page. Une propriete absente ou non numerique vaut zero plutot que
    /// de faire echouer l'iteration : un navigateur peut legitimement ne rapporter aucun LCP (page
    /// sans element peint mesurable), et ce n'est pas un echec du tir.
    /// </summary>
    public static WebVitalsSample FromJson(JsonElement json) => new(
        ReadNumber(json, "lcp"),
        ReadNumber(json, "fcp"),
        ReadNumber(json, "ttfb"),
        ReadNumber(json, "cls"));

    private static double ReadNumber(JsonElement json, string propertyName) =>
        json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double parsed)
            && double.IsFinite(parsed)
            && parsed >= 0d
                ? parsed
                : 0d;

    /// <summary>
    /// Fabrique une mesure Sirocco portant une duree <b>mesuree par le navigateur</b>, et non le
    /// temps qu'a pris ce plugin a la relever.
    /// <para>
    /// <c>ScheduledTicks</c> et <c>StartedTicks</c> sont deliberement egaux : la dette
    /// d'ordonnancement d'un tel point vaut donc toujours zero, et c'est correct — un Web Vital
    /// n'a pas de notion de depart theorique, la dette appartient a l'etape de navigation, pas a
    /// lui. Les rendre differents ferait entrer le retard de l'injecteur dans
    /// <c>ResponseTicks</c>, qui alimente les centiles, et corromprait la valeur rapportee.
    /// </para>
    /// </summary>
    public static MetricResult ToMetricResult(StepId step, int virtualUserId, long baseTicks, double milliseconds)
    {
        long durationTicks = SiroccoClock.FromSeconds(Math.Max(0d, milliseconds) / 1000d);

        return new MetricResult(
            step,
            virtualUserId,
            baseTicks,
            baseTicks,
            baseTicks + durationTicks,
            StepScope.DEFAULT_SUCCESS_STATUS_CODE,
            RequestOutcome.Success,
            MetricResult.NO_PAYLOAD);
    }
}