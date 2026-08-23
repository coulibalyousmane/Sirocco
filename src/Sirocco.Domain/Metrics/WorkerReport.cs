namespace Sirocco.Domain.Metrics;

/// <summary>
/// Etat brut cumule d'une etape, tel qu'exporte par un worker pour fusion cote maitre.
/// <see cref="Name"/>, pas <see cref="StepId"/> : chaque process scelle son propre
/// <c>StepRegistry</c> independamment, seul le nom reste stable entre processus.
/// </summary>
public sealed record WorkerStepReport(
    string Name,
    long[] CountByOutcome,
    long BytesReceived,
    long MaxSchedulingDelayMicroseconds,
    HistogramSnapshot Response,
    HistogramSnapshot Service);

/// <summary>
/// Rapport pousse par un worker vers le maitre a la fin de son tir local (mode distribue).
/// </summary>
/// <param name="WorkerId">Identifiant du worker (son adresse jointe), pour diagnostic.</param>
/// <param name="MetricsDropped">Mesures perdues sur ce worker, faute de place dans son canal.</param>
/// <param name="Steps">Etat brut cumule de chaque etape declaree par ce worker.</param>
public sealed record WorkerReport(
    string WorkerId,
    long MetricsDropped,
    List<WorkerStepReport> Steps);