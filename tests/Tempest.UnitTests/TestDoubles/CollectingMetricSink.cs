using System.Collections.Concurrent;
using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.TestDoubles;

/// <summary>Puits de test qui conserve toutes les mesures recues.</summary>
internal sealed class CollectingMetricSink : IMetricSink
{
    private readonly ConcurrentQueue<MetricResult> _results = new();

    public long DroppedMetrics => 0;

    public IReadOnlyCollection<MetricResult> Results => _results;

    public bool TryWrite(in MetricResult result)
    {
        _results.Enqueue(result);
        return true;
    }

    public IEnumerable<MetricResult> For(StepId step) => _results.Where(r => r.Step == step);
}