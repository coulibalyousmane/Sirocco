using System.Text.Json;
using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;
using Sirocco.Extensions.Browser;

namespace Sirocco.UnitTests.BrowserExtension;

/// <summary>
/// Couvre la partie pure du protocole de reference navigateur : la lecture du releve rendu par la
/// page, et la conversion d'un Web Vital en mesure Sirocco. Ce sont les deux endroits ou une erreur
/// serait <b>silencieuse</b> — un vital lu sous un mauvais nom vaudrait zero sans jamais faire
/// echouer une iteration, et une duree mal convertie donnerait des centiles plausibles mais faux.
/// <para>
/// Le pilotage de Chromium n'est pas teste ici : il exige de vrais binaires de navigateur, et la
/// discipline du depot interdit de le remplacer par un mock du navigateur. Il est verifie par un
/// vrai tir, documente dans <c>docs/extensions/contrat.md</c>.
/// </para>
/// </summary>
public sealed class WebVitalsSampleTests
{
    [Fact]
    public void A_complete_reading_is_read_field_by_field()
    {
        WebVitalsSample sample = WebVitalsSample.FromJson(Parse(
            """{ "lcp": 220.5, "fcp": 76.25, "ttfb": 32.75, "cls": 0.03 }"""));

        Assert.Equal(220.5, sample.Lcp);
        Assert.Equal(76.25, sample.Fcp);
        Assert.Equal(32.75, sample.Ttfb);
        Assert.Equal(0.03, sample.Cls);
    }

    [Theory]
    [InlineData("""{ "fcp": 76 }""")]                        // lcp absent
    [InlineData("""{ "lcp": null, "fcp": 76 }""")]           // lcp null
    [InlineData("""{ "lcp": "220", "fcp": 76 }""")]          // lcp non numerique
    [InlineData("""{ "lcp": -5, "fcp": 76 }""")]             // lcp negatif : impossible pour une duree
    public void A_missing_or_unusable_value_falls_back_to_zero_without_failing(string json)
    {
        // Un navigateur peut legitimement ne rapporter aucun LCP (page sans element peint
        // mesurable) : c'est zero, pas un echec du tir.
        WebVitalsSample sample = WebVitalsSample.FromJson(Parse(json));

        Assert.Equal(0d, sample.Lcp);
        Assert.Equal(76d, sample.Fcp);
    }

    [Fact]
    public void A_reading_that_is_not_an_object_yields_zeroes()
    {
        WebVitalsSample sample = WebVitalsSample.FromJson(Parse("null"));

        Assert.Equal(new WebVitalsSample(0d, 0d, 0d, 0d), sample);
    }

    [Fact]
    public void A_vital_is_published_with_the_duration_the_browser_measured()
    {
        const double MEASURED_MILLISECONDS = 220d;
        StepId step = new(3);
        long baseTicks = SiroccoClock.Now;

        MetricResult result = WebVitalsSample.ToMetricResult(step, virtualUserId: 7, baseTicks, MEASURED_MILLISECONDS);

        // La duree portee est celle du navigateur, pas le temps qu'a pris le plugin a la relever.
        Assert.Equal(MEASURED_MILLISECONDS, SiroccoClock.ToMilliseconds(result.ServiceTicks), precision: 0);
        Assert.Equal(MEASURED_MILLISECONDS, SiroccoClock.ToMilliseconds(result.ResponseTicks), precision: 0);
        Assert.Equal(step, result.Step);
        Assert.Equal(7, result.VirtualUserId);
        Assert.Equal(RequestOutcome.Success, result.Outcome);
    }

    [Fact]
    public void A_published_vital_carries_no_scheduling_debt()
    {
        // Un Web Vital n'a pas de depart theorique : la dette appartient a l'etape de navigation,
        // pas a lui. La rendre non nulle ferait entrer le retard de l'injecteur dans ResponseTicks,
        // qui alimente les centiles, et corromprait la valeur rapportee.
        MetricResult result = WebVitalsSample.ToMetricResult(new StepId(1), virtualUserId: 0, SiroccoClock.Now, 150d);

        Assert.Equal(0L, result.SchedulingDelayTicks);
    }

    [Fact]
    public void A_negative_measurement_never_produces_a_negative_duration()
    {
        MetricResult result = WebVitalsSample.ToMetricResult(new StepId(1), virtualUserId: 0, SiroccoClock.Now, -42d);

        Assert.Equal(0L, result.ServiceTicks);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}