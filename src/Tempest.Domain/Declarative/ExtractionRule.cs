using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Tempest.Domain.Declarative;

/// <summary>
/// Regle d'extraction d'une valeur depuis le corps d'une reponse, liee entre deux etapes
/// declaratives par un nom de variable.
/// <para>
/// Exactement une expression par regle : <see cref="Regex"/> (universelle, sur texte brut) ou
/// <see cref="XPath"/> (pour un corps XML). Pas de JSONPath — hors perimetre de cette version,
/// une extraction Regex suffit sur un corps JSON tant qu'aucune expression dediee n'est requise.
/// </para>
/// </summary>
public sealed record ExtractionRule
{
    /// <summary>Nom de la variable liee, reutilisable dans les etapes suivantes via <c>{{nom}}</c>.</summary>
    public required string Variable { get; init; }

    /// <summary>Motif Regex ; le premier groupe capturant est extrait, ou la correspondance entiere a defaut.</summary>
    public string? Regex { get; init; }

    /// <summary>Expression XPath, evaluee contre le corps interprete comme XML.</summary>
    public string? XPath { get; init; }

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

        if (hasRegex == hasXPath)
        {
            throw new ArgumentException(
                $"L'extraction de '{Variable}' doit fournir exactement une expression : Regex ou XPath, pas les deux ni aucune.",
                nameof(Regex));
        }

        if (hasRegex)
        {
            ValidateRegexSyntax();
        }
        else
        {
            ValidateXPathSyntax();
        }
    }

    /// <summary>Tente d'extraire la valeur depuis le corps de reponse fourni.</summary>
    public bool TryExtract(string responseBody, out string? value)
    {
        value = Regex is not null ? ExtractWithRegex(responseBody) : ExtractWithXPath(responseBody);
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
}