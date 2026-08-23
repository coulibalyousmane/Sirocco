namespace Sirocco.Domain.Metrics;

/// <summary>
/// Puits qui accepte silencieusement toute mesure sans la conserver. Valeur par défaut de
/// <see cref="Execution.VirtualUserContext"/> quand aucun puits réel n'est fourni — la grande
/// majorité des scénarios (et tous les tests qui ne portent pas sur les métriques
/// personnalisées) n'appellent jamais <see cref="Execution.IVirtualUserContext.RecordCustomMetric"/>,
/// et ne devraient pas avoir à câbler un puits pour autant.
/// </summary>
public sealed class NullCustomMetricSink : ICustomMetricSink
{
    /// <summary>Instance partagée : ce puits n'a aucun état, une seule suffit.</summary>
    public static readonly NullCustomMetricSink Instance = new();

    private NullCustomMetricSink()
    {
    }

    /// <inheritdoc />
    public bool TryWrite(in CustomMetricResult result) => true;

    /// <inheritdoc />
    public long DroppedMetrics => 0L;
}