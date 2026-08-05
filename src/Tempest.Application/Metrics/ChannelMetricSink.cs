using System.Threading.Channels;
using Tempest.Domain.Metrics;

namespace Tempest.Application.Metrics;

/// <summary>
/// Pont non bloquant entre les utilisateurs virtuels (producteurs) et l'agregateur
/// de metriques (consommateur unique), bati sur un canal borne sans verrou.
/// <para>
/// Le canal est <b>borne</b> volontairement : une file non bornee absorberait un
/// consommateur trop lent en gonflant le tas jusqu'a l'ecroulement, en degradant
/// silencieusement la precision du test. Ici, un debordement est compte et remonte.
/// </para>
/// </summary>
public sealed class ChannelMetricSink : IMetricSink
{
    /// <summary>Capacite par defaut : 65 536 mesures, soit environ 3 Mo.</summary>
    public const int DEFAULT_CAPACITY = 1 << 16;

    private readonly Channel<MetricResult> _channel;
    private long _dropped;
    private long _accepted;

    /// <summary>Cree un puits de metriques.</summary>
    /// <param name="capacity">Nombre maximal de mesures en attente d'agregation.</param>
    public ChannelMetricSink(int capacity = DEFAULT_CAPACITY)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _channel = Channel.CreateBounded<MetricResult>(new BoundedChannelOptions(capacity)
        {
            // FullMode.Wait : TryWrite renvoie false quand le canal est plein, ce qui nous
            // permet de compter la perte. DropWrite renverrait true en jetant en silence.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Flux de mesures a consommer par l'agregateur.</summary>
    public ChannelReader<MetricResult> Reader => _channel.Reader;

    /// <inheritdoc />
    public long DroppedMetrics => Interlocked.Read(ref _dropped);

    /// <summary>Nombre de mesures effectivement publiees.</summary>
    public long AcceptedMetrics => Interlocked.Read(ref _accepted);

    /// <inheritdoc />
    public bool TryWrite(in MetricResult result)
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