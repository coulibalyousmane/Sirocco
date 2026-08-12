using System.Text;
using System.Text.Json.Nodes;

namespace Tempest.OpenApiConvert;

/// <summary>
/// Traduit une specification OpenAPI 3.x en scenario scripte C# (<c>.csx</c>), conformement a la
/// meme decision structurante que <c>Tempest.HarConvert</c> (voir ROADMAP.md, "Implications pour
/// les phases 5 et 6") : un convertisseur genere du C#, pas du YAML/JSON. Un step est cree par
/// operation (methode + chemin), avec un corps JSON d'exemple derive du schema quand disponible.
/// </summary>
public static class OpenApiConverter
{
    // Ordre fixe plutot que de deduire l'ordre des methodes depuis le JSON source : deterministe,
    // et independant de l'ordre dans lequel System.Text.Json a rempli les proprietes de
    // OpenApiPathItem.
    private static readonly (string Method, Func<OpenApiPathItem, OpenApiOperation?> Select)[] _verbs =
    [
        ("GET", static item => item.Get),
        ("POST", static item => item.Post),
        ("PUT", static item => item.Put),
        ("DELETE", static item => item.Delete),
        ("PATCH", static item => item.Patch),
        ("HEAD", static item => item.Head),
        ("OPTIONS", static item => item.Options),
    ];

    /// <summary>
    /// Convertit la specification fournie en source C# d'un scenario scripte.
    /// <para>
    /// Seul le contenu JSON des corps de requete est traduit en exemple (voir
    /// <see cref="BuildExample"/>) ; un autre type de contenu (multipart, XML, ...) laisse le step
    /// sans corps, compte plutot que silencieux. Les schemas d'authentification ne sont jamais
    /// traduits en en-tetes : comme pour un HAR, une vraie valeur de session ne peut venir que
    /// d'un humain, pas de la specification elle-meme.
    /// </para>
    /// </summary>
    public static OpenApiConversionResult Convert(OpenApiDocument document, string workflowName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);

        List<ConvertedStep> steps = [];
        HashSet<string> usedLabels = new(StringComparer.Ordinal);
        int skippedOperationless = 0;
        int unsupportedBody = 0;

        foreach ((string pathTemplate, OpenApiPathItem pathItem) in document.Paths)
        {
            int operationsForPath = 0;

            foreach ((string method, Func<OpenApiPathItem, OpenApiOperation?> select) in _verbs)
            {
                OpenApiOperation? operation = select(pathItem);
                if (operation is null)
                {
                    continue;
                }

                operationsForPath++;

                List<OpenApiParameter> pathParameters = [.. operation.Parameters.Where(static p => p.In == "path")];
                List<OpenApiParameter> queryParameters = [.. operation.Parameters.Where(static p => p.In == "query" && p.Required)];
                List<OpenApiParameter> headerParameters = [.. operation.Parameters.Where(static p => p.In == "header")];

                string path = SubstitutePathParameters(pathTemplate, pathParameters, document.Components);
                string query = BuildQueryString(queryParameters, document.Components);

                (string? body, string? contentType, string? unsupportedMimeType) = BuildBody(operation.RequestBody, document.Components);
                if (unsupportedMimeType is not null)
                {
                    unsupportedBody++;
                }

                List<(string Name, string Value)> headers = [.. headerParameters.Select(p => (p.Name, PlaceholderFor(p.Schema, document.Components, p.Example)))];

                string label = UniqueLabel(
                    string.IsNullOrWhiteSpace(operation.OperationId) ? $"{method} {pathTemplate}" : operation.OperationId,
                    usedLabels);

                steps.Add(new ConvertedStep(label, method, path + query, body, contentType, unsupportedMimeType, headers));
            }

            if (operationsForPath == 0)
            {
                skippedOperationless++;
            }
        }

        string code = Render(workflowName, steps);
        return new OpenApiConversionResult(code, steps.Count, skippedOperationless, unsupportedBody);
    }

    private static string SubstitutePathParameters(string pathTemplate, List<OpenApiParameter> parameters, OpenApiComponents? components)
    {
        string path = pathTemplate;
        foreach (OpenApiParameter parameter in parameters)
        {
            string placeholder = PlaceholderFor(parameter.Schema, components, parameter.Example);
            path = path.Replace(
                $"{{{parameter.Name}}}",
                Uri.EscapeDataString(placeholder),
                StringComparison.Ordinal);
        }

        return path;
    }

    private static string BuildQueryString(List<OpenApiParameter> parameters, OpenApiComponents? components)
    {
        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        IEnumerable<string> pairs = parameters.Select(p =>
            $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(PlaceholderFor(p.Schema, components, p.Example))}");

        return "?" + string.Join('&', pairs);
    }

    private static (string? Body, string? ContentType, string? UnsupportedMimeType) BuildBody(
        OpenApiRequestBody? requestBody, OpenApiComponents? components)
    {
        if (requestBody is null || requestBody.Content.Count == 0)
        {
            return (null, null, null);
        }

        KeyValuePair<string, OpenApiMediaType> jsonEntry = requestBody.Content.FirstOrDefault(
            kvp => string.Equals(kvp.Key, "application/json", StringComparison.OrdinalIgnoreCase));

        if (jsonEntry.Value is null)
        {
            return (null, null, requestBody.Content.Keys.First());
        }

        JsonNode? example = jsonEntry.Value.Example ?? BuildExample(jsonEntry.Value.Schema, components, new HashSet<string>(StringComparer.Ordinal));
        string bodyJson = (example ?? new JsonObject()).ToJsonString();

        return (bodyJson, "application/json", null);
    }

    /// <summary>
    /// Construit une valeur d'exemple JSON a partir d'un schema : l'<c>example</c> declare s'il y
    /// en a un, sinon un placeholder derive du type. Les references <c>$ref</c> locales sont
    /// resolues contre <paramref name="components"/> ; une reference introuvable ou un cycle
    /// (schema auto-referent, ex. un arbre) rendent un objet vide plutot que de boucler ou lever.
    /// </summary>
    private static JsonNode? BuildExample(OpenApiSchema? schema, OpenApiComponents? components, HashSet<string> visiting)
    {
        if (schema is null)
        {
            return null;
        }

        if (schema.Example is not null)
        {
            return schema.Example.DeepClone();
        }

        if (schema.Ref is not null)
        {
            string name = schema.Ref[(schema.Ref.LastIndexOf('/') + 1)..];
            if (!visiting.Add(name))
            {
                return new JsonObject();
            }

            OpenApiSchema? resolved = components?.Schemas.GetValueOrDefault(name);
            JsonNode? result = BuildExample(resolved, components, visiting);
            visiting.Remove(name);
            return result ?? new JsonObject();
        }

        if (schema.Enum is { Count: > 0 })
        {
            return schema.Enum[0]?.DeepClone() ?? JsonValue.Create("chaine");
        }

        if (schema.Type == "array")
        {
            JsonArray array = [];
            JsonNode? item = BuildExample(schema.Items, components, visiting);
            if (item is not null)
            {
                array.Add(item);
            }

            return array;
        }

        if (schema.Type == "object" || (schema.Type is null && schema.Properties.Count > 0))
        {
            JsonObject obj = [];
            foreach ((string name, OpenApiSchema propertySchema) in schema.Properties)
            {
                obj[name] = BuildExample(propertySchema, components, visiting);
            }

            return obj;
        }

        return schema.Type switch
        {
            "integer" => JsonValue.Create(0),
            "number" => JsonValue.Create(0.0),
            "boolean" => JsonValue.Create(false),
            _ => JsonValue.Create("chaine"),
        };
    }

    private static string PlaceholderFor(OpenApiSchema? schema, OpenApiComponents? components, JsonNode? example)
    {
        JsonNode? node = example ?? BuildExample(schema, components, new HashSet<string>(StringComparer.Ordinal));
        return node switch
        {
            null => "valeur",
            JsonValue value when value.TryGetValue(out string? s) => s,
            _ => node.ToJsonString().Trim('"'),
        };
    }

    private static string UniqueLabel(string label, HashSet<string> used)
    {
        if (used.Add(label))
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
        while (!used.Add(candidate));

        return candidate;
    }

    private static string Render(string workflowName, List<ConvertedStep> steps)
    {
        string className = ToIdentifier(workflowName, "GeneratedWorkflow");

        StringBuilder code = new();
        code.AppendLine("// Genere par Tempest.OpenApiConvert : les en-tetes d'authentification et les valeurs de");
        code.AppendLine("// parametres sont des placeholders a remplacer par de vraies donnees avant de rejouer.");
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
        if (step.UnsupportedBodyMimeType is not null)
        {
            code.AppendLine($"        // Corps ignore : type de contenu \"{Escape(step.UnsupportedBodyMimeType)}\" non pris en charge (JSON seul).");
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

    private sealed record ConvertedStep(
        string Label,
        string Method,
        string Path,
        string? Body,
        string? ContentType,
        string? UnsupportedBodyMimeType,
        List<(string Name, string Value)> Headers);
}

/// <summary>Resultat d'une conversion OpenAPI : le script genere, plus de quoi rapporter ce qui a ete retenu ou ignore.</summary>
public sealed record OpenApiConversionResult(
    string Code,
    int StepCount,
    int SkippedOperationlessPathCount,
    int OperationsWithUnsupportedBodyCount);