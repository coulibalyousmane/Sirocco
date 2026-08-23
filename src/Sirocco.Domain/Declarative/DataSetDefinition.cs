using Sirocco.Domain.Data;

namespace Sirocco.Domain.Declarative;

/// <summary>
/// Reference declarative vers un jeu de donnees (CSV ou JSON) que
/// <c>Sirocco.Scenarios.DeclarativeWorkflow</c> charge au demarrage du tir et rend accessible a
/// chaque etape via <c>{{nom.colonne}}</c>, aux cotes des variables extraites.
/// </summary>
public sealed record DataSetDefinition
{
    /// <summary>Nom du jeu de donnees, prefixe des placeholders qui le referencent (<c>{{nom.colonne}}</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Chemin du fichier CSV ou JSON, resolu depuis le repertoire courant du processus.</summary>
    public required string Path { get; init; }

    /// <summary>Strategie de choix d'une ligne a chaque iteration. Circulaire par defaut.</summary>
    public DataSetIterationStrategy Strategy { get; init; } = DataSetIterationStrategy.Circular;

    /// <summary>Valide la coherence de la reference.</summary>
    /// <exception cref="ArgumentException">La reference est incoherente.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Le nom d'un jeu de donnees ne peut pas etre vide.", nameof(Name));
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new ArgumentException($"Le chemin du jeu de donnees '{Name}' ne peut pas etre vide.", nameof(Path));
        }
    }
}