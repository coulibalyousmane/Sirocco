using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;

namespace Sirocco.Extensions.Browser;

/// <summary>
/// Cinquieme protocole de reference : le navigateur (ROADMAP.md, ligne "Test navigateur
/// (Web Vitals)"). Il valide le contrat de plugin sous un angle qu'aucun des quatre autres
/// n'exerce — le scenario ne parle aucun protocole reseau lui-meme : il pilote un vrai Chromium
/// (Playwright) et rapporte ce que le <b>navigateur</b> a mesure.
/// <para>
/// <b>Modele ferme obligatoire.</b> Un contexte de navigateur coute des centaines de Mo et une
/// navigation prend des secondes : ce plugin tourne a concurrence a un chiffre, pas a 50
/// utilisateurs virtuels. Il se pilote donc en <c>--vus</c>, jamais en <c>--rps</c> — sous un
/// profil en debit, le moteur serait en dette d'ordonnancement permanente et rapporterait une
/// dette catastrophique a chaque tir : exact, et parfaitement inutile. Le partage habituel
/// s'applique : le navigateur <i>mesure l'experience</i>, un tir protocolaire <i>genere la
/// charge</i>, les deux cote a cote.
/// </para>
/// <para>
/// <b>Ce que devient chaque vital.</b> LCP, FCP et TTFB sont des durees en millisecondes, non
/// negatives et bornees : publies comme des <b>etapes</b> plutot que comme des metriques
/// personnalisees, ils heritent gratuitement de l'histogramme de latence, donc des centiles
/// <i>et</i> des seuils — <c>ResponseP75Milliseconds</c> existe deja, et c'est exactement le
/// centile auquel les Web Vitals sont definis. CLS, lui, est un score sans unite (typiquement
/// 0 a 1, fractionnaire) : un histogramme de millisecondes ne le represente pas. Il part donc en
/// metrique personnalisee de type <see cref="CustomMetricKind.Trend"/>, <b>qui n'expose aucun
/// centile</b> (voir <c>CustomMetricSnapshot</c>) — on n'a donc de CLS que le min, la moyenne et
/// le max, et aucun seuil ne peut le viser. C'est la limite connue de cette version, enoncee
/// plutot que contournee.
/// </para>
/// <para>
/// <b>Un contexte de navigateur neuf par iteration</b>, mais un seul navigateur pour tout le tir :
/// les Web Vitals decrivent une <i>premiere visite</i>, qu'un cache deja chaud fausserait, tandis
/// que relancer un processus Chromium a chaque iteration couterait plus cher que la mesure
/// elle-meme.
/// </para>
/// </summary>
public sealed class BrowserWorkflow : IWorkflow
{
    private const string PATH_ENVIRONMENT_VARIABLE = "SIROCCO_BROWSER_PLUGIN_PATH";
    private const string SETTLE_MILLISECONDS_ENVIRONMENT_VARIABLE = "SIROCCO_BROWSER_PLUGIN_SETTLE_MILLISECONDS";
    private const string TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE = "SIROCCO_BROWSER_PLUGIN_TIMEOUT_SECONDS";
    private const string HEADED_ENVIRONMENT_VARIABLE = "SIROCCO_BROWSER_PLUGIN_HEADED";

    private const string DEFAULT_PATH = "/demo";
    private const int DEFAULT_SETTLE_MILLISECONDS = 600;
    private const int DEFAULT_TIMEOUT_SECONDS = 30;
    private const string CLS_METRIC_NAME = "web_vitals_cls";

    private readonly string _path;
    private readonly int _settleMilliseconds;
    private readonly float _timeoutMilliseconds;
    private readonly bool _headed;

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private StepId _navigationStep;
    private StepId _largestContentfulPaintStep;
    private StepId _firstContentfulPaintStep;
    private StepId _timeToFirstByteStep;
    private CustomMetricId _cumulativeLayoutShift = CustomMetricId.None;

    public BrowserWorkflow()
    {
        _path = Environment.GetEnvironmentVariable(PATH_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredPath
            ? configuredPath
            : DEFAULT_PATH;

        _settleMilliseconds = ReadPositiveInt(SETTLE_MILLISECONDS_ENVIRONMENT_VARIABLE, DEFAULT_SETTLE_MILLISECONDS);
        _timeoutMilliseconds = ReadPositiveInt(TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE, DEFAULT_TIMEOUT_SECONDS) * 1000f;
        _headed = Environment.GetEnvironmentVariable(HEADED_ENVIRONMENT_VARIABLE) is "1" or "true" or "TRUE";
    }

    /// <inheritdoc />
    public string Name => "browser-plugin";

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _navigationStep = registry.Register("navigation");
        _largestContentfulPaintStep = registry.Register("LCP");
        _firstContentfulPaintStep = registry.Register("FCP");
        _timeToFirstByteStep = registry.Register("TTFB");
    }

    /// <inheritdoc />
    public void RegisterMetrics(CustomMetricRegistry registry) =>
        _cumulativeLayoutShift = registry.Register(CLS_METRIC_NAME, CustomMetricKind.Trend);

    /// <inheritdoc />
    public async ValueTask SetUpAsync(CancellationToken cancellationToken)
    {
        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        _browser = await _playwright.Chromium
            .LaunchAsync(new BrowserTypeLaunchOptions { Headless = !_headed })
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        if (_browser is null)
        {
            throw new InvalidOperationException(
                "Le navigateur n'est pas demarre : SetUpAsync n'a pas ete appele avant ExecuteAsync.");
        }

        string url = BuildUrl(context);

        // Contexte neuf : cache, cookies et stockage vides, donc une vraie premiere visite — ce
        // que les Web Vitals decrivent.
        await using IBrowserContext browserContext = await _browser.NewContextAsync().ConfigureAwait(false);
        IPage page = await browserContext.NewPageAsync().ConfigureAwait(false);

        StepScope navigation = context.BeginStep(_navigationStep);
        long baseTicks = navigation.StartedTicks;

        IResponse? response;
        try
        {
            response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = _timeoutMilliseconds,
            }).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            navigation.Fail(RequestOutcome.Timeout);
            return;
        }
        catch (PlaywrightException)
        {
            navigation.Fail(RequestOutcome.ConnectionError);
            return;
        }

        if (response is null)
        {
            // Navigation sans reponse : une redirection vers une ancre, ou une page servie depuis
            // le cache du navigateur. Rien a mesurer, et rien d'anormal non plus.
            navigation.Fail(RequestOutcome.AssertionFailed);
            return;
        }

        navigation.CompleteHttp(response.Status);
        if (!response.Ok)
        {
            return;
        }

        WebVitalsSample vitals;
        try
        {
            JsonElement collected = await page
                .EvaluateAsync<JsonElement>(BuildCollectorScript(_settleMilliseconds))
                .ConfigureAwait(false);
            vitals = WebVitalsSample.FromJson(collected);
        }
        catch (PlaywrightException)
        {
            // La page a disparu sous le releve (navigation, fermeture) : la navigation elle-meme
            // reste comptee, seuls les vitals manquent a l'appel.
            return;
        }

        Publish(context, _largestContentfulPaintStep, baseTicks, vitals.Lcp);
        Publish(context, _firstContentfulPaintStep, baseTicks, vitals.Fcp);
        Publish(context, _timeToFirstByteStep, baseTicks, vitals.Ttfb);

        if (_cumulativeLayoutShift != CustomMetricId.None)
        {
            context.RecordCustomMetric(_cumulativeLayoutShift, vitals.Cls);
        }
    }

    /// <inheritdoc />
    public async ValueTask TearDownAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync().ConfigureAwait(false);
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
    }

    private static void Publish(IVirtualUserContext context, StepId step, long baseTicks, double milliseconds) =>
        context.Report(WebVitalsSample.ToMetricResult(step, context.VirtualUserId, baseTicks, milliseconds));

    /// <summary>
    /// Adresse absolue de la page, derivee de <c>--target-url</c> via l'adresse de base du client
    /// HTTP partage — meme convention que les protocoles de reference SSE et GraphQL, qui
    /// n'imposent pas non plus leur propre cible.
    /// </summary>
    private string BuildUrl(IVirtualUserContext context)
    {
        Uri? baseAddress = context.HttpClient.BaseAddress;
        return baseAddress is null ? _path : new Uri(baseAddress, _path).ToString();
    }

    /// <summary>
    /// Releve les vitals depuis la page elle-meme. Ecrit a la main plutot qu'en embarquant la
    /// bibliotheque <c>web-vitals</c> : la page ne doit dependre d'aucune ressource externe, sans
    /// quoi la mesure dependrait du reseau de quelqu'un d'autre.
    /// <para>
    /// <c>buffered: true</c> est indispensable : l'observateur est pose <b>apres</b> la navigation,
    /// et sans lui les entrees deja emises (donc le LCP lui-meme) seraient invisibles.
    /// </para>
    /// </summary>
    private static string BuildCollectorScript(int settleMilliseconds) =>
        string.Create(CultureInfo.InvariantCulture, $$"""
            () => new Promise(resolve => {
              let lcp = 0;
              let cls = 0;
              try {
                new PerformanceObserver(list => {
                  for (const entry of list.getEntries()) {
                    lcp = Math.max(lcp, entry.startTime);
                  }
                }).observe({ type: 'largest-contentful-paint', buffered: true });
              } catch (error) { /* navigateur sans LCP : la valeur reste a zero */ }
              try {
                new PerformanceObserver(list => {
                  for (const entry of list.getEntries()) {
                    if (!entry.hadRecentInput) { cls += entry.value; }
                  }
                }).observe({ type: 'layout-shift', buffered: true });
              } catch (error) { /* navigateur sans layout-shift : la valeur reste a zero */ }
              setTimeout(() => {
                const navigation = performance.getEntriesByType('navigation')[0];
                const paint = performance.getEntriesByName('first-contentful-paint')[0];
                resolve({
                  lcp: lcp,
                  fcp: paint ? paint.startTime : 0,
                  ttfb: navigation ? navigation.responseStart : 0,
                  cls: cls
                });
              }, {{settleMilliseconds}});
            })
            """);

    private static int ReadPositiveInt(string variableName, int defaultValue) =>
        Environment.GetEnvironmentVariable(variableName) is { Length: > 0 } configured
            && int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed > 0
                ? parsed
                : defaultValue;
}