using Sirocco.HarConvert;

namespace Sirocco.RecorderProxy;

/// <summary>
/// Construit un <see cref="HarEntry"/> a partir d'une requete capturee en direct — la meme
/// forme qu'un export HAR de navigateur, pour que <see cref="HarConverter"/> s'applique sans
/// aucune modification (filtrage des actifs statiques, des en-tetes de bout en bout au rendu,
/// generation du <c>.csx</c>).
/// <para>
/// Volontairement pure : aucune dependance reseau, testable en isolation.
/// </para>
/// </summary>
public static class RecordedEntryBuilder
{
    /// <summary>
    /// Un corps binaire ou non textuel n'est jamais capture (<paramref name="body"/> doit deja
    /// avoir ete decode par l'appelant, ou laisse nul) — meme limite documentee que pour le HAR :
    /// seul un corps texte devient un <c>postData.text</c>, un corps binaire reste retransmis en
    /// direct sans etre enregistre dans le scenario genere.
    /// </summary>
    public static HarEntry Build(
        string method,
        string pathAndQuery,
        IEnumerable<(string Name, string Value)> headers,
        string? body,
        string? contentType,
        string targetBaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBaseUrl);

        HarRequest request = new()
        {
            Method = method,
            Url = targetBaseUrl.TrimEnd('/') + pathAndQuery,
        };

        foreach ((string name, string value) in headers)
        {
            request.Headers.Add(new HarHeader { Name = name, Value = value });
        }

        if (!string.IsNullOrEmpty(body))
        {
            request.PostData = new HarPostData { MimeType = contentType ?? "text/plain", Text = body };
        }

        return new HarEntry { Request = request };
    }

    /// <summary>
    /// Un corps binaire (upload de fichier, image, protobuf, ...) n'a pas de sens decode en
    /// texte pour un <c>postData.text</c> — decider ici, a partir du seul type de contenu, evite
    /// de dupliquer cette heuristique dans <c>Program.cs</c>.
    /// </summary>
    public static bool IsTextContent(string? contentType)
    {
        if (contentType is null)
        {
            return false;
        }

        return contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("graphql", StringComparison.OrdinalIgnoreCase);
    }
}