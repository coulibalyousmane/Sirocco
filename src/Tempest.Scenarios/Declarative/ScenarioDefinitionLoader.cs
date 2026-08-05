using System.Text.Json;
using Tempest.Domain.Declarative;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Tempest.Scenarios.Declarative;

/// <summary>
/// Charge un <see cref="ScenarioDefinition"/> depuis un fichier YAML ou JSON.
/// <para>
/// Le format est deduit de l'extension du fichier pour <see cref="LoadFromFile"/> ; les deux
/// formats partagent la meme convention de nommage (camelCase) pour qu'un meme scenario se lise
/// de la meme facon quelle que soit sa syntaxe.
/// </para>
/// <para>
/// Le chargement n'est jamais sur le chemin critique : il a lieu une seule fois, avant le
/// premier tir. Utiliser un deserialiseur reflechi ici — plutot que la generation de source
/// employee pour les contrats HTTP — est donc un choix delibere, pas un oubli.
/// </para>
/// </summary>
public static class ScenarioDefinitionLoader
{
    private static readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Charge un scenario depuis un fichier, en deduisant le format de son extension
    /// (<c>.yaml</c>, <c>.yml</c> ou <c>.json</c>).
    /// </summary>
    /// <exception cref="FileNotFoundException">Le fichier n'existe pas.</exception>
    /// <exception cref="NotSupportedException">L'extension n'est pas reconnue.</exception>
    /// <exception cref="FormatException">Le contenu ne peut pas etre interprete comme un scenario.</exception>
    public static ScenarioDefinition LoadFromFile(string path)
    {
        (string content, ScenarioFormat format) = ReadRaw(path);

        try
        {
            return Parse(content, format);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Scenario invalide dans '{path}' : {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Lit un fichier de scenario sans le parser, en deduisant son format de son extension.
    /// <para>
    /// Distinct de <see cref="LoadFromFile"/> : sert au mode distribue, ou le maitre lit le
    /// fichier depuis son propre systeme de fichiers pour en transmettre le contenu brut aux
    /// workers (voir <c>WorkerPrepareRequest</c>) — un worker distant n'a aucune raison de
    /// partager le meme chemin que le maitre.
    /// </para>
    /// </summary>
    /// <exception cref="FileNotFoundException">Le fichier n'existe pas.</exception>
    /// <exception cref="NotSupportedException">L'extension n'est pas reconnue.</exception>
    public static (string Content, ScenarioFormat Format) ReadRaw(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fichier de scenario introuvable : '{path}'.", path);
        }

        return (File.ReadAllText(path), FormatOf(path));
    }

    /// <summary>Interprete un contenu deja lu, dans le format indique.</summary>
    /// <exception cref="FormatException">Le contenu ne peut pas etre interprete comme un scenario.</exception>
    public static ScenarioDefinition Parse(string content, ScenarioFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        ScenarioDefinitionDto? dto;
        try
        {
            dto = format switch
            {
                ScenarioFormat.Yaml => _yamlDeserializer.Deserialize<ScenarioDefinitionDto>(content),
                ScenarioFormat.Json => JsonSerializer.Deserialize<ScenarioDefinitionDto>(content, _jsonOptions),
                _ => throw new NotSupportedException($"Format de scenario non pris en charge : {format}."),
            };
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or JsonException)
        {
            throw new FormatException($"Impossible d'interpreter le scenario ({format}) : {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new FormatException($"Le contenu du scenario ({format}) est vide ou nul.");
        }

        ScenarioDefinition definition = dto.ToDefinition();
        definition.Validate();
        return definition;
    }

    private static ScenarioFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".yaml" or ".yml" => ScenarioFormat.Yaml,
        ".json" => ScenarioFormat.Json,
        var extension => throw new NotSupportedException(
            $"Extension de fichier de scenario non reconnue : '{extension}'. Utilisez .yaml, .yml ou .json."),
    };
}