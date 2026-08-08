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
/// <para>
/// Les metriques personnalisees suivent le meme chemin sur un <b>second</b> canal et un
/// <b>second</b> consommateur, plutot qu'un seul consommateur draine les deux : deux boucles
/// independantes restent aussi simples a raisonner qu'une seule, sans avoir a multiplexer deux
/// <see cref="ChannelReader{T}"/> de types differents dans la meme boucle.
/// </para>
/// </summary>
public sealed class MetricsProcessor : IAsyncDisposable
{
    private readonly ChannelMetricSink _sink;
    private readonly ChannelCustomMetricSink _customMetricSink;
    private readonly CustomMetricsAggregator _customMetricsAggregator;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _consumer;
    private Task? _customMetricConsumer;

    /// <summary>Cree le processeur.</summary>
    /// <param name="sink">Puits alimente par les utilisateurs virtuels.</param>
    /// <param name="aggregator">Destination des mesures agregees.</param>
    /// <param name="customMetricSink">Puits de metriques personnalisees alimente par les utilisateurs virtuels.</param>
    /// <param name="customMetricsAggregator">Destination des metriques personnalisees agregees.</param>
    public MetricsProcessor(
        ChannelMetricSink sink,
        MetricsAggregator aggregator,
        ChannelCustomMetricSink? customMetricSink = null,
        CustomMetricsAggregator? customMetricsAggregator = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(aggregator);

        _sink = sink;
        Aggregator = aggregator;
        _customMetricSink = customMetricSink ?? new ChannelCustomMetricSink();
        _customMetricsAggregator = customMetricsAggregator ?? new CustomMetricsAggregator(new CustomMetricRegistry());
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
        _customMetricConsumer = Task.Run(() => ConsumeCustomMetricsAsync(_stopping.Token), CancellationToken.None);
    }

    /// <summary>
    /// Clot les puits puis attend que la totalite des mesures en attente soit agregee.
    /// <para>
    /// L'attente du drainage n'est pas une politesse : couper ici perdrait la queue du tir,
    /// c'est-a-dire precisement les mesures prises quand la cible etait la plus sollicitee.
    /// </para>
    /// </summary>
    public async Task StopAsync()
    {
        _sink.Complete();
        _customMetricSink.Complete();

        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
        }

        if (_customMetricConsumer is not null)
        {
            await _customMetricConsumer.ConfigureAwait(false);
        }

        Aggregator.MetricsDropped = _sink.DroppedMetrics;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        foreach (Task? consumer in new[] { _consumer, _customMetricConsumer })
        {
            if (consumer is not null)
            {
                try
                {
                    await consumer.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Arret force : rien a signaler.
                }
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

    private async Task ConsumeCustomMetricsAsync(CancellationToken cancellationToken)
    {
        ChannelReader<CustomMetricResult> reader = _customMetricSink.Reader;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out CustomMetricResult result))
                {
                    _customMetricsAggregator.Record(in result);
                }
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
    }
}