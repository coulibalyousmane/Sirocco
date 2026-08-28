using Sirocco.Application.Metrics;
using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;
using Sirocco.UnitTests.TestDoubles;

namespace Sirocco.UnitTests.Metrics;

public sealed class MetricsAggregatorTests
{
    private const string LOGIN_STEP = "login";
    private const string CHECKOUT_STEP = "checkout";

    private static readonly TimeSpan _windowDuration = TimeSpan.FromSeconds(10);
    private static readonly MetricsAggregatorOptions _options = new()
    {
        WindowDuration = _windowDuration,
        WindowBucketCount = 10,
    };

    private static StepRegistry CreateRegistry()
    {
        StepRegistry registry = new();
        registry.Register(WellKnownSteps.ITERATION);
        registry.Register(LOGIN_STEP);
        registry.Register(CHECKOUT_STEP);
        registry.Seal();

        return registry;
    }

    private static MetricResult Metric(
        StepId step,
        long completedAtTicks,
        double responseMilliseconds,
        double serviceMilliseconds,
        RequestOutcome outcome = RequestOutcome.Success,
        long bytes = 0L) =>
        MetricFactory.Create(step, completedAtTicks, responseMilliseconds, serviceMilliseconds, outcome, bytes);

    /// <summary>
    /// Un conteneur d'injection de dependances ne garantit aucun ordre entre deux singletons
    /// independants : l'agregateur peut tres bien etre construit, voire interroge, avant que
    /// le moteur n'ait rempli le registre. Ce n'est pas une erreur, juste un tir qui n'a pas
    /// encore commence.
    /// </summary>
    [Fact]
    public void Constructing_with_an_empty_registry_does_not_throw_and_yields_an_empty_report()
    {
        MetricsAggregator aggregator = new(new StepRegistry());

        LoadTestReport report = aggregator.Snapshot(StatisticsScope.Cumulative);

        Assert.Empty(report.Steps);
        Assert.Equal(0L, report.Iteration.Count);
    }

    /// <summary>
    /// Reproduit exactement l'ordre qui a fait echouer le premier tir reel : l'agregateur est
    /// construit sur un registre encore vide, puis le moteur remplit et scelle CE MEME
    /// registre au demarrage du tir. Sans reconstruire l'agregateur, il doit refleter les
    /// etapes des qu'elles existent.
    /// </summary>
    [Fact]
    public void Accumulators_pick_up_steps_registered_after_the_aggregator_was_constructed()
    {
        StepRegistry registry = new();
        MetricsAggregator aggregator = new(registry, _options);

        Assert.Empty(aggregator.Snapshot(StatisticsScope.Cumulative).Steps);

        StepId login = registry.Register(LOGIN_STEP);
        registry.Seal();

        long now = SiroccoClock.Now;
        aggregator.Record(Metric(login, now, 10d, 10d));

        Assert.Equal(1L, aggregator.SnapshotStep(login, StatisticsScope.Cumulative, now).Count);
    }

    [Fact]
    public void Every_declared_step_gets_its_own_row_even_without_measurements()
    {
        MetricsAggregator aggregator = new(CreateRegistry(), _options);

        LoadTestReport report = aggregator.Snapshot(StatisticsScope.Cumulative);

        Assert.Equal(3, report.Steps.Count);
        Assert.Contains(report.Steps, step => step.Name == LOGIN_STEP);
        Assert.All(report.Steps, step => Assert.Equal(0L, step.Count));
    }

    [Fact]
    public void Measurements_are_attributed_to_the_right_step()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(LOGIN_STEP, out StepId login);
        registry.TryGetId(CHECKOUT_STEP, out StepId checkout);

        long now = SiroccoClock.Now;
        aggregator.Record(Metric(login, now, 10d, 10d));
        aggregator.Record(Metric(login, now, 20d, 20d));
        aggregator.Record(Metric(checkout, now, 30d, 30d));

        Assert.Equal(2L, aggregator.SnapshotStep(login, StatisticsScope.Cumulative, now).Count);
        Assert.Equal(1L, aggregator.SnapshotStep(checkout, StatisticsScope.Cumulative, now).Count);
    }

    [Fact]
    public void An_unknown_step_is_ignored_rather_than_crashing_the_consumer()
    {
        MetricsAggregator aggregator = new(CreateRegistry(), _options);

        aggregator.Record(Metric(new StepId(99), SiroccoClock.Now, 10d, 10d));

        Assert.Equal(0L, aggregator.Snapshot(StatisticsScope.Cumulative).Steps.Sum(step => step.Count));
    }

    [Fact]
    public void Outcomes_are_counted_separately_and_drive_the_error_rate()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(LOGIN_STEP, out StepId login);
        long now = SiroccoClock.Now;

        for (int i = 0; i < 8; i++)
        {
            aggregator.Record(Metric(login, now, 10d, 10d));
        }

        aggregator.Record(Metric(login, now, 10d, 10d, RequestOutcome.HttpError));
        aggregator.Record(Metric(login, now, 10d, 10d, RequestOutcome.Dropped));

        StepStatistics statistics = aggregator.SnapshotStep(login, StatisticsScope.Cumulative, now);

        Assert.Equal(10L, statistics.Count);
        Assert.Equal(8L, statistics.SuccessCount);
        Assert.Equal(2L, statistics.FailureCount);
        Assert.Equal(1L, statistics.DroppedCount);
        Assert.Equal(0.2d, statistics.ErrorRate, 1e-9);
        Assert.Equal(1L, statistics.CountByOutcome[(int)RequestOutcome.HttpError]);
    }

    /// <summary>
    /// Le coeur de la promesse : les deux distributions cohabitent, et leur ecart chiffre
    /// exactement ce qu'un outil naif aurait masque.
    /// </summary>
    [Fact]
    public void Response_and_service_latencies_are_kept_side_by_side()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(LOGIN_STEP, out StepId login);
        long now = SiroccoClock.Now;

        // Service constant a 20 ms, mais 480 ms d'attente dans la file de l'injecteur.
        for (int i = 0; i < 100; i++)
        {
            aggregator.Record(Metric(login, now, responseMilliseconds: 500d, serviceMilliseconds: 20d));
        }

        StepStatistics statistics = aggregator.SnapshotStep(login, StatisticsScope.Cumulative, now);

        Assert.InRange(statistics.Service.P99Milliseconds, 20d, 20.5d);
        Assert.InRange(statistics.Response.P99Milliseconds, 500d, 504d);
        Assert.InRange(statistics.CoordinatedOmissionP99Milliseconds, 479d, 484d);
    }

    /// <summary>
    /// Le rapport d'histogramme n'est pas un a-cote optionnel : il doit refleter exactement le
    /// nombre de mesures que les centiles voisins comptent deja, sans quoi le graphe qui en est
    /// tire (voir <see cref="LoadTestReport"/>) mentirait par rapport au tableau.
    /// </summary>
    [Fact]
    public void The_response_histogram_reflects_recorded_measurements()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(LOGIN_STEP, out StepId login);
        long now = SiroccoClock.Now;

        for (int i = 0; i < 5; i++)
        {
            aggregator.Record(Metric(login, now, responseMilliseconds: 10d, serviceMilliseconds: 10d));
        }

        StepStatistics statistics = aggregator.SnapshotStep(login, StatisticsScope.Cumulative, now);

        Assert.Equal(5L, statistics.ResponseHistogram.TotalCount);
        Assert.Equal(statistics.ResponseHistogram.Buckets.Sum(), statistics.ResponseHistogram.TotalCount);
    }

    [Fact]
    public void The_response_histogram_is_empty_for_a_step_without_measurements()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(LOGIN_STEP, out StepId login);

        StepStatistics statistics = aggregator.SnapshotStep(login, StatisticsScope.Cumulative, SiroccoClock.Now);

        Assert.Equal(0L, statistics.ResponseHistogram.TotalCount);
    }

    [Fact]
    public void The_cumulative_scope_keeps_everything_the_sliding_scope_forgets()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(LOGIN_STEP, out StepId login);

        long start = SiroccoClock.Now;
        long windowTicks = SiroccoClock.FromTimeSpan(_windowDuration);

        // Une salve ancienne, puis une salve recente.
        for (int i = 0; i < 50; i++)
        {
            aggregator.Record(Metric(login, start, 100d, 100d));
        }

        long later = start + (windowTicks * 3L);
        for (int i = 0; i < 20; i++)
        {
            aggregator.Record(Metric(login, later, 10d, 10d));
        }

        StepStatistics cumulative = aggregator.SnapshotStep(login, StatisticsScope.Cumulative, later);
        StepStatistics sliding = aggregator.SnapshotStep(login, StatisticsScope.Sliding, later);

        Assert.Equal(70L, cumulative.Count);
        Assert.Equal(20L, sliding.Count);

        // Le cumul garde la trace des 100 ms ; la fenetre ne voit plus que les 10 ms.
        Assert.InRange(cumulative.Response.MaxMilliseconds, 100d, 101d);
        Assert.InRange(sliding.Response.MaxMilliseconds, 10d, 10.5d);
    }

    [Fact]
    public void The_sliding_window_empties_itself_when_traffic_stops()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(LOGIN_STEP, out StepId login);

        long start = SiroccoClock.Now;
        for (int i = 0; i < 30; i++)
        {
            aggregator.Record(Metric(login, start, 50d, 50d));
        }

        Assert.Equal(30L, aggregator.SnapshotStep(login, StatisticsScope.Sliding, start).Count);

        // Sans cette purge a la lecture, un tir termine afficherait eternellement
        // ses derniers centiles comme s'ils etaient encore d'actualite.
        long wellAfter = start + (SiroccoClock.FromTimeSpan(_windowDuration) * 5L);

        Assert.Equal(0L, aggregator.SnapshotStep(login, StatisticsScope.Sliding, wellAfter).Count);
        Assert.Equal(30L, aggregator.SnapshotStep(login, StatisticsScope.Cumulative, wellAfter).Count);
    }

    [Fact]
    public void A_partially_expired_window_keeps_only_the_recent_buckets()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(LOGIN_STEP, out StepId login);

        long bucketTicks = SiroccoClock.FromTimeSpan(_options.BucketDuration);
        long start = (SiroccoClock.Now / bucketTicks) * bucketTicks;

        // Une mesure par panier sur deux fenetres completes.
        for (int i = 0; i < 20; i++)
        {
            aggregator.Record(Metric(login, start + (bucketTicks * i), 10d, 10d));
        }

        long now = start + (bucketTicks * 19L);
        long slidingCount = aggregator.SnapshotStep(login, StatisticsScope.Sliding, now).Count;

        // Dix paniers d'une mesure : la fenetre en conserve dix, jamais les vingt.
        Assert.Equal(10L, slidingCount);
        Assert.Equal(20L, aggregator.SnapshotStep(login, StatisticsScope.Cumulative, now).Count);
    }

    [Fact]
    public void The_report_exposes_the_iteration_step_and_the_throughput()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(WellKnownSteps.ITERATION, out StepId iteration);
        long now = SiroccoClock.Now;

        for (int i = 0; i < 40; i++)
        {
            aggregator.Record(Metric(iteration, now, 25d, 25d));
        }

        LoadTestReport report = aggregator.Snapshot(StatisticsScope.Sliding, now);

        Assert.Equal(40L, report.Iteration.Count);
        Assert.Equal(_windowDuration, report.Duration);
        Assert.Equal(4d, report.IterationsPerSecond, 1e-9);
        Assert.True(report.IsTrustworthy);
    }

    [Fact]
    public void Lost_measurements_mark_the_report_as_untrustworthy()
    {
        MetricsAggregator aggregator = new(CreateRegistry(), _options)
        {
            MetricsDropped = 12L,
        };

        LoadTestReport report = aggregator.Snapshot(StatisticsScope.Cumulative);

        Assert.False(report.IsTrustworthy);
        Assert.Contains("12 mesures perdues", report.ToTable(), StringComparison.Ordinal);
    }

    [Fact]
    public void Bytes_received_are_accumulated_per_step()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(CHECKOUT_STEP, out StepId checkout);
        long now = SiroccoClock.Now;

        aggregator.Record(Metric(checkout, now, 10d, 10d, bytes: 1_500L));
        aggregator.Record(Metric(checkout, now, 10d, 10d, bytes: 2_500L));

        Assert.Equal(4_000L, aggregator.SnapshotStep(checkout, StatisticsScope.Cumulative, now).BytesReceived);
    }

    [Fact]
    public void Invalid_window_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new MetricsAggregator(
            CreateRegistry(),
            new MetricsAggregatorOptions { WindowBucketCount = 0 }));

        Assert.Throws<ArgumentException>(() => new MetricsAggregator(
            CreateRegistry(),
            new MetricsAggregatorOptions { WindowDuration = TimeSpan.Zero }));
    }

    /// <summary>
    /// Debut d'un panier, pour que le decoupage de la fenetre soit exact dans les deux tests
    /// suivants : les creneaux se calculent depuis les ticks absolus, pas depuis le premier
    /// enregistrement.
    /// </summary>
    private static long AlignedStart(out long bucketTicks)
    {
        bucketTicks = SiroccoClock.FromTimeSpan(_windowDuration) / 10L;
        return SiroccoClock.Now / bucketTicks * bucketTicks;
    }

    /// <summary>
    /// Le constat M7 de AUDIT-MATURITE.md : la dette d'ordonnancement etait la seule grandeur non
    /// fenetree, si bien que la portee glissante rendait un maximum <b>cumule</b>. La colonne
    /// "dette max" de la serie temporelle et la courbe de dette du rapport HTML etaient donc
    /// monotones par construction, et un pic de demarrage y restait affiche jusqu'a la fin du tir —
    /// indistinguable d'une saturation en cours.
    /// </summary>
    [Fact]
    public void The_sliding_scheduling_debt_falls_back_once_the_spike_has_left_the_window()
    {
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(LOGIN_STEP, out StepId login);

        long start = AlignedStart(out long bucketTicks);

        // Pic de demarrage : 400 ms de dette (response 500 - service 100).
        aggregator.Record(Metric(login, start, responseMilliseconds: 500d, serviceMilliseconds: 100d));

        // Neuf paniers plus tard, regime etabli : 20 ms de dette.
        long later = start + (9L * bucketTicks);
        aggregator.Record(Metric(login, later, responseMilliseconds: 120d, serviceMilliseconds: 100d));

        // Un panier de plus, et le pic sort de la fenetre : la dette glissante doit retomber sur ce
        // que la fenetre contient encore, jamais rester sur un maximum historique.
        long afterSpikeExpired = start + (10L * bucketTicks);
        StepStatistics sliding = aggregator.SnapshotStep(login, StatisticsScope.Sliding, afterSpikeExpired);

        Assert.Equal(20d, sliding.MaxSchedulingDelayMilliseconds, precision: 0);

        // Le cumul, lui, reste une marque de crue : c'est son role, et c'est ce que le bilan de fin
        // de tir et l'export inter-process doivent continuer de rapporter.
        StepStatistics cumulative = aggregator.SnapshotStep(login, StatisticsScope.Cumulative, afterSpikeExpired);

        Assert.Equal(400d, cumulative.MaxSchedulingDelayMilliseconds, precision: 0);
    }

    [Fact]
    public void The_sliding_scheduling_debt_still_reports_the_worst_of_the_whole_window()
    {
        // Contre-epreuve du test precedent : fenetrer la dette ne doit pas la reduire au dernier
        // panier. Tant que le pic est DANS la fenetre, c'est lui qu'on veut voir.
        StepRegistry registry = CreateRegistry();
        MetricsAggregator aggregator = new(registry, _options);
        registry.TryGetId(LOGIN_STEP, out StepId login);

        long start = AlignedStart(out long bucketTicks);

        aggregator.Record(Metric(login, start, responseMilliseconds: 500d, serviceMilliseconds: 100d));
        aggregator.Record(Metric(login, start + (2L * bucketTicks), responseMilliseconds: 120d, serviceMilliseconds: 100d));

        StepStatistics sliding = aggregator.SnapshotStep(
            login, StatisticsScope.Sliding, start + (2L * bucketTicks));

        Assert.Equal(400d, sliding.MaxSchedulingDelayMilliseconds, precision: 0);
    }
}