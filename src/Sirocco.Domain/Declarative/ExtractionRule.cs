using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Sirocco.Domain.Declarative;

/// <summary>
/// Regle d'extraction d'une valeur depuis le corps d'une reponse, liee entre deux etapes
/// declaratives par un nom de variable.
/// <para>
/// Exactement une expression par regle : <see cref="Regex"/> (universelle, sur texte brut),
/// <see cref="XPath"/> (pour un corps XML) ou <see cref="JsonPath"/> (pour un corps JSON).
/// <see cref="JsonPath"/> ne couvre volontairement qu'un sous-ensemble pratique — acces par
/// propriete (<c>.nom</c>) et par index (<c>[n]</c>) — sans caracteres generiques, filtres,
/// descente recursive (<c>..</c>) ni tranches : une extraction Regex suffisait jusqu'ici sur un
/// corps JSON, ce sous-ensemble couvre le reste des cas usuels sans reimplementer la
/// specification JSONPath entiere. <c>Sirocco.Domain</c> n'a aucune dependance NuGet externe :
/// cette extraction s'appuie uniquement sur <see cref="System.Text.Json"/> (BCL), pas sur une
/// bibliotheque JSONPath dediee.
/// </para>
/// </summary>
public sealed partial record ExtractionRule
{
    /// <summary>Nom de la variable liee, reutilisable dans les etapes suivantes via <c>{{nom}}</c>.</summary>
    public required string Variable { get; init; }

    /// <summary>Motif Regex ; le premier groupe capturant est extrait, ou la correspondance entiere a defaut.</summary>
    public string? Regex { get; init; }

    /// <summary>Expression XPath, evaluee contre le corps interprete comme XML.</summary>
    public string? XPath { get; init; }

    /// <summary>
    /// Expression JsonPath (ex. <c>$.data.token</c>, <c>$.items[0].id</c>), evaluee contre le
    /// corps interprete comme JSON. Doit commencer par <c>$</c> ; seuls les segments
    /// <c>.nom</c> et <c>[index]</c> sont pris en charge.
    /// </summary>
    public string? JsonPath { get; init; }

    /// <summary>Valide la coherence de la regle et la syntaxe de son expression.</summary>
    /// <exception cref="ArgumentException">La regle est incoherente ou son expression est invalide.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Variable))
        {
            throw new ArgumentException("Le nom de la variable extraite ne peut pas etre vide.", nameof(Variable));
        }

        bool hasRegex = !string.IsNullOrWhiteSpace(Regex);
        bool hasXPath = !string.IsNullOrWhiteSpace(XPath);
        bool hasJsonPath = !string.IsNullOrWhiteSpace(JsonPath);

        if ((hasRegex ? 1 : 0) + (hasXPath ? 1 : 0) + (hasJsonPath ? 1 : 0) != 1)
        {
            throw new ArgumentException(
                $"L'extraction de '{Variable}' doit fournir exactement une expression : Regex, XPath ou JsonPath, pas plusieurs ni aucune.",
                nameof(Regex));
        }

        if (hasRegex)
        {
            ValidateRegexSyntax();
        }
        else if (hasXPath)
        {
            ValidateXPathSyntax();
        }
        else
        {
            ValidateJsonPathSyntax();
        }
    }

    /// <summary>Tente d'extraire la valeur depuis le corps de reponse fourni.</summary>
    public bool TryExtract(string responseBody, out string? value)
    {
        value = Regex is not null
            ? ExtractWithRegex(responseBody)
            : XPath is not null
                ? ExtractWithXPath(responseBody)
                : ExtractWithJsonPath(responseBody);
        return value is not null;
    }

    private void ValidateRegexSyntax()
    {
        try
        {
            _ = System.Text.RegularExpressions.Regex.IsMatch(string.Empty, Regex!);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Le motif Regex de '{Variable}' est invalide : {ex.Message}", nameof(Regex), ex);
        }
    }

    private void ValidateXPathSyntax()
    {
        try
        {
            _ = new XDocument(new XElement("racine")).XPathEvaluate(XPath!);
        }
        catch (XPathException ex)
        {
            throw new ArgumentException($"L'expression XPath de '{Variable}' est invalide : {ex.Message}", nameof(XPath), ex);
        }
    }

    private void ValidateJsonPathSyntax()
    {
        if (!TryParseJsonPathSegments(JsonPath!, out _, out string? error))
        {
            throw new ArgumentException($"L'expression JsonPath de '{Variable}' est invalide : {error}", nameof(JsonPath));
        }
    }

    private string? ExtractWithRegex(string responseBody)
    {
        Match match = System.Text.RegularExpressions.Regex.Match(responseBody, Regex!);
        if (!match.Success)
        {
            return null;
        }

        // Un groupe capturant (index 1) prime sur la correspondance entiere (index 0) : c'est
        // la valeur utile qu'on veut isoler, pas le texte qui l'entoure.
        return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
    }

    private string? ExtractWithXPath(string responseBody)
    {
        object result;
        try
        {
            XDocument document = XDocument.Parse(responseBody);
            result = document.XPathEvaluate(XPath!);
        }
        catch (XmlException)
        {
            return null;
        }

        return result switch
        {
            string text => text,
            System.Collections.IEnumerable nodes => FirstNodeValue(nodes),
            _ => result.ToString(),
        };
    }

    private static string? FirstNodeValue(System.Collections.IEnumerable nodes)
    {
        foreach (object node in nodes)
        {
            return node switch
            {
                XElement element => element.Value,
                XAttribute attribute => attribute.Value,
                _ => node.ToString(),
            };
        }

        return null;
    }

    private string? ExtractWithJsonPath(string responseBody)
    {
        // Validate() a deja garanti la syntaxe en amont du tir ; un appelant qui contournerait
        // Validate() obtient simplement une extraction manquee plutot qu'une exception au milieu
        // d'une iteration en cours.
        if (!TryParseJsonPathSegments(JsonPath!, out List<JsonPathSegment> segments, out _))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(responseBody);
        }
        catch (JsonException)
        {
            return null;
        }

        foreach (JsonPathSegment segment in segments)
        {
            if (node is null)
            {
                return null;
            }

            node = segment.IsIndex
                ? (node as JsonArray) is { } array && segment.Index < array.Count ? array[segment.Index] : null
                : (node as JsonObject) is { } obj && obj.TryGetPropertyValue(segment.Name!, out JsonNode? child) ? child : null;
        }

        return node switch
        {
            null => null,
            JsonValue value => value.ToString(),
            _ => node.ToJsonString(),
        };
    }

    /// <summary>Segment d'une expression JsonPath : soit une propriete (<c>.nom</c>), soit un index (<c>[n]</c>).</summary>
    private readonly record struct JsonPathSegment(bool IsIndex, string? Name, int Index)
    {
        public static JsonPathSegment Property(string name) => new(IsIndex: false, name, Index: 0);

        public static JsonPathSegment ArrayIndex(int index) => new(IsIndex: true, Name: null, index);
    }

    [GeneratedRegex(@"\G(?:\.(?<name>[A-Za-z_][A-Za-z0-9_]*)|\[(?<index>\d+)\])")]
    private static partial Regex JsonPathSegmentPattern();

    /// <summary>
    /// Analyse une expression JsonPath en segments <c>.nom</c>/<c>[index]</c>. Ne couvre
    /// volontairement pas les caracteres generiques, filtres, descente recursive ni tranches.
    /// </summary>
    private static bool TryParseJsonPathSegments(string path, out List<JsonPathSegment> segments, out string? error)
    {
        segments = [];

        if (!path.StartsWith('$'))
        {
            error = "l'expression doit commencer par '$'.";
            return false;
        }

        int position = 1;
        while (position < path.Length)
        {
            Match match = JsonPathSegmentPattern().Match(path, position);
            if (!match.Success || match.Index != position)
            {
                error = $"segment non reconnu a partir de la position {position} ('{path[position..]}') — " +
                    "seuls '.nom' et '[index]' sont pris en charge (pas de '*', '..' ni de filtre).";
                return false;
            }

            segments.Add(match.Groups["name"].Success
                ? JsonPathSegment.Property(match.Groups["name"].Value)
                : JsonPathSegment.ArrayIndex(int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture)));

            position += match.Length;
        }

        error = null;
        return true;
    }
}