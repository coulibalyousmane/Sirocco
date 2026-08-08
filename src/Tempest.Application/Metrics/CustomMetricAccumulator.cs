using Tempest.Domain.Metrics;

namespace Tempest.Application.Metrics;

/// <summary>
/// Etat agrege d'une seule metrique personnalisee : compte, somme, min, max et derniere valeur.
/// <para>
/// Plus simple que <see cref="StepAccumulator"/> volontairement : pas de fenetre glissante en
/// v1 (une seule photographie cumulee), et pas d'histogramme pour la tendance (voir
/// <see cref="CustomMetricSnapshot"/>) — ces deux limites sont documentees comme telles plutot
/// que devinees, et pourront etre levees si le besoin se confirme.
/// </para>
/// </summary>
internal sealed class CustomMetricAccumulator(CustomMetricId metric, string name, CustomMetricKind kind)
{
    private readonly Lock _gate = new();

    private long _count;
    private double _sum;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;
    private double _last;

    public CustomMetricId Metric { get; } = metric;

    public string Name { get; } = name;

    public CustomMetricKind Kind { get; } = kind;

    /// <summary>Agrege une valeur. Appele une fois par mesure, sur le thread de l'agregateur.</summary>
    public void Record(double value)
    {
        lock (_gate)
        {
            _count++;
            _sum += value;

            if (value < _min)
            {
                _min = value;
            }

            if (value > _max)
            {
                _max = value;
            }

            _last = value;
        }
    }

    /// <summary>Photographie l'etat cumule de la metrique.</summary>
    public CustomMetricSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new CustomMetricSnapshot
            {
                Name = Name,
                Kind = Kind,
                Count = _count,
                Sum = _sum,
                Min = _count > 0 ? _min : 0d,
                Max = _count > 0 ? _max : 0d,
                Last = _last,
            };
        }
    }
}