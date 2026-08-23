using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sirocco.PostmanConvert;

/// <summary>
/// Traduit une collection Postman (export v2.1) en scenario scripte C# (<c>.csx</c>), meme
/// decision structurante que <c>Sirocco.HarConvert</c>/<c>Sirocco.OpenApiConvert</c> (voir
/// ROADMAP.md, "Implications pour les phases 5 et 6") : un convertisseur genere du C#, pas du
/// YAML/JSON. Les dossiers d'une collection sont parcourus recursivement ; chaque requete feuille
/// devient un step.
/// </summary>
public static partial class PostmanConverter
{
    // Meme raison que dans Sirocco.HarConvert : geres autrement (Content-Type par le mode du
    // corps) ou generes par la pile HTTP, les rejouer tels quels serait redondant ou faux.
    private static readonly string[] _headersToStrip = ["host", "content-length", "connection", "content-type", "accept-encoding"];

    private static readonly Regex _variablePattern = VariablePattern();

    /// <summary>
    /// Convertit la collection fournie en source C# d'un scenario scripte.
    /// <para>
    /// Seules les variables declarees au niveau de la collection (<c>collection.variable</c>)
    /// sont resolues — un environnement Postman separe n'est pas dans le scope de ce premier
    /// tour. Une variable <c>{{...}}</c> sans valeur connue devient un placeholder, compte
    /// plutot que silencieux.
    /// </para>
    /// </summary>
    public static PostmanConversionResult Convert(PostmanCollection collection, string workflowName)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);

        Dictionary<string, string> variables = BuildVariableDictionary(collection.Variable);
        ConversionState state = new();

        WalkItems(collection.Item, string.Empty, variables, state);

        string code = Render(workflowName, state.Steps);
        return new PostmanConversionResult(code, state.Steps.Count, state.UnresolvedVariableCount, state.SkippedFormDataBodyCount);
    }

    private static void WalkItems(List<PostmanItem> items, string prefix, Dictionary<string, string> variables, ConversionState state)
    {
        foreach (PostmanItem item in items)
        {
            if (item.Item is { Count: > 0 } children)
            {
                WalkItems(children, QualifiedName(prefix, item.Name), variables, state);
                continue;
            }

            if (item.Request is not null)
            {
                ConvertRequest(item, prefix, variables, state);
            }
        }
    }

    private static void ConvertRequest(PostmanItem item, string prefix, Dictionary<string, string> variables, ConversionState state)
    {
        PostmanRequest request = item.Request!;
        string method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.ToUpperInvariant();

        string rawUrl = ExtractRawUrl(request.Url) ?? string.Empty;
        (string substitutedUrl, int unresolvedInUrl) = SubstituteVariables(rawUrl, variables);
        state.UnresolvedVariableCount += unresolvedInUrl;
        string path = ToPath(substitutedUrl);

        List<(string Name, string Value)> headers = [];
        foreach (PostmanHeader header in request.Header)
        {
            if (header.Disabled || Array.IndexOf(_headersToStrip, header.Key.ToLowerInvariant()) >= 0)
            {
                continue;
            }

            (string value, int unresolvedInHeader) = SubstituteVariables(header.Value, variables);
            state.UnresolvedVariableCount += unresolvedInHeader;
            headers.Add((header.Key, value));
        }

        (string? body, string? contentType, bool unsupportedFormData, int unresolvedInBody) = BuildBody(request.Body, variables);
        state.UnresolvedVariableCount += unresolvedInBody;
        if (unsupportedFormData)
        {
            state.SkippedFormDataBodyCount++;
        }

        string label = state.UniqueLabel(
            string.IsNullOrWhiteSpace(item.Name) ? $"{method} {path}" : QualifiedName(prefix, item.Name));

        state.Steps.Add(new ConvertedStep(label, method, path, body, contentType, unsupportedFormData, headers));
    }

    private static string QualifiedName(string prefix, string? name) =>
        string.IsNullOrEmpty(prefix) ? name ?? string.Empty : $"{prefix} / {name}";

    private static Dictionary<string, string> BuildVariableDictionary(List<PostmanVariable> variables)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (PostmanVariable variable in variables)
        {
            if (string.IsNullOrEmpty(variable.Key))
            {
                continue;
            }

            result[variable.Key] = NodeToText(variable.Value) ?? string.Empty;
        }

        return result;
    }

    private static (string Text, int UnresolvedCount) SubstituteVariables(string text, Dictionary<string, string> variables)
    {
        int unresolved = 0;
        string result = _variablePattern.Replace(text, match =>
        {
            if (variables.TryGetValue(match.Groups[1].Value, out string? value))
            {
                return value;
            }

            unresolved++;
            return "valeur";
        });

        return (result, unresolved);
    }

    /// <summary>Postman represente "url" en chaine (v2.0) ou en objet {raw, ...} (v2.1) : les deux sont acceptes.</summary>
    private static string? ExtractRawUrl(JsonNode? url) => url switch
    {
        null => null,
        JsonValue value when value.TryGetValue(out string? s) => s,
        JsonObject obj => obj["raw"]?.GetValue<string>(),
        _ => null,
    };

    /// <summary>
    /// Chemin + requete a partir de l'URL substituee : si elle est absolue (l'hote a ete resolu
    /// via une variable de collection comme <c>{{baseUrl}}</c>), on ne garde que
    /// <see cref="Uri.PathAndQuery"/> ; sinon, on garde tout a partir du premier <c>/</c> — l'hote
    /// est de toute facon ignore a l'execution, fixe par <c>--target-url</c>.
    /// </summary>
    private static string ToPath(string substitutedUrl)
    {
        if (Uri.TryCreate(substitutedUrl, UriKind.Absolute, out Uri? uri))
        {
            return uri.PathAndQuery;
        }

        int slash = substitutedUrl.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 ? substitutedUrl[slash..] : "/";
    }

    private static (string? Body, string? ContentType, bool UnsupportedFormData, int UnresolvedCount) BuildBody(
        PostmanBody? body, Dictionary<string, string> variables)
    {
        switch (body?.Mode)
        {
            case "raw" when body.Raw is not null:
                (string text, int unresolved) = SubstituteVariables(body.Raw, variables);
                string contentType = body.Options?.Raw?.Language switch
                {
                    "json" => "application/json",
                    "xml" => "application/xml",
                    "html" => "text/html",
                    _ => "text/plain",
                };
                return (text, contentType, false, unresolved);

            case "urlencoded":
                int totalUnresolved = 0;
                List<string> pairs = [];
                foreach (PostmanHeader entry in body.Urlencoded)
                {
                    if (entry.Disabled)
                    {
                        continue;
                    }

                    (string value, int unresolvedInValue) = SubstituteVariables(entry.Value, variables);
                    totalUnresolved += unresolvedInValue;
                    pairs.Add($"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(value)}");
                }

                return (string.Join('&', pairs), "application/x-www-form-urlencoded", false, totalUnresolved);

            case "formdata":
                return (null, null, true, 0);

            default:
                return (null, null, false, 0);
        }
    }

    private static string? NodeToText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue(out string? s) => s,
        _ => node.ToJsonString().Trim('"'),
    };

    private static string Render(string workflowName, List<ConvertedStep> steps)
    {
        string className = ToIdentifier(workflowName, "GeneratedWorkflow");

        StringBuilder code = new();
        code.AppendLine("// Genere par Sirocco.PostmanConvert : les en-tetes d'authentification et les variables");
        code.AppendLine("// non resolues sont des placeholders a remplacer par de vraies donnees avant de rejouer.");
        code.AppendLine();
        code.AppendLine("using System.Text;");
        code.AppendLine();
        code.AppendLine($"public sealed class {className} : IWorkflow");
        code.AppendLine("{");

        for (int i = 0; i < steps.Count; i++)
        {
            code.AppendLine($"    private StepId _step{i};");
        }

        code.AppendLine();
        code.AppendLine($"    public string Name => \"{Escape(workflowName)}\";");
        code.AppendLine();
        code.AppendLine("    public void RegisterSteps(StepRegistry registry)");
        code.AppendLine("    {");

        for (int i = 0; i < steps.Count; i++)
        {
            code.AppendLine($"        _step{i} = registry.Register(\"{Escape(steps[i].Label)}\");");
        }

        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)");
        code.AppendLine("    {");

        for (int i = 0; i < steps.Count; i++)
        {
            AppendStep(code, steps[i], i);
        }

        code.AppendLine("    }");
        code.AppendLine("}");
        code.AppendLine();
        code.AppendLine($"new {className}()");

        return code.ToString();
    }

    private static void AppendStep(StringBuilder code, ConvertedStep step, int index)
    {
        if (step.UnsupportedFormData)
        {
            code.AppendLine("        // Corps ignore : mode \"formdata\" non pris en charge (JSON/urlencoded/texte brut seuls).");
        }

        code.AppendLine($"        StepScope scope{index} = context.BeginStep(_step{index});");
        code.Append($"        HttpRequestMessage request{index} = new(new HttpMethod(\"{Escape(step.Method)}\"), \"{Escape(step.Path)}\")");

        if (step.Body is null)
        {
            code.AppendLine(";");
        }
        else
        {
            code.AppendLine();
            code.AppendLine("        {");
            code.AppendLine($"            Content = new StringContent(\"{Escape(step.Body)}\", Encoding.UTF8, \"{Escape(step.ContentType!)}\"),");
            code.AppendLine("        };");
        }

        foreach ((string name, string value) in step.Headers)
        {
            code.AppendLine($"        request{index}.Headers.TryAddWithoutValidation(\"{Escape(name)}\", \"{Escape(value)}\");");
        }

        code.AppendLine($"        HttpResponseMessage response{index} = await context.HttpClient.SendAsync(request{index}, cancellationToken);");
        code.AppendLine($"        scope{index}.CompleteHttp((int)response{index}.StatusCode);");
        code.AppendLine();
    }

    /// <summary>Derive un identifiant C# valide d'un texte quelconque, ou repli si rien n'en reste.</summary>
    private static string ToIdentifier(string text, string fallback)
    {
        StringBuilder builder = new();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        string sanitized = builder.ToString().Trim('_');
        if (sanitized.Length == 0)
        {
            return fallback;
        }

        return char.IsDigit(sanitized[0]) ? "_" + sanitized : sanitized;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    /// <summary>Etat mutable accumule pendant la marche recursive des dossiers de la collection.</summary>
    private sealed class ConversionState
    {
        private readonly HashSet<string> _usedLabels = new(StringComparer.Ordinal);

        public List<ConvertedStep> Steps { get; } = [];

        public int UnresolvedVariableCount { get; set; }

        public int SkippedFormDataBodyCount { get; set; }

        public string UniqueLabel(string label)
        {
            if (_usedLabels.Add(label))
            {
                return label;
            }

            int suffix = 2;
            string candidate;
            do
            {
                candidate = $"{label} ({suffix})";
                suffix++;
            }
            while (!_usedLabels.Add(candidate));

            return candidate;
        }
    }

    private sealed record ConvertedStep(
        string Label,
        string Method,
        string Path,
        string? Body,
        string? ContentType,
        bool UnsupportedFormData,
        List<(string Name, string Value)> Headers);

    [GeneratedRegex(@"\{\{([^{}]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex VariablePattern();
}

/// <summary>Resultat d'une conversion Postman : le script genere, plus de quoi rapporter ce qui a ete retenu ou ignore.</summary>
public sealed record PostmanConversionResult(
    string Code,
    int StepCount,
    int UnresolvedVariableCount,
    int SkippedFormDataBodyCount);