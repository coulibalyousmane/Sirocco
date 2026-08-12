using Tempest.Domain.Metrics;

namespace Tempest.Host.Configuration;

/// <summary>
/// Un scenario au sein d'un tir a <b>scenarios concurrents</b> (<see cref="TempestHostOptions.Scenarios"/>) :
/// meme vocabulaire de modele de charge que <see cref="TempestHostOptions"/> lui-meme (profil,
/// modele ferme sous ses quatre formes), mais son propre nom, ses propres etiquettes (portees par
/// le workflow qu'il charge) et ses propres seuils — jamais partages avec un autre scenario du
/// meme tir.
/// <para>
/// Reste volontairement un sous-ensemble : le role, le secret de cluster, les chemins de rapport
/// et l'arret en fin de tir restent l'affaire du tir entier, pas d'un scenario individuel.
/// </para>
/// </summary>
public sealed class ScenarioOptions
{
    /// <summary>
    /// Nom du scenario : identifie ses statistiques dans le rapport combine. Doit etre unique au
    /// sein d'un meme tir.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Adresse de base de la cible pour ce scenario. <see langword="null"/> par defaut : le
    /// scenario utilise alors <see cref="TempestHostOptions.TargetBaseUrl"/>, ce qui couvre le cas
    /// courant ou tous les scenarios visent la meme cible.
    /// </summary>
    public string? TargetBaseUrl { get; init; }

    /// <summary>Meme role que <see cref="TempestHostOptions.MaxVirtualUsers"/>, propre a ce scenario.</summary>
    public int MaxVirtualUsers { get; init; } = TempestHostOptions.DEFAULT_MAX_VIRTUAL_USERS;

    /// <summary>Meme role que <see cref="TempestHostOptions.Profile"/>, propre a ce scenario.</summary>
    public IReadOnlyList<LoadStageOptions> Profile { get; init; } = [];

    /// <summary>Meme role que <see cref="TempestHostOptions.ClosedModelDuration"/>, propre a ce scenario.</summary>
    public TimeSpan? ClosedModelDuration { get; init; }

    /// <summary>Meme role que <see cref="TempestHostOptions.RampVus"/>, propre a ce scenario.</summary>
    public IReadOnlyList<VirtualUserStageOptions> RampVus { get; init; } = [];

    /// <summary>Vrai si ce scenario utilise une montee d'utilisateurs plutot qu'un effectif fixe.</summary>
    public bool IsRampingVus => RampVus.Count > 0;

    /// <summary>Meme role que <see cref="TempestHostOptions.SharedIterations"/>, propre a ce scenario.</summary>
    public long? SharedIterations { get; init; }

    /// <summary>Meme role que <see cref="TempestHostOptions.IterationsPerVirtualUser"/>, propre a ce scenario.</summary>
    public long? IterationsPerVirtualUser { get; init; }

    /// <summary>
    /// Meme role que <see cref="TempestHostOptions.MaxRequestsPerSecond"/>, propre a ce scenario.
    /// <see langword="null"/> par defaut : le scenario retombe alors sur le plafond du tir entier,
    /// s'il en a un.
    /// </summary>
    public double? MaxRequestsPerSecond { get; init; }

    /// <summary>Vrai si ce scenario utilise un modele sans echeancier theorique — voir <see cref="TempestHostOptions.IsClosedModel"/>.</summary>
    public bool IsClosedModel =>
        ClosedModelDuration is not null || IsRampingVus || SharedIterations is not null || IterationsPerVirtualUser is not null;

    /// <summary>Meme role que <see cref="TempestHostOptions.ScenarioFile"/>, propre a ce scenario.</summary>
    public string? ScenarioFile { get; init; }

    /// <summary>Meme role que <see cref="TempestHostOptions.Workflow"/>, propre a ce scenario.</summary>
    public string Workflow { get; init; } = TempestHostOptions.DYNAMIC_CHECKOUT_WORKFLOW;

    /// <summary>Seuils evalues pour ce scenario uniquement. Vide par defaut.</summary>
    public IReadOnlyList<ThresholdRule> Thresholds { get; init; } = [];
}