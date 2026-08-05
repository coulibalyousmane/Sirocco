using Tempest.Domain.Metrics;

namespace Tempest.Domain.Execution;

/// <summary>
/// Scenario utilisateur execute en boucle par les utilisateurs virtuels.
/// <para>
/// Le cycle de vie est en trois temps : <see cref="RegisterSteps"/> (une fois, a froid),
/// <see cref="SetUpAsync"/> (une fois, pre-generation des jeux de donnees), puis
/// <see cref="ExecuteAsync"/> (a chaque iteration, sur le chemin critique).
/// </para>
/// </summary>
public interface IWorkflow
{
    /// <summary>Nom lisible du scenario, utilise dans les rapports et les metriques.</summary>
    string Name { get; }

    /// <summary>
    /// Declare toutes les etapes du scenario. Appele une seule fois avant le demarrage :
    /// c'est le seul moment ou un <see cref="StepId"/> peut etre obtenu.
    /// </summary>
    void RegisterSteps(StepRegistry registry);

    /// <summary>
    /// Prepare le scenario avant le premier tir : pre-generation des pools de donnees,
    /// prechauffage des connexions, authentification technique.
    /// Le temps passe ici n'est pas comptabilise dans les metriques.
    /// </summary>
    ValueTask SetUpAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Execute une iteration complete du parcours utilisateur.
    /// <para>
    /// Implementation sur le chemin critique : eviter LINQ, les fermetures capturantes,
    /// les concatenations de chaines et toute allocation evitable.
    /// </para>
    /// </summary>
    ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken);

    /// <summary>Libere les ressources du scenario apres le test.</summary>
    ValueTask TearDownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}