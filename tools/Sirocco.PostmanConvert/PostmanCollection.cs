using System.Text.Json.Nodes;

namespace Sirocco.PostmanConvert;

/// <summary>Racine d'une collection Postman (export v2.1), reduite aux champs que le convertisseur exploite.</summary>
public sealed class PostmanCollection
{
    public List<PostmanItem> Item { get; set; } = [];

    public List<PostmanVariable> Variable { get; set; } = [];
}

/// <summary>
/// Un item Postman est soit un dossier (<see cref="Item"/> non vide), soit une requete
/// (<see cref="Request"/> non nul) — jamais les deux en pratique dans un export reel.
/// </summary>
public sealed class PostmanItem
{
    public string? Name { get; set; }

    public List<PostmanItem>? Item { get; set; }

    public PostmanRequest? Request { get; set; }
}

/// <inheritdoc cref="PostmanCollection" />
public sealed class PostmanRequest
{
    public string? Method { get; set; }

    public List<PostmanHeader> Header { get; set; } = [];

    // Postman represente "url" tantot comme une simple chaine (v2.0), tantot comme un objet
    // {raw, host, path, query} (v2.1, l'export courant aujourd'hui) : un JsonNode encaisse les
    // deux formes sans faire echouer la desserialisation de toute la collection pour une seule
    // requete d'un format different.
    public JsonNode? Url { get; set; }

    public PostmanBody? Body { get; set; }
}

/// <inheritdoc cref="PostmanCollection" />
public sealed class PostmanHeader
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool Disabled { get; set; }
}

/// <inheritdoc cref="PostmanCollection" />
public sealed class PostmanBody
{
    public string? Mode { get; set; }

    public string? Raw { get; set; }

    public List<PostmanHeader> Urlencoded { get; set; } = [];

    public PostmanBodyOptions? Options { get; set; }
}

/// <inheritdoc cref="PostmanCollection" />
public sealed class PostmanBodyOptions
{
    public PostmanRawOptions? Raw { get; set; }
}

/// <inheritdoc cref="PostmanCollection" />
public sealed class PostmanRawOptions
{
    public string? Language { get; set; }
}

/// <inheritdoc cref="PostmanCollection" />
public sealed class PostmanVariable
{
    public string? Key { get; set; }

    public JsonNode? Value { get; set; }
}