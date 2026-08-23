using Sirocco.Application.Metrics;
using Sirocco.Domain.Metrics;

namespace Sirocco.UnitTests.Metrics;

public sealed class ChannelMetricSinkTests
{
    private static MetricResult Sample(int virtualUserId = 0) =>
        new(new StepId(0), virtualUserId, 0, 0, 100, 200, RequestOutcome.Success, 0);

    [Fact]
    public void Accepted_metrics_reach_the_reader_in_order()
    {
        ChannelMetricSink sink = new(capacity: 8);

        Assert.True(sink.TryWrite(Sample(1)));
        Assert.True(sink.TryWrite(Sample(2)));
        sink.Complete();

        Assert.True(sink.Reader.TryRead(out var first));
        Assert.True(sink.Reader.TryRead(out var second));
        Assert.Equal(1, first.VirtualUserId);
        Assert.Equal(2, second.VirtualUserId);
        Assert.Equal(2, sink.AcceptedMetrics);
        Assert.Equal(0, sink.DroppedMetrics);
    }

    /// <summary>
    /// Une file pleine doit renvoyer <see langword="false"/> et compter la perte.
    /// Le mode <c>DropWrite</c> renverrait <see langword="true"/> en jetant en silence,
    /// et le rapport final annoncerait des percentiles calcules sur un echantillon tronque
    /// sans jamais le signaler.
    /// </summary>
    [Fact]
    public void Overflow_is_refused_and_counted_never_silent()
    {
        ChannelMetricSink sink = new(capacity: 2);

        Assert.True(sink.TryWrite(Sample()));
        Assert.True(sink.TryWrite(Sample()));
        Assert.False(sink.TryWrite(Sample()));
        Assert.False(sink.TryWrite(Sample()));

        Assert.Equal(2, sink.AcceptedMetrics);
        Assert.Equal(2, sink.DroppedMetrics);
    }

    [Fact]
    public void Draining_frees_room_again()
    {
        ChannelMetricSink sink = new(capacity: 1);

        Assert.True(sink.TryWrite(Sample()));
        Assert.False(sink.TryWrite(Sample()));

        Assert.True(sink.Reader.TryRead(out _));
        Assert.True(sink.TryWrite(Sample()));
    }

    [Fact]
    public async Task Concurrent_producers_lose_nothing_when_the_reader_keeps_up()
    {
        const int producers = 8;
        const int perProducer = 2_000;

        ChannelMetricSink sink = new(capacity: 1 << 14);

        int consumed = 0;
        Task consumer = Task.Run(async () =>
        {
            await foreach (var _ in sink.Reader.ReadAllAsync())
            {
                consumed++;
            }
        });

        await Task.WhenAll(Enumerable.Range(0, producers).Select(id => Task.Run(() =>
        {
            for (int i = 0; i < perProducer; i++)
            {
                while (!sink.TryWrite(Sample(id)))
                {
                    Thread.SpinWait(50);
                }
            }
        })));

        sink.Complete();
        await consumer;

        Assert.Equal(producers * perProducer, consumed);
        Assert.Equal(producers * perProducer, sink.AcceptedMetrics);
    }

    [Fact]
    public void A_non_positive_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelMetricSink(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelMetricSink(-1));
    }

    [Fact]
    public void The_null_sink_counts_without_retaining()
    {
        NullMetricSink sink = new();

        Assert.True(sink.TryWrite(Sample()));
        Assert.True(sink.TryWrite(Sample()));

        Assert.Equal(2, sink.Count);
        Assert.Equal(0, sink.DroppedMetrics);
    }
}