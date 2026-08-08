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

    /// <summary>
    /// Jeux de donnees charges au demarrage du tir, accessibles depuis n'importe quelle etape
    /// via <c>{{nom.colonne}}</c>. Vide par defaut : aucun scenario existant n'en depend.
    /// </summary>
    public IReadOnlyList<DataSetDefinition> Datasets { get; init; } = [];

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

        // Meme espace de noms pour les etapes et leurs checks : les deux deviennent chacun leur
        // propre StepId dans le rapport (voir DeclarativeWorkflow), une collision entre les deux
        // fusionnerait silencieusement deux lignes distinctes du rapport en une seule.
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

            foreach (CheckRule check in step.Checks)
            {
                if (!seenNames.Add(check.Name))
                {
                    throw new ArgumentException(
                        $"Le nom '{check.Name}' apparait plusieurs fois (etape ou check) : les noms doivent etre uniques dans tout le scenario.",
                        nameof(Steps));
                }
            }
        }

        HashSet<string> seenDatasetNames = new(StringComparer.Ordinal);
        foreach (DataSetDefinition dataset in Datasets)
        {
            dataset.Validate();

            if (!seenDatasetNames.Add(dataset.Name))
            {
                throw new ArgumentException(
                    $"Le nom de jeu de donnees '{dataset.Name}' apparait plusieurs fois : les noms doivent etre uniques.",
                    nameof(Datasets));
            }
        }
    }
}