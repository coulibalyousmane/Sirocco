using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class StepRegistryTests
{
    [Fact]
    public void Ids_are_dense_and_sequential()
    {
        StepRegistry registry = new();

        Assert.Equal(0, registry.Register("login").Value);
        Assert.Equal(1, registry.Register("browse").Value);
        Assert.Equal(2, registry.Register("checkout").Value);
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public void Registering_the_same_name_twice_returns_the_same_id()
    {
        StepRegistry registry = new();

        var first = registry.Register("login");
        var second = registry.Register("login");

        Assert.Equal(first, second);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Names_are_case_sensitive()
    {
        StepRegistry registry = new();

        Assert.NotEqual(registry.Register("Login"), registry.Register("login"));
    }

    [Fact]
    public void GetName_round_trips_the_id()
    {
        StepRegistry registry = new();
        var id = registry.Register("checkout");

        Assert.Equal("checkout", registry.GetName(id));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.GetName(new StepId(42)));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.GetName(StepId.None));
    }

    [Fact]
    public void Sealing_blocks_new_names_but_still_resolves_known_ones()
    {
        StepRegistry registry = new();
        var login = registry.Register("login");
        registry.Seal();

        Assert.Equal(login, registry.Register("login"));
        Assert.Throws<InvalidOperationException>(() => registry.Register("late-step"));
    }

    [Fact]
    public void Blank_names_are_rejected()
    {
        StepRegistry registry = new();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
        Assert.Throws<ArgumentException>(() => registry.Register("   "));
    }

    [Fact]
    public void StepId_None_is_invalid()
    {
        Assert.False(StepId.None.IsValid);
        Assert.True(new StepId(0).IsValid);
    }
}