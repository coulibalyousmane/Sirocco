using Tempest.Domain.Metrics;

namespace Tempest.Application.Metrics;

/// <summary>
/// Agrege les metriques personnalisees en statistiques exploitables — meme discipline que
/// <see cref="MetricsAggregator"/> (tableau dimensionne une fois pour toutes d'apres le
/// <see cref="CustomMetricRegistry"/> scelle, acces par index, construction paresseuse), mais
/// sur un seul perimetre cumule : voir la limite documentee sur <see cref="CustomMetricAccumulator"/>.
/// </summary>
public sealed class CustomMetricsAggregator
{
    private readonly CustomMetricRegistry _metrics;
    private readonly Lock _buildGate = new();

    private CustomMetricAccumulator[]? _accumulators;

    /// <summary>Cree un agregateur pour un registre de metriques personnalisees.</summary>
    /// <param name="metrics">Registre de metriques. Peut encore etre vide a cet instant.</param>
    public CustomMetricsAggregator(CustomMetricRegistry metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        _metrics = metrics;
    }

    /// <summary>
    /// Agrege une mesure. Une metrique inconnue, ou un registre pas encore rempli, est ignoree
    /// en silence plutot que de faire tomber le processeur.
    /// </summary>
    public void Record(in CustomMetricResult result)
    {
        CustomMetricAccumulator[]? accumulators = EnsureAccumulators();
        if (accumulators is null)
        {
            return;
        }

        int index = result.Metric.Value;
        if ((uint)index >= (uint)accumulators.Length)
        {
            return;
        }

        accumulators[index].Record(result.Value);
    }

    /// <summary>Photographie l'ensemble des metriques personnalisees.</summary>
    public IReadOnlyList<CustomMetricSnapshot> Snapshot()
    {
        CustomMetricAccumulator[]? accumulators = EnsureAccumulators();
        if (accumulators is null)
        {
            return [];
        }

        CustomMetricSnapshot[] snapshots = new CustomMetricSnapshot[accumulators.Length];
        for (int i = 0; i < accumulators.Length; i++)
        {
            snapshots[i] = accumulators[i].Snapshot();
        }

        return snapshots;
    }

    /// <summary>
    /// Construit les accumulateurs au premier usage reel, jamais a la construction : le registre
    /// n'est rempli et scelle qu'au demarrage du tir, pas forcement avant que cet agregateur soit
    /// resolu par le conteneur d'injection de dependances.
    /// </summary>
    private CustomMetricAccumulator[]? EnsureAccumulators()
    {
        if (_accumulators is { } built)
        {
            return built;
        }

        if (_metrics.Count == 0)
        {
            return null;
        }

        lock (_buildGate)
        {
            if (_accumulators is { } builtWhileWaiting)
            {
                return builtWhileWaiting;
            }

            CustomMetricAccumulator[] accumulators = new CustomMetricAccumulator[_metrics.Count];
            for (int i = 0; i < accumulators.Length; i++)
            {
                CustomMetricId id = new(i);
                accumulators[i] = new CustomMetricAccumulator(id, _metrics.GetName(id), _metrics.GetKind(id));
            }

            _accumulators = accumulators;
            return accumulators;
        }
    }
}