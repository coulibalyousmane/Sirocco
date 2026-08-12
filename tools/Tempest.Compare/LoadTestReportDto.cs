using Tempest.Domain.Metrics;

namespace Tempest.Compare;

/// <summary>
/// Forme intermediaire, mutable et a types concrets, utilisee uniquement pour la
/// deserialisation JSON d'un rapport exporte par <c>/report</c>.
/// <para>
/// Meme raison que <c>ScenarioDefinitionDto</c> cote <c>Tempest.Scenarios</c> :
/// <see cref="System.Text.Json.JsonSerializer"/> ne sait pas construire un
/// <c>IReadOnlyList&lt;T&gt;</c> par reflexion, il lui faut un type concret
/// (<see cref="List{T}"/>). Cette classe isole ce compromis a la frontiere, avant de mapper
/// vers le type Domain reel via <see cref="ToDomain"/>.
/// </para>
/// </summary>
internal sealed class LoadTestReportDto
{
    public StatisticsScope Scope { get; set; }

    public TimeSpan Duration { get; set; }

    public List<StepStatisticsDto> Steps { get; set; } = [];

    public StepStatisticsDto? Iteration { get; set; }

    public long MetricsDropped { get; set; }

    public LoadTestReport ToDomain() => new()
    {
        Scope = Scope,
        Duration = Duration,
        Steps = Steps.ConvertAll(static step => step.ToDomain()),
        Iteration = Iteration?.ToDomain() ?? StepStatistics.Empty(StepId.None, WellKnownSteps.ITERATION),
        MetricsDropped = MetricsDropped,
    };
}

/// <inheritdoc cref="LoadTestReportDto" />
internal sealed class StepStatisticsDto
{
    public string Name { get; set; } = string.Empty;

    public long Count { get; set; }

    public long SuccessCount { get; set; }

    public long DroppedCount { get; set; }

    public List<long> CountByOutcome { get; set; } = [];

    public long BytesReceived { get; set; }

    public long MaxSchedulingDelayMicroseconds { get; set; }

    public LatencySnapshot Response { get; set; }

    public LatencySnapshot Service { get; set; }

    // L'identifiant de l'etape n'a de sens qu'au sein du StepRegistry d'un tir en cours :
    // une fois exporte en JSON puis relu ici, seul le nom compte pour apparier deux rapports.
    public StepStatistics ToDomain() => new()
    {
        Name = Name,
        Step = StepId.None,
        Count = Count,
        SuccessCount = SuccessCount,
        DroppedCount = DroppedCount,
        CountByOutcome = CountByOutcome,
        BytesReceived = BytesReceived,
        MaxSchedulingDelayMicroseconds = MaxSchedulingDelayMicroseconds,
        Response = Response,
        Service = Service,
        // Non transporte par ce DTO : Tempest.Compare ne diffe que des centiles deja
        // calcules, jamais les paniers bruts d'un histogramme.
        ResponseHistogram = HistogramSnapshot.Empty,
    };
}