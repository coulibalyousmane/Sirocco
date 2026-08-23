using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sirocco.OpenApiConvert;

/// <summary>Racine d'une specification OpenAPI 3.x, reduite aux champs que le convertisseur exploite.</summary>
public sealed class OpenApiDocument
{
    public Dictionary<string, OpenApiPathItem> Paths { get; set; } = [];

    public OpenApiComponents? Components { get; set; }
}

/// <inheritdoc cref="OpenApiDocument" />
public sealed class OpenApiPathItem
{
    public OpenApiOperation? Get { get; set; }

    public OpenApiOperation? Post { get; set; }

    public OpenApiOperation? Put { get; set; }

    public OpenApiOperation? Delete { get; set; }

    public OpenApiOperation? Patch { get; set; }

    public OpenApiOperation? Head { get; set; }

    public OpenApiOperation? Options { get; set; }
}

/// <inheritdoc cref="OpenApiDocument" />
public sealed class OpenApiOperation
{
    public string? OperationId { get; set; }

    public string? Summary { get; set; }

    public List<OpenApiParameter> Parameters { get; set; } = [];

    public OpenApiRequestBody? RequestBody { get; set; }
}

/// <inheritdoc cref="OpenApiDocument" />
public sealed class OpenApiParameter
{
    public string Name { get; set; } = string.Empty;

    public string In { get; set; } = string.Empty;

    public bool Required { get; set; }

    public OpenApiSchema? Schema { get; set; }

    public JsonNode? Example { get; set; }
}

/// <inheritdoc cref="OpenApiDocument" />
public sealed class OpenApiRequestBody
{
    public Dictionary<string, OpenApiMediaType> Content { get; set; } = [];
}

/// <inheritdoc cref="OpenApiDocument" />
public sealed class OpenApiMediaType
{
    public OpenApiSchema? Schema { get; set; }

    public JsonNode? Example { get; set; }
}

/// <inheritdoc cref="OpenApiDocument" />
public sealed class OpenApiSchema
{
    [JsonPropertyName("$ref")]
    public string? Ref { get; set; }

    public string? Type { get; set; }

    public string? Format { get; set; }

    public Dictionary<string, OpenApiSchema> Properties { get; set; } = [];

    public OpenApiSchema? Items { get; set; }

    public List<JsonNode?>? Enum { get; set; }

    public JsonNode? Example { get; set; }
}

/// <inheritdoc cref="OpenApiDocument" />
public sealed class OpenApiComponents
{
    public Dictionary<string, OpenApiSchema> Schemas { get; set; } = [];
}