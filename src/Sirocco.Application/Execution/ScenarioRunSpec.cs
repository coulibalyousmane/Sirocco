using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;

namespace Sirocco.Application.Execution;

/// <summary>
/// Recette d'execution d'un scenario au sein d'un tir a scenarios concurrents : tout ce dont
/// <c>MultiScenarioRunner</c> (<c>Sirocco.Host</c>) a besoin pour le faire tourner de facon isolee
/// des autres scenarios du meme tir — son propre <see cref="IWorkflow"/>, son propre
/// <see cref="ILoadScheduler"/>, son propre <see cref="HttpClient"/>.
/// <para>
/// Reste un aplat de valeurs deja resolues, comme <see cref="LoadTestOptions"/> : c'est l'appelant
/// (l'hote) qui decide comment les construire a partir de sa propre configuration, pas cette
/// classe.
/// </para>
/// </summary>
public sealed class ScenarioRunSpec
{
    /// <summary>Nom du scenario, reporte tel quel dans <see cref="Metrics.ScenarioReport.Name"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Scenario a jouer.</summary>
    public required IWorkflow Workflow { get; init; }

    /// <summary>Ordonnanceur pilotant ce scenario, independant de celui des autres scenarios du tir.</summary>
    public required ILoadScheduler Scheduler { get; init; }

    /// <summary>Client HTTP dedie a ce scenario — jamais partage avec un autre scenario du meme tir.</summary>
    public required HttpClient HttpClient { get; init; }

    /// <summary>Reglages de l'injecteur pour ce scenario. Valeurs par defaut si omis.</summary>
    public LoadTestOptions Options { get; init; } = new();

    /// <summary>Seuils propres a ce scenario. Vide par defaut.</summary>
    public IReadOnlyList<ThresholdRule> Thresholds { get; init; } = [];

    /// <summary>
    /// Vrai si ce scenario utilise un modele sans echeancier theorique — reporte tel quel dans
    /// <see cref="Metrics.LoadTestReport.ClosedModel"/> pour ce scenario, independamment des
    /// autres scenarios du tir qui peuvent choisir un modele different.
    /// </summary>
    public bool IsClosedModel { get; init; }
}