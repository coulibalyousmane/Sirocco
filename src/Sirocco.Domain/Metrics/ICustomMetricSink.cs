namespace Sirocco.Domain.Metrics;

/// <summary>
/// Point de sortie des métriques personnalisées depuis le chemin critique — même contrat non
/// bloquant que <see cref="IMetricSink"/>, sur un canal séparé : une métrique personnalisée ne
/// doit jamais faire attendre ni ralentir la publication des mesures natives.
/// </summary>
public interface ICustomMetricSink
{
    /// <summary>
    /// Publie une mesure sans jamais bloquer.
    /// </summary>
    /// <returns><see langword="false"/> si la mesure a été rejetée faute de place.</returns>
    bool TryWrite(in CustomMetricResult result);

    /// <summary>Nombre d'écritures rejetées depuis le début du test.</summary>
    long DroppedMetrics { get; }
}