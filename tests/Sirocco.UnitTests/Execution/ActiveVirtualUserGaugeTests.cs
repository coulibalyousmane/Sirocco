using Sirocco.Application.Execution;

namespace Sirocco.UnitTests.Execution;

public sealed class ActiveVirtualUserGaugeTests
{
    [Fact]
    public void A_new_gauge_starts_at_zero() =>
        Assert.Equal(0, new ActiveVirtualUserGauge().Value);

    [Fact]
    public void Increment_and_decrement_track_the_current_count()
    {
        ActiveVirtualUserGauge gauge = new();

        gauge.Increment();
        gauge.Increment();
        gauge.Increment();
        Assert.Equal(3, gauge.Value);

        gauge.Decrement();
        Assert.Equal(2, gauge.Value);
    }

    [Fact]
    public async Task Concurrent_increments_and_decrements_never_lose_a_count()
    {
        ActiveVirtualUserGauge gauge = new();
        const int WorkerCount = 64;

        await Task.WhenAll(Enumerable.Range(0, WorkerCount).Select(_ => Task.Run(() =>
        {
            gauge.Increment();
            gauge.Decrement();
            gauge.Increment();
        })));

        Assert.Equal(WorkerCount, gauge.Value);
    }
}