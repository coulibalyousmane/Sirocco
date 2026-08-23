using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;

namespace Sirocco.UnitTests.TestDoubles;

/// <summary>
/// Fabrique de mesures dont on maitrise entierement la chronologie : les tests d'agregation
/// doivent pouvoir decrire une latence en millisecondes sans manipuler de ticks a la main.
/// </summary>
internal static class MetricFactory
{
    private const double MILLISECONDS_PER_SECOND = 1_000d;
    private const int DEFAULT_STATUS_CODE = 200;

    public static MetricResult Create(
        StepId step,
        long completedAtTicks,
        double responseMilliseconds,
        double serviceMilliseconds,
        RequestOutcome outcome = RequestOutcome.Success,
        long bytesReceived = 0L,
        int virtualUserId = 0)
    {
        long responseTicks = SiroccoClock.FromSeconds(responseMilliseconds / MILLISECONDS_PER_SECOND);
        long serviceTicks = SiroccoClock.FromSeconds(serviceMilliseconds / MILLISECONDS_PER_SECOND);

        return new MetricResult(
            step,
            virtualUserId,
            ScheduledTicks: completedAtTicks - responseTicks,
            StartedTicks: completedAtTicks - serviceTicks,
            CompletedTicks: completedAtTicks,
            StatusCode: DEFAULT_STATUS_CODE,
            Outcome: outcome,
            BytesReceived: bytesReceived);
    }
}