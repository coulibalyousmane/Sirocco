using Sirocco.Application.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;

namespace Sirocco.Application.Metrics;

/// <summary>
/// Releve periodiquement une photographie glissante du tir en cours et en garde la trace, pour
/// produire une trajectoire (<see cref="LoadTestReport.TimeSeries"/>) plutot qu'un seul etat
/// final.
/// <para>
/// Tourne en parallele du moteur, jamais a l'interieur de lui : <see cref="Execution.TargetRpsLoadEngine"/>
/// ne detient pas de reference vers un <see cref="MetricsAggregator"/> (il ne fait qu'ecrire des
/// mesures dans un puits), donc le releve periodique vit un niveau au-dessus, cote appelant, qui
/// detient les deux.
/// </para>
/// </summary>
public sealed class TimeSeriesRecorder
{
    private readonly MetricsAggregator _aggregator;
    private readonly ActiveVirtualUserGauge _activeVirtualUsers;
    private readonly TimeSpan _interval;
    private readonly List<TimeSeriesSample> _samples = [];

    private long _startTicks;

    /// <summary>Cree un enregistreur.</summary>
    /// <param name="aggregator">Source des statistiques glissantes.</param>
    /// <param name="activeVirtualUsers">Jauge de concurrence reelle a relever au meme instant.</param>
    /// <param name="interval">Ecart entre deux releves. Doit etre strictement positif.</param>
    public TimeSeriesRecorder(MetricsAggregator aggregator, ActiveVirtualUserGauge activeVirtualUsers, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(activeVirtualUsers);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        _aggregator = aggregator;
        _activeVirtualUsers = activeVirtualUsers;
        _interval = interval;
    }

    /// <summary>Points relevés jusqu'ici, dans l'ordre chronologique.</summary>
    public IReadOnlyList<TimeSeriesSample> Samples => _samples;

    /// <summary>
    /// Releve un point toutes les <c>interval</c>, jusqu'a annulation. Prend toujours un dernier
    /// point juste avant de rendre la main, meme si l'annulation intervient avant le premier
    /// intervalle — un tir plus court que l'intervalle de releve garde ainsi au moins un point,
    /// plutot qu'une serie vide.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _startTicks = SiroccoClock.Now;

        try
        {
            while (true)
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
                Record();
            }
        }
        catch (OperationCanceledException)
        {
            // Fin normale ou anticipee du tir : le dernier point est quand meme pris ci-dessous.
        }

        Record();
    }

    private void Record()
    {
        long now = SiroccoClock.Now;
        LoadTestReport snapshot = _aggregator.Snapshot(StatisticsScope.Sliding, now);
        StepStatistics iteration = snapshot.Iteration;

        _samples.Add(new TimeSeriesSample
        {
            ElapsedSeconds = SiroccoClock.ToSeconds(now - _startTicks),
            IterationsPerSecond = snapshot.IterationsPerSecond,
            ActiveVirtualUsers = _activeVirtualUsers.Value,
            ErrorRate = iteration.ErrorRate,
            ResponseP50Milliseconds = iteration.Response.P50Milliseconds,
            ResponseP95Milliseconds = iteration.Response.P95Milliseconds,
            ResponseP99Milliseconds = iteration.Response.P99Milliseconds,
            MaxSchedulingDelayMilliseconds = iteration.MaxSchedulingDelayMilliseconds,
        });
    }
}