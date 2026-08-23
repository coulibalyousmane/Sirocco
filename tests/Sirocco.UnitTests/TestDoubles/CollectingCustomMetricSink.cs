using System.Collections.Concurrent;
using Sirocco.Domain.Metrics;

namespace Sirocco.UnitTests.TestDoubles;

/// <summary>Puits de test qui conserve toutes les metriques personnalisees recues.</summary>
internal sealed class CollectingCustomMetricSink : ICustomMetricSink
{
    private readonly ConcurrentQueue<CustomMetricResult> _results = new();

    public long DroppedMetrics => 0;

    public IReadOnlyCollection<CustomMetricResult> Results => _results;

    public bool TryWrite(in CustomMetricResult result)
    {
        _results.Enqueue(result);
        return true;
    }

    public IEnumerable<double> ValuesFor(CustomMetricId metric) => _results.Where(r => r.Metric == metric).Select(r => r.Value);
}