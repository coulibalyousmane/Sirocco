using Tempest.Domain.Data;
using Tempest.Domain.Declarative;

namespace Tempest.Scenarios.Declarative;

/// <summary>
/// Forme intermediaire, mutable et a types concrets, utilisee uniquement pour la
/// deserialisation YAML/JSON.
/// <para>
/// Ni YamlDotNet ni <see cref="System.Text.Json.JsonSerializer"/> ne savent construire un
/// <c>IReadOnlyList&lt;T&gt;</c> ou un <c>IReadOnlyDictionary&lt;K,V&gt;</c> par reflexion : il
/// leur faut un type concret (<see cref="List{T}"/>, <see cref="Dictionary{TKey,TValue}"/>).
/// Exposer <see cref="Domain.Declarative.ScenarioDefinition"/> avec des collections mutables
/// pour satisfaire ce besoin degraderait un objet-valeur immuable pour le confort d'un
/// desserialiseur ; cette classe isole ce compromis a la frontiere, avant de mapper vers le
/// type Domain reel via <see cref="ToDefinition"/>.
/// </para>
/// </summary>
internal sealed class ScenarioDefinitionDto
{
    public string Name { get; set; } = string.Empty;

    public List<HttpStepDefinitionDto> Steps { get; set; } = [];

    public List<DataSetDefinitionDto> Datasets { get; set; } = [];

    public ScenarioDefinition ToDefinition() => new()
    {
        Name = Name,
        Steps = Steps.ConvertAll(static step => step.ToDefinition()),
        Datasets = Datasets.ConvertAll(static dataset => dataset.ToDefinition()),
    };
}

/// <inheritdoc cref="ScenarioDefinitionDto" />
internal sealed class DataSetDefinitionDto
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Strategy { get; set; } = nameof(DataSetIterationStrategy.Circular);

    public DataSetDefinition ToDefinition() => new()
    {
        Name = Name,
        Path = Path,
        Strategy = Enum.TryParse(Strategy, ignoreCase: true, out DataSetIterationStrategy strategy)
            ? strategy
            : throw new FormatException(
                $"Strategie de jeu de donnees inconnue : '{Strategy}'. Valeurs possibles : " +
                $"{string.Join(", ", Enum.GetNames<DataSetIterationStrategy>())}."),
    };
}

/// <inheritdoc cref="ScenarioDefinitionDto" />
internal sealed class HttpStepDefinitionDto
{
    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string? Body { get; set; }

    public string ContentType { get; set; } = "application/json";

    public Dictionary<string, string> Headers { get; set; } = [];

    public List<int> ExpectedStatusCodes { get; set; } = [];

    public List<ExtractionRuleDto> Extract { get; set; } = [];

    public List<CheckRuleDto> Checks { get; set; } = [];

    public HttpStepDefinition ToDefinition() => new()
    {
        Name = Name,
        Method = Method,
        Path = Path,
        Body = Body,
        ContentType = ContentType,
        Headers = Headers,
        ExpectedStatusCodes = ExpectedStatusCodes,
        Extract = Extract.ConvertAll(static rule => rule.ToDefinition()),
        Checks = Checks.ConvertAll(static check => check.ToDefinition()),
    };
}

/// <inheritdoc cref="ScenarioDefinitionDto" />
internal sealed class ExtractionRuleDto
{
    public string Variable { get; set; } = string.Empty;

    public string? Regex { get; set; }

    public string? XPath { get; set; }

    public string? JsonPath { get; set; }

    public ExtractionRule ToDefinition() => new()
    {
        Variable = Variable,
        Regex = Regex,
        XPath = XPath,
        JsonPath = JsonPath,
    };
}

/// <inheritdoc cref="ScenarioDefinitionDto" />
internal sealed class CheckRuleDto
{
    public string Name { get; set; } = string.Empty;

    public string? Regex { get; set; }

    public string? XPath { get; set; }

    public string? JsonPath { get; set; }

    public string? Expected { get; set; }

    public CheckRule ToDefinition() => new()
    {
        Name = Name,
        Regex = Regex,
        XPath = XPath,
        JsonPath = JsonPath,
        Expected = Expected,
    };
}