using Tempest.Application.Metrics;
using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class CustomMetricsAggregatorTests
{
    [Fact]
    public void Constructing_with_an_empty_registry_does_not_throw_and_yields_an_empty_snapshot()
    {
        CustomMetricsAggregator aggregator = new(new CustomMetricRegistry());

        Assert.Empty(aggregator.Snapshot());
    }

    [Fact]
    public void A_counter_accumulates_the_sum_of_every_recorded_value()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId ordersTotal = registry.Register("orders_total", CustomMetricKind.Counter);
        registry.Seal();

        CustomMetricsAggregator aggregator = new(registry);
        aggregator.Record(new CustomMetricResult(ordersTotal, 1d));
        aggregator.Record(new CustomMetricResult(ordersTotal, 2d));
        aggregator.Record(new CustomMetricResult(ordersTotal, 3d));

        CustomMetricSnapshot snapshot = Assert.Single(aggregator.Snapshot());
        Assert.Equal("orders_total", snapshot.Name);
        Assert.Equal(CustomMetricKind.Counter, snapshot.Kind);
        Assert.Equal(3L, snapshot.Count);
        Assert.Equal(6d, snapshot.Sum);
    }

    [Fact]
    public void A_gauge_keeps_only_the_last_recorded_value()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId activeCarts = registry.Register("active_carts", CustomMetricKind.Gauge);
        registry.Seal();

        CustomMetricsAggregator aggregator = new(registry);
        aggregator.Record(new CustomMetricResult(activeCarts, 5d));
        aggregator.Record(new CustomMetricResult(activeCarts, 3d));
        aggregator.Record(new CustomMetricResult(activeCarts, 8d));

        CustomMetricSnapshot snapshot = Assert.Single(aggregator.Snapshot());
        Assert.Equal(8d, snapshot.Last);
    }

    [Fact]
    public void A_rate_is_the_mean_of_its_zero_and_one_values()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId cacheHitRate = registry.Register("cache_hit_rate", CustomMetricKind.Rate);
        registry.Seal();

        CustomMetricsAggregator aggregator = new(registry);
        aggregator.Record(new CustomMetricResult(cacheHitRate, 1d));
        aggregator.Record(new CustomMetricResult(cacheHitRate, 1d));
        aggregator.Record(new CustomMetricResult(cacheHitRate, 1d));
        aggregator.Record(new CustomMetricResult(cacheHitRate, 0d));

        CustomMetricSnapshot snapshot = Assert.Single(aggregator.Snapshot());
        Assert.Equal(0.75d, snapshot.Mean);
    }

    [Fact]
    public void A_trend_tracks_min_max_and_mean()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId orderValue = registry.Register("order_value", CustomMetricKind.Trend);
        registry.Seal();

        CustomMetricsAggregator aggregator = new(registry);
        aggregator.Record(new CustomMetricResult(orderValue, 10d));
        aggregator.Record(new CustomMetricResult(orderValue, 50d));
        aggregator.Record(new CustomMetricResult(orderValue, 30d));

        CustomMetricSnapshot snapshot = Assert.Single(aggregator.Snapshot());
        Assert.Equal(10d, snapshot.Min);
        Assert.Equal(50d, snapshot.Max);
        Assert.Equal(30d, snapshot.Mean);
    }

    [Fact]
    public void A_metric_never_recorded_reports_zero_everywhere_but_keeps_its_name_and_kind()
    {
        CustomMetricRegistry registry = new();
        registry.Register("orders_total", CustomMetricKind.Counter);
        registry.Seal();

        CustomMetricsAggregator aggregator = new(registry);

        CustomMetricSnapshot snapshot = Assert.Single(aggregator.Snapshot());
        Assert.Equal("orders_total", snapshot.Name);
        Assert.Equal(0L, snapshot.Count);
        Assert.Equal(0d, snapshot.Min);
        Assert.Equal(0d, snapshot.Max);
    }

    /// <summary>
    /// Reproduit le meme ordre que <c>MetricsAggregatorTests</c> pour son homologue natif : un
    /// conteneur d'injection de dependances peut resoudre cet agregateur avant que le moteur
    /// n'ait rempli et scelle le registre au demarrage du tir.
    /// </summary>
    [Fact]
    public void The_registry_can_be_filled_and_sealed_after_the_aggregator_was_constructed()
    {
        CustomMetricRegistry registry = new();
        CustomMetricsAggregator aggregator = new(registry);

        CustomMetricId ordersTotal = registry.Register("orders_total", CustomMetricKind.Counter);
        registry.Seal();

        aggregator.Record(new CustomMetricResult(ordersTotal, 5d));

        CustomMetricSnapshot snapshot = Assert.Single(aggregator.Snapshot());
        Assert.Equal(5d, snapshot.Sum);
    }

    [Fact]
    public void An_unknown_metric_is_ignored_rather_than_throwing()
    {
        CustomMetricsAggregator aggregator = new(new CustomMetricRegistry());

        Exception? escaped = Record.Exception(() => aggregator.Record(new CustomMetricResult(new CustomMetricId(0), 1d)));

        Assert.Null(escaped);
    }
}