using System.Net.WebSockets;
using Tempest.Domain.Data;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Data;

public sealed class DataSetTests
{
    private static Dictionary<string, string> Row(string value) =>
        new(StringComparer.Ordinal) { ["value"] = value };

    [Fact]
    public void An_empty_data_set_is_rejected_at_construction() =>
        Assert.Throws<ArgumentException>(() => new DataSet([], DataSetIterationStrategy.Circular));

    [Fact]
    public void Unique_per_virtual_user_always_returns_the_same_row_for_the_same_virtual_user()
    {
        DataSet dataSet = new([Row("a"), Row("b"), Row("c")], DataSetIterationStrategy.UniquePerVirtualUser);
        FakeVirtualUserContext context = new(virtualUserId: 1);

        string first = dataSet.Pick(context)["value"];
        string second = dataSet.Pick(context)["value"];

        Assert.Equal("b", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Unique_per_virtual_user_wraps_around_when_there_are_fewer_rows_than_virtual_users()
    {
        DataSet dataSet = new([Row("a"), Row("b")], DataSetIterationStrategy.UniquePerVirtualUser);

        Assert.Equal("a", dataSet.Pick(new FakeVirtualUserContext(virtualUserId: 2))["value"]);
        Assert.Equal("b", dataSet.Pick(new FakeVirtualUserContext(virtualUserId: 3))["value"]);
    }

    [Fact]
    public void Circular_advances_through_every_row_before_repeating()
    {
        DataSet dataSet = new([Row("a"), Row("b"), Row("c")], DataSetIterationStrategy.Circular);
        FakeVirtualUserContext context = new(virtualUserId: 0);

        string[] picked = [dataSet.Pick(context)["value"], dataSet.Pick(context)["value"], dataSet.Pick(context)["value"]];

        Assert.Equal(["a", "b", "c"], picked);
        Assert.Equal("a", dataSet.Pick(context)["value"]);
    }

    [Fact]
    public void Random_always_picks_a_row_from_the_set()
    {
        DataSet dataSet = new([Row("a"), Row("b")], DataSetIterationStrategy.Random);
        FakeVirtualUserContext context = new(virtualUserId: 0);

        for (int i = 0; i < 20; i++)
        {
            Assert.True(dataSet.Pick(context)["value"] is "a" or "b");
        }
    }

    [Fact]
    public void A_null_context_is_rejected()
    {
        DataSet dataSet = new([Row("a")], DataSetIterationStrategy.Circular);

        Assert.Throws<ArgumentNullException>(() => dataSet.Pick(null!));
    }

    /// <summary>
    /// Double minimal : <see cref="DataSet.Pick"/> ne lit que <see cref="VirtualUserId"/>, le
    /// reste n'a donc pas besoin d'etre fonctionnel.
    /// </summary>
    private sealed class FakeVirtualUserContext(int virtualUserId) : IVirtualUserContext
    {
        public int VirtualUserId { get; } = virtualUserId;

        public long IterationNumber => 0;

        public long ScheduledTicks => 0;

        public HttpClient HttpClient => throw new NotSupportedException();

        public CancellationToken CancellationToken => CancellationToken.None;

        public object? State { get; set; }

        public StepScope BeginStep(StepId step) => throw new NotSupportedException();

        public void Report(in MetricResult result) => throw new NotSupportedException();

        public Task<WebSocketConnection> ConnectWebSocketAsync(
            Uri uri, Action<ClientWebSocketOptions>? configureOptions, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}