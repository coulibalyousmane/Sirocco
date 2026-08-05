using System.Threading.Channels;
using Tempest.Application.Metrics;
using Tempest.Domain.Metrics;

namespace Tempest.Infrastructure.Metrics;

/// <summary>
/// Consommateur unique du canal de mesures : draine le puits et alimente l'agregateur.
/// <para>
/// C'est le seul point du systeme ou les mesures de tous les utilisateurs virtuels se
/// rejoignent. Le tenir sur un unique consommateur evite toute contention entre les
/// travailleurs : cote emission il n'y a qu'un <c>TryWrite</c> sans verrou, et l'agregation,
/// forcement sequentielle, se paie une seule fois au lieu d'une par thread.
/// </para>
/// </summary>
public sealed class MetricsProcessor : IAsyncDisposable
{
    private readonly ChannelMetricSink _sink;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _consumer;

    /// <summary>Cree le processeur.</summary>
    /// <param name="sink">Puits alimente par les utilisateurs virtuels.</param>
    /// <param name="aggregator">Destination des mesures agregees.</param>
    public MetricsProcessor(ChannelMetricSink sink, MetricsAggregator aggregator)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(aggregator);

        _sink = sink;
        Aggregator = aggregator;
    }

    /// <summary>Agregateur alimente par ce processeur.</summary>
    public MetricsAggregator Aggregator { get; }

    /// <summary>Indique que la consommation est en cours.</summary>
    public bool IsRunning => _consumer is { IsCompleted: false };

    /// <summary>Demarre la consommation en arriere-plan.</summary>
    /// <exception cref="InvalidOperationException">Le processeur tourne deja.</exception>
    public void Start()
    {
        if (_consumer is not null)
        {
            throw new InvalidOperationException("Le processeur de metriques a deja ete demarre.");
        }

        _consumer = Task.Run(() => ConsumeAsync(_stopping.Token), CancellationToken.None);
    }

    /// <summary>
    /// Clot le puits puis attend que la totalite des mesures en attente soit agregee.
    /// <para>
    /// L'attente du drainage n'est pas une politesse : couper ici perdrait la queue du tir,
    /// c'est-a-dire precisement les mesures prises quand la cible etait la plus sollicitee.
    /// </para>
    /// </summary>
    public async Task StopAsync()
    {
        _sink.Complete();

        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
        }

        Aggregator.MetricsDropped = _sink.DroppedMetrics;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_consumer is not null)
        {
            try
            {
                await _consumer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Arret force : rien a signaler.
            }
        }

        _stopping.Dispose();
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        ChannelReader<MetricResult> reader = _sink.Reader;

        try
        {
            // Un seul lecteur : WaitToReadAsync est ici sans danger, contrairement au canal
            // de jetons ou N travailleurs se reveillaient tous a chaque ecriture.
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out MetricResult result))
                {
                    Aggregator.Record(in result);
                }

                Aggregator.MetricsDropped = _sink.DroppedMetrics;
            }
        }
        catch (OperationCanceledException)
        {
            // Arret demande.
        }
        catch (ChannelClosedException)
        {
            // Fin de tir.
        }

        Aggregator.MetricsDropped = _sink.DroppedMetrics;
    }
}