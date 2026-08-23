using Sirocco.Domain.Metrics;

namespace Sirocco.Host.Execution;

/// <summary>
/// Case memoire du dernier rapport d'un tir a scenarios concurrents, remplie une seule fois par
/// <see cref="MultiScenarioLoadTestHostedService"/> a la fin du tir.
/// <para>
/// Contrairement au tir simple, ou <c>MetricsAggregator.Snapshot</c> permet de photographier
/// l'etat courant a tout instant (donc <c>/report/live</c>), le tir a scenarios concurrents
/// n'expose que le rapport final : voir la remarque de classe de <see cref="MultiScenarioHost"/>.
/// </para>
/// </summary>
internal sealed class MultiScenarioReportHolder
{
    /// <summary><see langword="null"/> jusqu'a la fin du tir.</summary>
    public MultiScenarioReport? Report { get; set; }
}