namespace Sirocco.Domain.Declarative;

/// <summary>
/// Assertion logique sur le corps d'une reponse, rapportee comme sa <b>propre</b> etape dans
/// les rapports — distincte de la classification de la requete HTTP dont elle derive (voir
/// <see cref="HttpStepDefinition.ExpectedStatusCodes"/>, qui reste inchangee par un check qui
/// echoue).
/// <para>
/// Reutilise exactement le meme vocabulaire d'expression que <see cref="ExtractionRule"/>
/// (Regex/XPath/JsonPath, une seule des trois) plutot que d'inventer un second langage
/// d'assertion : un check est une extraction dont on ne garde que « a-t-elle trouve quelque
/// chose » (<see cref="Expected"/> absent) ou « a-t-elle trouve exactement cette valeur »
/// (<see cref="Expected"/> present).
/// </para>
/// </summary>
public sealed record CheckRule
{
    /// <summary>
    /// Nom du check, tel qu'il apparait dans les rapports — dans le meme espace de noms que les
    /// noms d'etape : un check ne peut pas porter le nom d'une etape existante, ni d'un autre
    /// check, dans tout le scenario.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Motif Regex ; le premier groupe capturant est extrait, ou la correspondance entiere a defaut.</summary>
    public string? Regex { get; init; }

    /// <summary>Expression XPath, evaluee contre le corps interprete comme XML.</summary>
    public string? XPath { get; init; }

    /// <summary>Expression JsonPath, evaluee contre le corps interprete comme JSON.</summary>
    public string? JsonPath { get; init; }

    /// <summary>
    /// Valeur attendue. <see langword="null"/> (par defaut) : le check reussit si l'expression
    /// trouve quoi que ce soit. Fournie : le check ne reussit que si la valeur trouvee lui est
    /// identique (comparaison de texte exacte).
    /// </summary>
    public string? Expected { get; init; }

    /// <summary>Valide la coherence du check et la syntaxe de son expression.</summary>
    /// <exception cref="ArgumentException">Le check est incoherent ou son expression est invalide.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Le nom d'un check ne peut pas etre vide.", nameof(Name));
        }

        try
        {
            ToExtractionRule().Validate();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Check '{Name}' invalide : {ex.Message}", nameof(Name), ex);
        }
    }

    /// <summary>
    /// Evalue le check contre le corps de reponse fourni : trouve (et, si <see cref="Expected"/>
    /// est fourni, identique a cette valeur) vaut succes, tout le reste vaut echec.
    /// </summary>
    public bool Evaluate(string responseBody)
    {
        bool matched = ToExtractionRule().TryExtract(responseBody, out string? actual);
        return Expected is null ? matched : matched && string.Equals(actual, Expected, StringComparison.Ordinal);
    }

    private ExtractionRule ToExtractionRule() => new() { Variable = Name, Regex = Regex, XPath = XPath, JsonPath = JsonPath };
}