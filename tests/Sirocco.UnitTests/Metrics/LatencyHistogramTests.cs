using Sirocco.Domain.Metrics;

namespace Sirocco.UnitTests.Metrics;

public sealed class LatencyHistogramTests
{
    [Fact]
    public void An_empty_histogram_reports_nothing()
    {
        LatencyHistogram histogram = new();

        Assert.True(histogram.IsEmpty);
        Assert.Equal(0L, histogram.Count);
        Assert.True(histogram.Snapshot().IsEmpty);
    }

    /// <summary>
    /// Sous 128 microsecondes, un panier par microseconde : la zone basse est exacte,
    /// sans quoi les latences des cibles rapides seraient noyees dans l'arrondi.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(42L)]
    [InlineData(127L)]
    public void Small_values_are_stored_exactly(long microseconds)
    {
        LatencyHistogram histogram = new();
        histogram.Record(microseconds);

        LatencySnapshot snapshot = histogram.Snapshot();

        Assert.Equal(microseconds, snapshot.P50Microseconds);
        Assert.Equal(microseconds, snapshot.MinMicroseconds);
        Assert.Equal(microseconds, snapshot.MaxMicroseconds);
    }

    /// <summary>
    /// La garantie de precision : une valeur rapportee n'est jamais inferieure a la valeur
    /// reelle, et la depasse de moins de 0,8 %. Se tromper par exces est la seule erreur
    /// acceptable pour une verification de SLO.
    /// </summary>
    [Theory]
    [InlineData(128L)]
    [InlineData(200L)]
    [InlineData(1_000L)]
    [InlineData(12_345L)]
    [InlineData(999_999L)]
    [InlineData(60_000_000L)]
    [InlineData(500_000_000L)]
    public void A_reported_value_never_understates_the_real_one(long microseconds)
    {
        LatencyHistogram histogram = new();
        histogram.Record(microseconds);

        long reported = histogram.Snapshot().P50Microseconds;
        double upperBound = microseconds * (1d + LatencyHistogram.RELATIVE_ERROR);

        Assert.True(reported >= microseconds, $"{reported} sous-estime {microseconds}.");
        Assert.True(reported <= upperBound, $"{reported} depasse la borne d'erreur {upperBound:F0}.");
    }

    [Fact]
    public void Percentiles_of_a_uniform_distribution_land_where_expected()
    {
        LatencyHistogram histogram = new();
        for (long value = 1L; value <= 10_000L; value++)
        {
            histogram.Record(value);
        }

        LatencySnapshot snapshot = histogram.Snapshot();

        Assert.Equal(10_000L, snapshot.Count);
        Assert.Equal(1L, snapshot.MinMicroseconds);
        Assert.Equal(10_000L, snapshot.MaxMicroseconds);

        AssertWithinTolerance(5_000L, snapshot.P50Microseconds);
        AssertWithinTolerance(9_000L, snapshot.P90Microseconds);
        AssertWithinTolerance(9_900L, snapshot.P99Microseconds);
        AssertWithinTolerance(9_990L, snapshot.P999Microseconds);
    }

    [Fact]
    public void The_mean_is_exact_not_derived_from_the_buckets()
    {
        LatencyHistogram histogram = new();
        for (long value = 1L; value <= 1_000L; value++)
        {
            histogram.Record(value);
        }

        // Moyenne de 1..1000 = 500,5. Un calcul base sur les paniers deriverait.
        Assert.Equal(500.5d, histogram.Snapshot().MeanMicroseconds, 1e-9);
    }

    [Fact]
    public void Percentiles_are_monotonically_increasing()
    {
        LatencyHistogram histogram = new();
        for (int i = 0; i < 1_000; i++)
        {
            histogram.Record(i % 7 == 0 ? 500_000L : 250L);
        }

        LatencySnapshot snapshot = histogram.Snapshot();

        Assert.True(snapshot.P50Microseconds <= snapshot.P75Microseconds);
        Assert.True(snapshot.P75Microseconds <= snapshot.P90Microseconds);
        Assert.True(snapshot.P90Microseconds <= snapshot.P95Microseconds);
        Assert.True(snapshot.P95Microseconds <= snapshot.P99Microseconds);
        Assert.True(snapshot.P99Microseconds <= snapshot.P999Microseconds);
        Assert.True(snapshot.P999Microseconds <= snapshot.MaxMicroseconds);
    }

    /// <summary>
    /// Une poignee de valeurs extremes ne doit pas deplacer la mediane : c'est exactement
    /// ce qu'un outil qui ne publierait que la moyenne masquerait.
    /// </summary>
    [Fact]
    public void A_heavy_tail_moves_the_high_percentiles_not_the_median()
    {
        LatencyHistogram histogram = new();
        for (int i = 0; i < 990; i++)
        {
            histogram.Record(1_000L);
        }

        for (int i = 0; i < 10; i++)
        {
            histogram.Record(2_000_000L);
        }

        LatencySnapshot snapshot = histogram.Snapshot();

        AssertWithinTolerance(1_000L, snapshot.P50Microseconds);
        AssertWithinTolerance(1_000L, snapshot.P95Microseconds);
        AssertWithinTolerance(2_000_000L, snapshot.P999Microseconds);
    }

    [Fact]
    public void Merging_two_histograms_matches_recording_into_one()
    {
        LatencyHistogram left = new();
        LatencyHistogram right = new();
        LatencyHistogram reference = new();

        for (long value = 1L; value <= 5_000L; value++)
        {
            LatencyHistogram target = value % 2L == 0L ? left : right;
            target.Record(value);
            reference.Record(value);
        }

        left.Add(right);

        Assert.Equal(reference.Snapshot(), left.Snapshot());
    }

    [Fact]
    public void Merging_an_empty_histogram_changes_nothing()
    {
        LatencyHistogram histogram = new();
        histogram.Record(1_234L);
        LatencySnapshot before = histogram.Snapshot();

        histogram.Add(new LatencyHistogram());

        Assert.Equal(before, histogram.Snapshot());
    }

    /// <summary>
    /// Le chemin emprunte par le mode distribue : exporter l'etat brut d'un histogramme, le
    /// transferer (ici, juste passer l'objet), puis le fusionner dans un autre — doit produire
    /// exactement le meme resultat que la fusion directe entre deux histogrammes.
    /// </summary>
    [Fact]
    public void Merging_an_exported_snapshot_matches_recording_into_one()
    {
        LatencyHistogram left = new();
        LatencyHistogram right = new();
        LatencyHistogram reference = new();

        for (long value = 1L; value <= 5_000L; value++)
        {
            LatencyHistogram target = value % 2L == 0L ? left : right;
            target.Record(value);
            reference.Record(value);
        }

        left.Add(right.Export());

        Assert.Equal(reference.Snapshot(), left.Snapshot());
    }

    [Fact]
    public void Merging_an_empty_exported_snapshot_changes_nothing()
    {
        LatencyHistogram histogram = new();
        histogram.Record(1_234L);
        LatencySnapshot before = histogram.Snapshot();

        histogram.Add(new LatencyHistogram().Export());

        Assert.Equal(before, histogram.Snapshot());
    }

    [Fact]
    public void An_exported_snapshot_reports_the_same_scalar_aggregates()
    {
        LatencyHistogram histogram = new();
        histogram.Record(10L);
        histogram.Record(20L);
        histogram.Record(30L);

        HistogramSnapshot exported = histogram.Export();

        Assert.Equal(3L, exported.TotalCount);
        Assert.Equal(60L, exported.SumMicroseconds);
        Assert.Equal(10L, exported.MinMicroseconds);
        Assert.Equal(30L, exported.MaxMicroseconds);
        Assert.Equal(LatencyHistogram.BucketCount, exported.Buckets.Length);
    }

    [Fact]
    public void Reset_makes_the_histogram_reusable()
    {
        LatencyHistogram histogram = new();
        histogram.Record(5_000L);
        histogram.Reset();

        Assert.True(histogram.IsEmpty);

        histogram.Record(100L);
        LatencySnapshot snapshot = histogram.Snapshot();

        Assert.Equal(1L, snapshot.Count);
        Assert.Equal(100L, snapshot.MaxMicroseconds);
        Assert.Equal(100L, snapshot.MinMicroseconds);
    }

    [Fact]
    public void A_value_beyond_the_tracked_range_is_still_counted_and_its_maximum_kept()
    {
        LatencyHistogram histogram = new();
        long huge = LatencyHistogram.MAX_TRACKABLE_MICROSECONDS * 10L;

        histogram.Record(huge);
        LatencySnapshot snapshot = histogram.Snapshot();

        Assert.Equal(1L, snapshot.Count);

        // La valeur exacte est perdue dans le dernier panier, mais ni le compte ni le
        // maximum reel ne le sont : un depassement reste visible dans le rapport.
        Assert.Equal(huge, snapshot.MaxMicroseconds);
        Assert.Equal(LatencyHistogram.MAX_TRACKABLE_MICROSECONDS, snapshot.P50Microseconds);
    }

    [Fact]
    public void A_negative_duration_is_clamped_to_zero_rather_than_corrupting_the_buckets()
    {
        LatencyHistogram histogram = new();

        histogram.Record(-42L);

        Assert.Equal(1L, histogram.Count);
        Assert.Equal(0L, histogram.Snapshot().MaxMicroseconds);
    }

    [Fact]
    public void The_footprint_stays_constant_whatever_the_sample_count()
    {
        // 3 072 paniers de 8 octets : 24 Ko, quel que soit le nombre de mesures.
        Assert.Equal(3_072, LatencyHistogram.BucketCount);
    }

    private static void AssertWithinTolerance(long expected, long actual)
    {
        double upperBound = (expected * (1d + LatencyHistogram.RELATIVE_ERROR)) + 1d;

        Assert.True(
            actual >= expected && actual <= upperBound,
            $"Attendu dans [{expected}, {upperBound:F0}], obtenu {actual}.");
    }
}