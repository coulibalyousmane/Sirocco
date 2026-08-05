namespace Tempest.Domain.Declarative;

/// <summary>
/// Description declarative d'une etape HTTP : une requete unique, sans branchement ni boucle.
/// <para>
/// Peut extraire des valeurs de sa reponse (<see cref="Extract"/>) et reutiliser celles
/// extraites par une etape precedente via <c>{{nom}}</c> dans <see cref="Path"/>,
/// <see cref="Body"/> ou <see cref="Headers"/> — c'est la seule forme de dependance entre
/// etapes que cette version autorise. Un scenario qui a besoin de branchement ou de boucle
/// reste un <see cref="Execution.IWorkflow"/> ecrit a la main.
/// </para>
/// </summary>
public sealed record HttpStepDefinition
{
    /// <summary>Nom de l'etape, tel qu'il apparait dans les rapports.</summary>
    public required string Name { get; init; }

    /// <summary>Methode HTTP (GET, POST, ...).</summary>
    public required string Method { get; init; }

    /// <summary>Chemin relatif, combine a l'adresse de base du client HTTP partage.</summary>
    public required string Path { get; init; }

    /// <summary>Corps de la requete, statique. <see langword="null"/> si l'etape n'en envoie pas.</summary>
    public string? Body { get; init; }

    /// <summary>Type de contenu du corps, si un corps est fourni.</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>En-tetes additionnels, statiques.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Codes de statut attendus. Une liste vide (par defaut) applique l'heuristique usuelle :
    /// tout 2xx est un succes. Une liste non vide impose une correspondance exacte — un statut
    /// 2xx absent de cette liste devient un echec d'assertion, pas un succes.
    /// </summary>
    public IReadOnlyList<int> ExpectedStatusCodes { get; init; } = [];

    /// <summary>
    /// Valeurs a extraire de la reponse de cette etape, pour reutilisation dans les etapes
    /// suivantes. Vide par defaut : aucune extraction n'a lieu.
    /// </summary>
    public IReadOnlyList<ExtractionRule> Extract { get; init; } = [];

    /// <summary>Valide la coherence de l'etape et de chacune de ses regles d'extraction.</summary>
    /// <exception cref="ArgumentException">L'etape est incoherente.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Le nom d'une etape ne peut pas etre vide.", nameof(Name));
        }

        if (string.IsNullOrWhiteSpace(Method))
        {
            throw new ArgumentException($"L'etape '{Name}' n'a pas de methode HTTP.", nameof(Method));
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new ArgumentException($"L'etape '{Name}' n'a pas de chemin.", nameof(Path));
        }

        if (Body is not null && string.IsNullOrWhiteSpace(ContentType))
        {
            throw new ArgumentException($"L'etape '{Name}' a un corps mais pas de type de contenu.", nameof(ContentType));
        }

        foreach (ExtractionRule rule in Extract)
        {
            rule.Validate();
        }
    }
}