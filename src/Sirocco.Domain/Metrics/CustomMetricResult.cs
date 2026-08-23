namespace Sirocco.Domain.Metrics;

/// <summary>
/// Mesure immuable d'une métrique personnalisée. Structure <b>unmanaged</b> (aucune référence),
/// pour les mêmes raisons que <see cref="MetricResult"/> : elle transite par un canal borné sans
/// jamais déclencher d'allocation sur le tas ni de scan GC.
/// </summary>
/// <param name="Metric">Métrique concernée, résolue via <see cref="CustomMetricRegistry"/>.</param>
/// <param name="Value">
/// Valeur mesurée. Son interprétation dépend du type de la métrique : un incrément pour un
/// compteur, la valeur courante pour une jauge, 0 ou 1 pour un taux, une observation pour une
/// tendance — l'agrégation (<c>CustomMetricAccumulator</c>) fait le reste.
/// </param>
public readonly record struct CustomMetricResult(CustomMetricId Metric, double Value);