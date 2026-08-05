using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class LatencySnapshotTests
{
    private static readonly LatencySnapshot _snapshot = new(
        Count: 100L,
        MinMicroseconds: 1_000L,
        MaxMicroseconds: 500_000L,
        MeanMicroseconds: 42_000d,
        P50Microseconds: 40_000L,
        P75Microseconds: 60_000L,
        P90Microseconds: 80_000L,
        P95Microseconds: 90_000L,
        P99Microseconds: 120_000L,
        P999Microseconds: 480_000L);

    [Fact]
    public void Every_percentile_converts_microseconds_to_milliseconds()
    {
        Assert.Equal(40d, _snapshot.P50Milliseconds, 1e-9);
        Assert.Equal(60d, _snapshot.P75Milliseconds, 1e-9);
        Assert.Equal(80d, _snapshot.P90Milliseconds, 1e-9);
        Assert.Equal(90d, _snapshot.P95Milliseconds, 1e-9);
        Assert.Equal(120d, _snapshot.P99Milliseconds, 1e-9);
        Assert.Equal(480d, _snapshot.P999Milliseconds, 1e-9);
        Assert.Equal(500d, _snapshot.MaxMilliseconds, 1e-9);
        Assert.Equal(42d, _snapshot.MeanMilliseconds, 1e-9);
    }

    [Fact]
    public void The_empty_snapshot_reports_zero_everywhere()
    {
        Assert.True(LatencySnapshot.Empty.IsEmpty);
        Assert.Equal(0d, LatencySnapshot.Empty.P75Milliseconds);
        Assert.Equal(0d, LatencySnapshot.Empty.P90Milliseconds);
    }
}