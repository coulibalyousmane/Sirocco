using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class CustomMetricRegistryTests
{
    [Fact]
    public void Registering_a_new_name_assigns_a_dense_id_starting_at_zero()
    {
        CustomMetricRegistry registry = new();

        CustomMetricId first = registry.Register("orders_total", CustomMetricKind.Counter);
        CustomMetricId second = registry.Register("active_carts", CustomMetricKind.Gauge);

        Assert.Equal(0, first.Value);
        Assert.Equal(1, second.Value);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Registering_the_same_name_twice_with_the_same_kind_returns_the_same_id()
    {
        CustomMetricRegistry registry = new();

        CustomMetricId first = registry.Register("orders_total", CustomMetricKind.Counter);
        CustomMetricId second = registry.Register("orders_total", CustomMetricKind.Counter);

        Assert.Equal(first, second);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Registering_the_same_name_with_a_different_kind_is_rejected()
    {
        CustomMetricRegistry registry = new();
        registry.Register("orders_total", CustomMetricKind.Counter);

        Assert.Throws<ArgumentException>(() => registry.Register("orders_total", CustomMetricKind.Gauge));
    }

    [Fact]
    public void GetName_resolves_the_name_of_a_registered_metric()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId id = registry.Register("orders_total", CustomMetricKind.Counter);

        Assert.Equal("orders_total", registry.GetName(id));
    }

    [Fact]
    public void GetKind_resolves_the_kind_of_a_registered_metric()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId id = registry.Register("order_value", CustomMetricKind.Trend);

        Assert.Equal(CustomMetricKind.Trend, registry.GetKind(id));
    }

    [Fact]
    public void TryGetId_finds_a_registered_name()
    {
        CustomMetricRegistry registry = new();
        registry.Register("orders_total", CustomMetricKind.Counter);

        Assert.True(registry.TryGetId("orders_total", out CustomMetricId id));
        Assert.Equal(0, id.Value);
    }

    [Fact]
    public void TryGetId_does_not_find_an_unknown_name()
    {
        CustomMetricRegistry registry = new();

        Assert.False(registry.TryGetId("unknown", out _));
    }

    [Fact]
    public void A_sealed_registry_rejects_a_brand_new_name()
    {
        CustomMetricRegistry registry = new();
        registry.Seal();

        Assert.Throws<InvalidOperationException>(() => registry.Register("orders_total", CustomMetricKind.Counter));
    }

    [Fact]
    public void A_sealed_registry_still_returns_the_id_of_an_already_known_name()
    {
        CustomMetricRegistry registry = new();
        CustomMetricId id = registry.Register("orders_total", CustomMetricKind.Counter);
        registry.Seal();

        Assert.Equal(id, registry.Register("orders_total", CustomMetricKind.Counter));
    }
}