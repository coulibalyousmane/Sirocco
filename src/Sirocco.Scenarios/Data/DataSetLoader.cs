using System.Text;
using System.Text.Json;
using Sirocco.Domain.Data;

namespace Sirocco.Scenarios.Data;

/// <summary>
/// Charge un <see cref="DataSet"/> depuis un fichier CSV ou JSON.
/// <para>
/// Le format est deduit de l'extension du fichier, comme
/// <c>Sirocco.Scenarios.Declarative.ScenarioDefinitionLoader</c>. Le chargement n'est jamais sur
/// le chemin critique : il a lieu une seule fois, dans <c>IWorkflow.SetUpAsync</c>.
/// </para>
/// </summary>
public static class DataSetLoader
{
    /// <summary>Charge un jeu de donnees depuis un fichier <c>.csv</c> ou <c>.json</c>.</summary>
    /// <exception cref="FileNotFoundException">Le fichier n'existe pas.</exception>
    /// <exception cref="NotSupportedException">L'extension n'est pas reconnue.</exception>
    /// <exception cref="FormatException">Le contenu ne peut pas etre interprete comme un jeu de donnees.</exception>
    public static DataSet LoadFromFile(string path, DataSetIterationStrategy strategy = DataSetIterationStrategy.Circular)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fichier de jeu de donnees introuvable : '{path}'.", path);
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows;
        try
        {
            rows = extension switch
            {
                ".csv" => ParseCsv(File.ReadAllText(path)),
                ".json" => ParseJson(File.ReadAllText(path)),
                _ => throw new NotSupportedException(
                    $"Extension de jeu de donnees non reconnue : '{extension}'. Utilisez .csv ou .json."),
            };
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Jeu de donnees invalide dans '{path}' : {ex.Message}", ex);
        }

        try
        {
            return new DataSet(rows, strategy);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"Jeu de donnees invalide dans '{path}' : {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parse un contenu CSV deja lu : premiere ligne l'entete, une ligne par ligne suivante.
    /// Champs entre guillemets pris en charge (virgule ou saut de ligne a l'interieur, guillemet
    /// double pour un guillemet litteral), au sens RFC 4180.
    /// </summary>
    /// <exception cref="FormatException">Le contenu est vide.</exception>
    internal static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseCsv(string content)
    {
        List<List<string>> records = [];
        List<string> record = [];
        StringBuilder field = new();
        bool inQuotes = false;

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        if (records.Count == 0)
        {
            throw new FormatException("Le fichier CSV ne contient aucune ligne.");
        }

        List<string> header = records[0];
        List<IReadOnlyDictionary<string, string>> rows = [];
        for (int r = 1; r < records.Count; r++)
        {
            List<string> current = records[r];
            if (current.Count == 1 && current[0].Length == 0)
            {
                // Ligne vide en fin de fichier (dernier saut de ligne du fichier) : ignoree.
                continue;
            }

            Dictionary<string, string> row = new(StringComparer.Ordinal);
            for (int c = 0; c < header.Count && c < current.Count; c++)
            {
                row[header[c]] = current[c];
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Parse un contenu JSON deja lu : un tableau d'objets plats, une ligne par objet.</summary>
    /// <exception cref="FormatException">Le contenu n'est pas un tableau d'objets plats valide.</exception>
    internal static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseJson(string content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"JSON invalide : {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Le JSON d'un jeu de donnees doit etre un tableau d'objets.");
            }

            List<IReadOnlyDictionary<string, string>> rows = [];
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("Chaque element du jeu de donnees JSON doit etre un objet.");
                }

                Dictionary<string, string> row = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    row[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString()!,
                        JsonValueKind.Null => string.Empty,
                        _ => property.Value.GetRawText(),
                    };
                }

                rows.Add(row);
            }

            return rows;
        }
    }
}