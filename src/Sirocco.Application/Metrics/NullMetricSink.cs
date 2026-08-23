using Sirocco.Domain.Metrics;

namespace Sirocco.Application.Metrics;

/// <summary>
/// Puits qui compte les mesures sans les conserver. Sert a mesurer le cout propre du
/// moteur (etalonnage de l'injecteur) en retirant l'agregation de l'equation.
/// </summary>
public sealed class NullMetricSink : IMetricSink
{
    private long _count;

    /// <summary>Nombre de mesures recues.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <inheritdoc />
    public long DroppedMetrics => 0;

    /// <inheritdoc />
    public bool TryWrite(in MetricResult result)
    {
        Interlocked.Increment(ref _count);
        return true;
    }
}