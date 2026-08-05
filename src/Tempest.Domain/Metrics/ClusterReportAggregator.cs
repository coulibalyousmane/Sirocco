namespace Tempest.Domain.Metrics;

/// <summary>
/// Fusionne les rapports de plusieurs workers en un seul <see cref="LoadTestReport"/> (mode
/// distribue Master/Workers).
/// <para>
/// Fusionne au niveau des histogrammes <b>bruts</b> (<see cref="LatencyHistogram.Add(HistogramSnapshot)"/>)
/// puis ne calcule les centiles qu'une seule fois, sur le resultat fusionne
/// (<see cref="LatencyHistogram.Snapshot"/>). Combiner directement des <see cref="LatencySnapshot"/>
/// deja calcules — moyenner ou maximiser des p99 individuels — serait le piege classique du
/// "centile de centiles" : statistiquement faux, quelle que soit la formule choisie.
/// </para>
/// </summary>
public static class ClusterReportAggregator
{
    private static readonly int _outcomeCount = Enum.GetValues<RequestOutcome>().Length;

    /// <summary>Fusionne les rapports bruts d'au moins un worker.</summary>
    /// <param name="workers">Rapports pousses par chaque worker a la fin de son tir local.</param>
    /// <param name="duration">
    /// Duree du tir mesuree cote maitre (mur), depuis l'ordre de depart jusqu'au dernier
    /// rapport recu : chaque worker mesure son propre temps sur une horloge monotone locale,
    /// qui ne se compare pas d'un process a l'autre.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="workers"/> est vide.</exception>
    public static LoadTestReport Merge(IReadOnlyList<WorkerReport> workers, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(workers);

        if (workers.Count == 0)
        {
            throw new ArgumentException("Au moins un rapport de worker est requis pour fusionner.", nameof(workers));
        }

        Dictionary<string, Accumulator> byName = new(StringComparer.Ordinal);
        long metricsDropped = 0L;

        foreach (WorkerReport worker in workers)
        {
            metricsDropped += worker.MetricsDropped;

            foreach (WorkerStepReport step in worker.Steps)
            {
                if (!byName.TryGetValue(step.Name, out Accumulator? accumulator))
                {
                    accumulator = new Accumulator();
                    byName[step.Name] = accumulator;
                }

                accumulator.Add(step);
            }
        }

        StepStatistics[] steps = new StepStatistics[byName.Count];
        StepStatistics iteration = StepStatistics.Empty(StepId.None, WellKnownSteps.ITERATION);

        int index = 0;
        foreach ((string name, Accumulator accumulator) in byName)
        {
            StepStatistics statistics = accumulator.Build(name);
            steps[index++] = statistics;

            if (name == WellKnownSteps.ITERATION)
            {
                iteration = statistics;
            }
        }

        return new LoadTestReport
        {
            Scope = StatisticsScope.Cumulative,
            Duration = duration,
            Steps = steps,
            Iteration = iteration,
            MetricsDropped = metricsDropped,
        };
    }

    /// <summary>Etat intermediaire d'une etape pendant la fusion, avant le calcul final des centiles.</summary>
    private sealed class Accumulator
    {
        private readonly LatencyHistogram _response = new();
        private readonly LatencyHistogram _service = new();
        private readonly long[] _countByOutcome = new long[_outcomeCount];

        private long _bytesReceived;
        private long _maxSchedulingDelayMicroseconds;

        public void Add(WorkerStepReport step)
        {
            _response.Add(step.Response);
            _service.Add(step.Service);
            _bytesReceived += step.BytesReceived;
            _maxSchedulingDelayMicroseconds = Math.Max(_maxSchedulingDelayMicroseconds, step.MaxSchedulingDelayMicroseconds);

            for (int i = 0; i < step.CountByOutcome.Length; i++)
            {
                _countByOutcome[i] += step.CountByOutcome[i];
            }
        }

        public StepStatistics Build(string name)
        {
            long total = 0L;
            foreach (long count in _countByOutcome)
            {
                total += count;
            }

            return new StepStatistics
            {
                Name = name,
                Step = StepId.None,
                Count = total,
                SuccessCount = _countByOutcome[(int)RequestOutcome.Success],
                DroppedCount = _countByOutcome[(int)RequestOutcome.Dropped],
                CountByOutcome = _countByOutcome,
                BytesReceived = _bytesReceived,
                MaxSchedulingDelayMicroseconds = _maxSchedulingDelayMicroseconds,
                Response = _response.Snapshot(),
                Service = _service.Snapshot(),
            };
        }
    }
}