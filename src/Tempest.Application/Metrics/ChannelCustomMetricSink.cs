using System.Threading.Channels;
using Tempest.Domain.Metrics;

namespace Tempest.Application.Metrics;

/// <summary>
/// Pont non bloquant entre les utilisateurs virtuels (producteurs) et l'agregateur de metriques
/// personnalisees (consommateur unique) — meme construction que <see cref="ChannelMetricSink"/>,
/// sur un canal separe : une metrique personnalisee ne doit jamais retarder la publication des
/// mesures natives, ni l'inverse.
/// </summary>
public sealed class ChannelCustomMetricSink : ICustomMetricSink
{
    /// <summary>Capacite par defaut : 65 536 mesures.</summary>
    public const int DEFAULT_CAPACITY = 1 << 16;

    private readonly Channel<CustomMetricResult> _channel;
    private long _dropped;
    private long _accepted;

    /// <summary>Cree un puits de metriques personnalisees.</summary>
    /// <param name="capacity">Nombre maximal de mesures en attente d'agregation.</param>
    public ChannelCustomMetricSink(int capacity = DEFAULT_CAPACITY)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _channel = Channel.CreateBounded<CustomMetricResult>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Flux de mesures a consommer par l'agregateur.</summary>
    public ChannelReader<CustomMetricResult> Reader => _channel.Reader;

    /// <inheritdoc />
    public long DroppedMetrics => Interlocked.Read(ref _dropped);

    /// <summary>Nombre de mesures effectivement publiees.</summary>
    public long AcceptedMetrics => Interlocked.Read(ref _accepted);

    /// <inheritdoc />
    public bool TryWrite(in CustomMetricResult result)
    {
        if (_channel.Writer.TryWrite(result))
        {
            Interlocked.Increment(ref _accepted);
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Signale la fin du test : le consommateur pourra terminer sa boucle apres drainage.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}