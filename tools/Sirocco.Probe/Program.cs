using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Sirocco.Application.Execution;
using Sirocco.Application.Metrics;
using Sirocco.Domain.Execution;
using Sirocco.Domain.Load;
using Sirocco.Domain.Metrics;
using Sirocco.Infrastructure.Metrics;

// Etalonnage de l'injecteur : scenario a vide, aucun reseau. On mesure le cout propre de
// Sirocco, c'est-a-dire le plafond au-dela duquel il mesurerait sa propre lenteur.
//
// Deux passes, pour que le prix de l'observabilite soit un chiffre et non une intuition :
//   1. puits qui se contente de compter — le moteur seul ;
//   2. chaine complete — canal, consommateur, histogrammes, fenetre glissante.

const int VIRTUAL_USERS = 256;
const int STAGE_SECONDS = 3;

double[] targets = args.Length > 0
    ? Array.ConvertAll(args, a => double.Parse(a, CultureInfo.InvariantCulture))
    : [10_000d, 50_000d, 100_000d];

TimeSpan duration = TimeSpan.FromSeconds(STAGE_SECONDS);

Console.WriteLine(
    $"ServerGC={GCSettings.IsServerGC}  Coeurs={Environment.ProcessorCount}  " +
    $"Frequence={Stopwatch.Frequency:N0} Hz  Duree/palier={duration.TotalSeconds:F0}s");

using HttpClient httpClient = new();

Console.WriteLine();
Console.WriteLine("-- moteur seul (NullMetricSink) --");
foreach (double targetRps in targets)
{
    RunResult result = await MeasureAsync(targetRps, withMetrics: false);
    Console.WriteLine(result.Line);
}

Console.WriteLine();
Console.WriteLine("-- chaine complete (agregation + fenetre glissante) --");
LoadTestReport? lastReport = null;
foreach (double targetRps in targets)
{
    RunResult result = await MeasureAsync(targetRps, withMetrics: true);
    Console.WriteLine(result.Line);
    lastReport = result.Report;
}

if (lastReport is not null)
{
    Console.WriteLine();
    Console.Write(lastReport.ToTable());
}

async Task<RunResult> MeasureAsync(double targetRps, bool withMetrics)
{
    StepRegistry steps = new();
    ChannelMetricSink? sink = withMetrics ? new ChannelMetricSink() : null;

    TargetRpsLoadEngine engine = new(
        new CoordinatedRateLimiter(LoadProfile.Constant(targetRps, duration)),
        new NoOpWorkflow(),
        httpClient,
        (IMetricSink?)sink ?? new NullMetricSink(),
        new LoadTestOptions { MaxVirtualUsers = VIRTUAL_USERS },
        steps);

    // L'agregateur se dimensionne sur le registre scelle : il ne peut donc etre construit
    // qu'une fois les etapes declarees, ce que fait ici une preparation a blanc.
    engine.Steps.Register(WellKnownSteps.ITERATION);
    new NoOpWorkflow().RegisterSteps(engine.Steps);

    MetricsProcessor? processor = null;
    if (sink is not null)
    {
        processor = new MetricsProcessor(sink, new MetricsAggregator(engine.Steps));
        processor.Start();
    }

    int gen0Before = GC.CollectionCount(0);
    long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

    LoadTestSummary summary = await engine.RunAsync();

    LoadTestReport? report = null;
    if (processor is not null)
    {
        await processor.StopAsync();
        report = processor.Aggregator.Snapshot(StatisticsScope.Cumulative);
        await processor.DisposeAsync();
    }

    long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    int gen0 = GC.CollectionCount(0) - gen0Before;
    double perIteration = allocated / (double)Math.Max(summary.IterationsStarted, 1);

    string line =
        $"cible {targetRps,8:N0} RPS -> reel {summary.EffectiveRps,8:N0} RPS | " +
        $"iterations {summary.IterationsStarted,8:N0}/{summary.TokensPlanned,-8:N0} | " +
        $"dette max {summary.MaxSchedulingDelayMilliseconds,7:F2} ms | " +
        $"{perIteration,5:F0} o/iteration | gen0 {gen0,3}";

    return new RunResult(line, report);
}

internal readonly record struct RunResult(string Line, LoadTestReport? Report);

/// <summary>
/// Scenario degenere : il ouvre et ferme une etape, sans aucune I/O.
/// Tout ce qui est mesure ici est imputable a l'injecteur.
/// </summary>
internal sealed class NoOpWorkflow : IWorkflow
{
    private const string STEP_NAME = "noop";

    private StepId _step;

    public string Name => STEP_NAME;

    public void RegisterSteps(StepRegistry registry) => _step = registry.Register(STEP_NAME);

    public ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        context.BeginStep(_step).Success();
        return ValueTask.CompletedTask;
    }
}