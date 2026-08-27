using Sirocco.Application.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Host;
using Sirocco.Host.Configuration;
using Sirocco.UnitTests.TestDoubles;

namespace Sirocco.UnitTests.Host;

/// <summary>
/// Verrouille la capacite de file de jetons que <c>StandaloneHost.BuildLoadModel</c> impose aux
/// modeles <b>tires par les utilisateurs virtuels</b> (modele ferme, montee d'utilisateurs,
/// iterations par utilisateur, iterations partagees).
/// <para>
/// Ce plafond est une correction, pas un reglage : leurs ordonnanceurs horodatent chaque jeton a
/// l'emission, donc tout ce qui attend en file gonfle <c>ResponseTicks</c> — la grandeur qui
/// alimente <b>tous</b> les centiles publies. Le defaut du moteur (<c>max(vus * 2, 64)</c>) leur
/// ouvrait une file bien plus profonde que l'effectif, ce qui restait invisible tant qu'une
/// iteration durait une milliseconde et devenait grossier des qu'elle durait une seconde.
/// </para>
/// <para>
/// Les quatre premiers tests portent sur le plan lui-meme — c'est la que se logerait une
/// regression. Le dernier est le vrai tir : il prouve que ce plan tient sur un scenario lent, et
/// sa contre-epreuve prouve qu'il discrimine.
/// </para>
/// </summary>
public sealed class LoadModelTokenQueueTests
{
    private const int VIRTUAL_USERS = 6;

    private static readonly HttpClient _sharedClient = new();

    /// <summary>
    /// Filet de securite : un blocage du moteur doit faire echouer le test, pas figer la suite.
    /// </summary>
    private static CancellationToken Guard() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static LoadStageOptions RpsStage(double rps, double seconds) =>
        new() { FromRps = rps, ToRps = rps, DurationSeconds = seconds };

    private static VirtualUserStageOptions VusStage(int fromVus, int toVus, double seconds) =>
        new() { FromVus = fromVus, ToVus = toVus, DurationSeconds = seconds };

    /// <summary>
    /// Reproduit le mappage plan -> options fait par <c>StandaloneHost.Run</c> : c'est cette
    /// traduction, et pas la valeur du plan prise isolement, qui decide de la file reellement
    /// ouverte par le moteur.
    /// </summary>
    private static LoadTestOptions OptionsFor(StandaloneHost.LoadModelPlan plan) => new()
    {
        MaxVirtualUsers = plan.EffectiveMaxVirtualUsers,
        RampProfile = plan.RampProfile,
        IterationsPerVirtualUser = plan.IterationsPerVirtualUser,
        TokenQueueCapacity = plan.TokenQueueCapacity,
    };

    [Fact]
    public void The_four_pull_models_cap_the_queue_at_the_virtual_user_count()
    {
        StandaloneHost.LoadModelPlan fixedDuration = StandaloneHost.BuildLoadModel(
            VIRTUAL_USERS, [], TimeSpan.FromSeconds(10d), [], null, null);
        StandaloneHost.LoadModelPlan perVirtualUser = StandaloneHost.BuildLoadModel(
            VIRTUAL_USERS, [], null, [], null, 20L);
        StandaloneHost.LoadModelPlan shared = StandaloneHost.BuildLoadModel(
            VIRTUAL_USERS, [], null, [], 120L, null);
        StandaloneHost.LoadModelPlan ramp = StandaloneHost.BuildLoadModel(
            1, [], null, [VusStage(0, VIRTUAL_USERS, 10d)], null, null);

        Assert.Equal(VIRTUAL_USERS, fixedDuration.TokenQueueCapacity);
        Assert.Equal(VIRTUAL_USERS, perVirtualUser.TokenQueueCapacity);
        Assert.Equal(VIRTUAL_USERS, shared.TokenQueueCapacity);

        // La montee d'utilisateurs se plafonne sur son PIC, pas sur MaxVirtualUsers (ici 1) :
        // c'est l'effectif que le pool atteindra reellement, donc le seul nombre de jetons qui
        // puisse etre consomme de front.
        Assert.Equal(VIRTUAL_USERS, ramp.TokenQueueCapacity);
    }

    [Fact]
    public void The_open_model_keeps_the_default_queue_that_absorbs_a_burst()
    {
        StandaloneHost.LoadModelPlan open = StandaloneHost.BuildLoadModel(
            VIRTUAL_USERS, [RpsStage(rps: 50d, seconds: 10d)], null, [], null, null);

        // Le modele ouvert garde deliberement le defaut : sa file doit absorber une rafale, et la
        // dette qu'elle produit y est le vrai signal de saturation, pas un artefact. Lui appliquer
        // le meme plafond qu'aux modeles tires effacerait la mesure que tout le projet defend.
        Assert.Null(open.TokenQueueCapacity);
        Assert.Equal(
            LoadTestOptions.MINIMUM_TOKEN_QUEUE_CAPACITY,
            OptionsFor(open).EffectiveTokenQueueCapacity);
    }

    [Fact]
    public void The_capped_queue_is_the_one_the_engine_will_actually_open()
    {
        LoadTestOptions options = OptionsFor(StandaloneHost.BuildLoadModel(
            VIRTUAL_USERS, [], TimeSpan.FromSeconds(10d), [], null, null));

        Assert.Equal(VIRTUAL_USERS, options.EffectiveTokenQueueCapacity);

        // Ce qu'aurait ouvert le meme effectif sans le plan : dix fois plus profond que l'effectif,
        // par le seul jeu du plancher de 64.
        Assert.Equal(
            LoadTestOptions.MINIMUM_TOKEN_QUEUE_CAPACITY,
            new LoadTestOptions { MaxVirtualUsers = VIRTUAL_USERS }.EffectiveTokenQueueCapacity);
    }

    [Fact]
    public void A_pull_model_never_opens_a_zero_sized_queue()
    {
        // Un profil de montee qui reste a zero est constructible — VirtualUserStage n'interdit que
        // le negatif. Sans plancher, la capacite vaudrait 0 et Channel.CreateBounded(0) leverait
        // au demarrage du tir.
        StandaloneHost.LoadModelPlan idle = StandaloneHost.BuildLoadModel(
            1, [], null, [VusStage(0, 0, 1d)], null, null);

        Assert.Equal(1, idle.TokenQueueCapacity);
    }

    /// <summary>
    /// Le vrai tir : sur un scenario lent — le seul regime ou le defaut se voyait — un modele
    /// ferme ne doit plus deborder sa duree, et la contre-epreuve (meme configuration, file par
    /// defaut) doit deborder franchement. Sans elle, le premier assert pourrait passer pour une
    /// raison etrangere a la correction.
    /// </summary>
    [Fact]
    public async Task A_closed_model_run_with_a_slow_scenario_stays_within_its_duration()
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(200d);
        TimeSpan iteration = TimeSpan.FromMilliseconds(20d);

        LoadTestSummary capped = await RunClosedModelAsync(duration, iteration, cappedByThePlan: true);
        LoadTestSummary uncapped = await RunClosedModelAsync(duration, iteration, cappedByThePlan: false);

        Assert.Equal(0L, capped.IterationsFailed);
        Assert.Equal(0L, uncapped.IterationsFailed);

        // Ce que la duree autorise : une iteration par tranche de 20 ms pour l'unique utilisateur
        // virtuel, plus celle en vol et le seul jeton que la file plafonnee peut retenir. Borne
        // insensible a la charge de la machine, et par le bon cote : Task.Delay ne se declenche
        // jamais en avance, donc la contention ne peut que faire BAISSER ce compte.
        long maximumIterations = (long)(duration / iteration) + 2L;
        Assert.True(
            capped.IterationsStarted <= maximumIterations,
            $"Debordement : {capped.IterationsStarted} iterations pour une duree qui en autorisait {maximumIterations}.");

        // La contre-epreuve. Avec la file par defaut (64 jetons pour un seul utilisateur virtuel),
        // l'ordonnanceur la remplit d'un coup et le moteur la vide bien apres l'expiration du
        // palier : l'ecart est la profondeur de file elle-meme.
        Assert.True(
            uncapped.IterationsStarted > capped.IterationsStarted + (LoadTestOptions.MINIMUM_TOKEN_QUEUE_CAPACITY / 2),
            $"Contre-epreuve muette : {uncapped.IterationsStarted} iterations sans plafond contre {capped.IterationsStarted} avec.");

        // La duree, enoncee en relatif plutot qu'en absolu : les deux tirs subissent la meme
        // contention et l'ecart vient entierement de la file a vider, alors qu'une borne absolue
        // deviendrait instable des que la suite sature les coeurs (meme raisonnement que
        // TargetRpsLoadEngineTests sur son ecart response/service).
        Assert.True(
            uncapped.Duration > capped.Duration * 2d,
            $"Le plafond n'a rien change a la duree : {capped.Duration.TotalMilliseconds:F0} ms contre {uncapped.Duration.TotalMilliseconds:F0} ms sans lui.");

        // Et la consequence la plus grave, celle qui justifiait la correction : un jeton horodate a
        // l'emission mais execute une fois la file videe affiche un retard qui n'a jamais existe —
        // retard qui entre dans ResponseTicks, donc dans tous les centiles publies.
        Assert.True(
            capped.MaxSchedulingDelayMilliseconds * 4d < uncapped.MaxSchedulingDelayMilliseconds,
            $"Dette fantome non reduite : {capped.MaxSchedulingDelayMilliseconds:F1} ms contre {uncapped.MaxSchedulingDelayMilliseconds:F1} ms sans plafond.");
    }

    /// <summary>
    /// Deroule un vrai tir en modele ferme a un seul utilisateur virtuel. La capacite de file vient
    /// du plan reel, jamais d'une constante recopiee ici ; <paramref name="cappedByThePlan"/> a
    /// <see langword="false"/> rend au moteur son defaut, c'est-a-dire le comportement d'avant la
    /// correction.
    /// </summary>
    private static async Task<LoadTestSummary> RunClosedModelAsync(
        TimeSpan duration,
        TimeSpan iterationDuration,
        bool cappedByThePlan)
    {
        // Un plan neuf a chaque tir : un ordonnanceur ne se deroule qu'une fois.
        StandaloneHost.LoadModelPlan plan = StandaloneHost.BuildLoadModel(
            maxVirtualUsers: 1, [], duration, [], null, null);

        TargetRpsLoadEngine engine = new(
            plan.Scheduler,
            DelegateWorkflow.Slow(iterationDuration),
            _sharedClient,
            new CollectingMetricSink(),
            new LoadTestOptions
            {
                MaxVirtualUsers = plan.EffectiveMaxVirtualUsers,
                TokenQueueCapacity = cappedByThePlan ? plan.TokenQueueCapacity : null,
            },
            new StepRegistry());

        return await engine.RunAsync(Guard());
    }
}