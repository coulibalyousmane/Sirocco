using Sirocco.Domain.Timing;

namespace Sirocco.Application.Execution;

/// <summary>
/// Bilan d'execution d'un tir. Ne contient <b>que</b> ce que le moteur sait de lui-meme :
/// les percentiles de latence relevent de l'agregateur de metriques.
/// </summary>
public sealed record LoadTestSummary
{
    /// <summary>Duree reelle du tir.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Nombre de jetons emis par le regulateur de debit.</summary>
    public required long TokensIssued { get; init; }

    /// <summary>Nombre de jetons que le profil prevoyait.</summary>
    public required long TokensPlanned { get; init; }

    /// <summary>Nombre d'iterations demarrees.</summary>
    public required long IterationsStarted { get; init; }

    /// <summary>Nombre d'iterations terminees sans exception.</summary>
    public required long IterationsCompleted { get; init; }

    /// <summary>Nombre d'iterations interrompues par une exception du scenario.</summary>
    public required long IterationsFailed { get; init; }

    /// <summary>Nombre de jetons abandonnes pour cause de retard excessif.</summary>
    public required long IterationsDropped { get; init; }

    /// <summary>Nombre de mesures emises par les scenarios et le moteur.</summary>
    public required long MetricsEmitted { get; init; }

    /// <summary>Nombre de mesures perdues faute de place dans le canal.</summary>
    public required long MetricsDropped { get; init; }

    /// <summary>Dette d'ordonnancement maximale observee, en ticks.</summary>
    public required long MaxSchedulingDelayTicks { get; init; }

    /// <summary>
    /// Premiere exception non geree remontee par le scenario, s'il y en a eu une.
    /// Un tir avec <see cref="IterationsFailed"/> eleve sans cette trace serait indebogable.
    /// </summary>
    public Exception? FirstScenarioError { get; init; }

    /// <summary>Debit reellement soutenu, iterations demarrees par seconde.</summary>
    public double EffectiveRps => Duration > TimeSpan.Zero ? IterationsStarted / Duration.TotalSeconds : 0d;

    /// <summary>Dette d'ordonnancement maximale, en millisecondes.</summary>
    public double MaxSchedulingDelayMilliseconds => SiroccoClock.ToMilliseconds(MaxSchedulingDelayTicks);

    /// <summary>
    /// L'injecteur n'a pas tenu la cadence : des jetons ont ete planifies mais jamais emis.
    /// Le tir sous-estime la charge demandee, les resultats ne sont pas exploitables tels quels.
    /// </summary>
    public bool InjectorFellBehind => TokensIssued < TokensPlanned;

    /// <summary>
    /// Une partie des mesures a ete perdue : les percentiles publies portent sur un
    /// echantillon incomplet et biaise.
    /// </summary>
    public bool MetricsAreIncomplete => MetricsDropped > 0;

    /// <inheritdoc />
    public override string ToString() =>
        $"{IterationsStarted} iterations en {Duration.TotalSeconds:F2}s ({EffectiveRps:F0} RPS) | " +
        $"terminees {IterationsCompleted}, echecs {IterationsFailed}, abandons {IterationsDropped} | " +
        $"dette max {MaxSchedulingDelayMilliseconds:F1} ms | " +
        $"metriques {MetricsEmitted} (perdues {MetricsDropped})";
}