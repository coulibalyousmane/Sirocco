namespace Sirocco.HarConvert;

/// <summary>
/// Forme minimale du format HAR (HTTP Archive, <c>.har</c>) exporte par les outils de
/// developpement d'un navigateur : seuls les champs que <see cref="HarConverter"/> exploite sont
/// modelises, le reste de la specification (timings, cache, cookies structures) est ignore sans
/// faire echouer la deserialisation (<c>PropertyNameCaseInsensitive</c>, champs non reconnus
/// simplement absents des objets ci-dessous).
/// </summary>
public sealed class HarDocument
{
    public HarLog Log { get; set; } = new();
}

/// <inheritdoc cref="HarDocument" />
public sealed class HarLog
{
    public List<HarEntry> Entries { get; set; } = [];
}

/// <inheritdoc cref="HarDocument" />
public sealed class HarEntry
{
    public HarRequest Request { get; set; } = new();
}

/// <inheritdoc cref="HarDocument" />
public sealed class HarRequest
{
    public string Method { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public List<HarHeader> Headers { get; set; } = [];

    public HarPostData? PostData { get; set; }
}

/// <inheritdoc cref="HarDocument" />
public sealed class HarHeader
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <inheritdoc cref="HarDocument" />
public sealed class HarPostData
{
    public string MimeType { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}