using Tempest.Domain.Metrics;
using Tempest.Domain.Timing;

namespace Tempest.Application.Metrics;

/// <summary>
/// Etat agrege d'une seule etape : un histogramme cumule et un anneau de paniers temporels
/// formant la fenetre glissante.
/// <para>
/// <b>Pourquoi enregistrer dans les deux a la fois.</b> On pourrait n'alimenter que le panier
/// courant et deverser dans le cumul a chaque rotation. Deux increments de tableau coutent
/// moins cher qu'une fusion periodique de 3 072 cellules, et surtout la version naive est
/// evidemment correcte : pas de fenetre a recycler au bon moment, pas de mesure perdue au
/// changement de panier.
/// </para>
/// </summary>
internal sealed class StepAccumulator
{
    private static readonly int _outcomeCount = Enum.GetValues<RequestOutcome>().Length;

    private readonly Lock _gate = new();

    private readonly LatencyHistogram _cumulativeResponse = new();
    private readonly LatencyHistogram _cumulativeService = new();
    private readonly long[] _cumulativeByOutcome = new long[_outcomeCount];

    private readonly LatencyHistogram[] _windowResponse;
    private readonly LatencyHistogram[] _windowService;
    private readonly long[][] _windowByOutcome;
    private readonly long[] _windowBytes;

    // Tampons reutilises a chaque interrogation de la fenetre : eviter d'allouer 24 Ko
    // d'histogramme a chaque collecte Prometheus.
    private readonly LatencyHistogram _scratchResponse = new();
    private readonly LatencyHistogram _scratchService = new();
    private readonly long[] _scratchByOutcome;

    private readonly long _bucketTicks;

    private long _cumulativeBytes;
    private long _maxSchedulingDelayTicks;
    private long _currentSlot = -1L;

    public StepAccumulator(StepId step, string name, int windowBucketCount, long bucketTicks)
    {
        Step = step;
        Name = name;
        _bucketTicks = bucketTicks;

        _windowResponse = CreateHistograms(windowBucketCount);
        _windowService = CreateHistograms(windowBucketCount);
        _windowBytes = new long[windowBucketCount];
        _windowByOutcome = new long[windowBucketCount][];
        for (int i = 0; i < windowBucketCount; i++)
        {
            _windowByOutcome[i] = new long[_outcomeCount];
        }

        _scratchByOutcome = new long[_outcomeCount];
    }

    public StepId Step { get; }

    public string Name { get; }

    /// <summary>Agrege une mesure. Appele une fois par metrique, sur le thread de l'agregateur.</summary>
    public void Record(in MetricResult result)
    {
        long responseMicroseconds = ToMicroseconds(result.ResponseTicks);
        long serviceMicroseconds = ToMicroseconds(result.ServiceTicks);
        int outcome = (int)result.Outcome;

        lock (_gate)
        {
            // L'horloge de la fenetre est celle des donnees, pas celle du mur : le decoupage
            // est ainsi reproductible a l'identique dans un test.
            int slot = Roll(result.CompletedTicks);

            _cumulativeResponse.Record(responseMicroseconds);
            _cumulativeService.Record(serviceMicroseconds);
            _cumulativeByOutcome[outcome]++;
            _cumulativeBytes += result.BytesReceived;

            _windowResponse[slot].Record(responseMicroseconds);
            _windowService[slot].Record(serviceMicroseconds);
            _windowByOutcome[slot][outcome]++;
            _windowBytes[slot] += result.BytesReceived;

            if (result.SchedulingDelayTicks > _maxSchedulingDelayTicks)
            {
                _maxSchedulingDelayTicks = result.SchedulingDelayTicks;
            }
        }
    }

    /// <summary>
    /// Exporte l'etat brut cumule de l'etape, pour fusion inter-process (mode distribue
    /// Master/Workers) — jamais les centiles deja calcules, qui ne se fusionnent pas
    /// correctement (voir <see cref="ClusterReportAggregator"/>).
    /// </summary>
    public WorkerStepReport ExportRaw()
    {
        lock (_gate)
        {
            return new WorkerStepReport(
                Name,
                (long[])_cumulativeByOutcome.Clone(),
                _cumulativeBytes,
                ToMicroseconds(_maxSchedulingDelayTicks),
                _cumulativeResponse.Export(),
                _cumulativeService.Export());
        }
    }

    /// <summary>Photographie les statistiques de l'etape sur le perimetre demande.</summary>
    public StepStatistics Snapshot(StatisticsScope scope, long nowTicks)
    {
        lock (_gate)
        {
            return scope == StatisticsScope.Cumulative
                ? Build(_cumulativeResponse, _cumulativeService, _cumulativeByOutcome, _cumulativeBytes)
                : SnapshotWindow(nowTicks);
        }
    }

    private StepStatistics SnapshotWindow(long nowTicks)
    {
        // Faire avancer la fenetre avant de lire : sans cela, un tir termine continuerait
        // d'afficher indefiniment les derniers centiles observes.
        Roll(nowTicks);

        _scratchResponse.Reset();
        _scratchService.Reset();
        Array.Clear(_scratchByOutcome);
        long bytes = 0L;

        for (int i = 0; i < _windowResponse.Length; i++)
        {
            _scratchResponse.Add(_windowResponse[i]);
            _scratchService.Add(_windowService[i]);
            bytes += _windowBytes[i];

            long[] bucketOutcomes = _windowByOutcome[i];
            for (int outcome = 0; outcome < _scratchByOutcome.Length; outcome++)
            {
                _scratchByOutcome[outcome] += bucketOutcomes[outcome];
            }
        }

        return Build(_scratchResponse, _scratchService, _scratchByOutcome, bytes);
    }

    private StepStatistics Build(
        LatencyHistogram response,
        LatencyHistogram service,
        long[] byOutcome,
        long bytesReceived)
    {
        long total = 0L;
        long[] outcomes = new long[byOutcome.Length];
        for (int i = 0; i < byOutcome.Length; i++)
        {
            outcomes[i] = byOutcome[i];
            total += byOutcome[i];
        }

        return new StepStatistics
        {
            Name = Name,
            Step = Step,
            Count = total,
            SuccessCount = outcomes[(int)RequestOutcome.Success],
            DroppedCount = outcomes[(int)RequestOutcome.Dropped],
            CountByOutcome = outcomes,
            BytesReceived = bytesReceived,
            MaxSchedulingDelayMicroseconds = ToMicroseconds(_maxSchedulingDelayTicks),
            Response = response.Snapshot(),
            Service = service.Snapshot(),
        };
    }

    /// <summary>
    /// Fait avancer l'anneau jusqu'au panier correspondant a <paramref name="nowTicks"/>,
    /// en vidant au passage ceux que la fenetre vient de laisser derriere elle.
    /// </summary>
    /// <returns>Index du panier courant.</returns>
    private int Roll(long nowTicks)
    {
        long slot = nowTicks / _bucketTicks;

        if (slot == _currentSlot)
        {
            return SlotIndex(slot);
        }

        if (_currentSlot < 0L || (slot - _currentSlot) >= _windowResponse.Length)
        {
            // Premier enregistrement, ou silence assez long pour que toute la fenetre soit
            // perimee : plus rien a conserver.
            ResetWindow();
        }
        else
        {
            for (long expired = _currentSlot + 1L; expired <= slot; expired++)
            {
                ResetBucket(SlotIndex(expired));
            }
        }

        _currentSlot = slot;
        return SlotIndex(slot);
    }

    private int SlotIndex(long slot) => (int)(slot % _windowResponse.Length);

    private void ResetWindow()
    {
        for (int i = 0; i < _windowResponse.Length; i++)
        {
            ResetBucket(i);
        }
    }

    private void ResetBucket(int index)
    {
        _windowResponse[index].Reset();
        _windowService[index].Reset();
        _windowBytes[index] = 0L;
        Array.Clear(_windowByOutcome[index]);
    }

    private static LatencyHistogram[] CreateHistograms(int count)
    {
        LatencyHistogram[] histograms = new LatencyHistogram[count];
        for (int i = 0; i < count; i++)
        {
            histograms[i] = new LatencyHistogram();
        }

        return histograms;
    }

    private static long ToMicroseconds(long ticks) => (long)TempestClock.ToMicroseconds(ticks);
}