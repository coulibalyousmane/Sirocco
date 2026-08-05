namespace Tempest.Domain.Declarative;

/// <summary>
/// Description declarative complete d'un scenario : un nom, une sequence d'etapes HTTP.
/// <para>
/// C'est la structure que <c>Tempest.Scenarios.Declarative.ScenarioDefinitionLoader</c>
/// produit a partir d'un fichier YAML ou JSON, et que
/// <c>Tempest.Scenarios.DeclarativeWorkflow</c> interprete a chaque iteration. Aucune des deux
/// classes n'est referencee ici : le Domain ne connait que la forme des donnees, jamais la
/// facon dont elles sont lues ni executees.
/// </para>
/// </summary>
public sealed record ScenarioDefinition
{
    /// <summary>Nom du scenario, utilise dans les rapports.</summary>
    public required string Name { get; init; }

    /// <summary>Etapes du scenario, executees dans l'ordre a chaque iteration.</summary>
    public required IReadOnlyList<HttpStepDefinition> Steps { get; init; }

    /// <summary>Valide la coherence du scenario et de chacune de ses etapes.</summary>
    /// <exception cref="ArgumentException">Le scenario est incoherent.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Le nom du scenario ne peut pas etre vide.", nameof(Name));
        }

        if (Steps.Count == 0)
        {
            throw new ArgumentException("Un scenario declaratif doit contenir au moins une etape.", nameof(Steps));
        }

        HashSet<string> seenNames = new(StringComparer.Ordinal);
        foreach (HttpStepDefinition step in Steps)
        {
            step.Validate();

            if (!seenNames.Add(step.Name))
            {
                throw new ArgumentException(
                    $"Le nom d'etape '{step.Name}' apparait plusieurs fois : les noms doivent etre uniques.",
                    nameof(Steps));
            }
        }
    }
}