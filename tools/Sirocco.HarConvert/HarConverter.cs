using System.Text;

namespace Sirocco.HarConvert;

/// <summary>
/// Traduit un HAR (export du navigateur) en scenario scripte C# (<c>.csx</c>), conformement a la
/// decision structurante de la roadmap phase 2 (voir ROADMAP.md, "Implications pour les phases 5
/// et 6") : les convertisseurs generent du C#, pas du YAML/JSON — plus proche d'une generation de
/// client type que d'un simple mapping de requetes. Le script produit implemente
/// <c>Sirocco.Domain.Execution.IWorkflow</c> exactement comme <c>scenarios/scripted-checkout.csx</c>,
/// et se charge sans aucun cablage supplementaire via <c>WorkflowFileLoader</c>.
/// </summary>
public static class HarConverter
{
    // Un HAR de chargement de page complet est majoritairement fait d'actifs statiques (css/js/
    // images/polices) qu'on ne veut jamais rejouer contre une cible de tir de charge : les
    // ignorer par extension rend le scenario genere directement utilisable plutot que noye dans
    // du bruit, sans heuristique fragile sur le Content-Type de la reponse.
    private static readonly string[] _staticAssetExtensions =
    [
        ".css", ".js", ".mjs", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif",
        ".ico", ".woff", ".woff2", ".ttf", ".eot", ".otf", ".mp4", ".webm",
    ];

    // En-tetes geres autrement (Content-Type par StringContent) ou generes automatiquement par
    // le client HTTP/la pile reseau : les rejouer tels quels serait soit redondant, soit faux
    // (Content-Length d'un corps peut-etre modifie, pseudo-en-tetes HTTP/2 sans equivalent HTTP/1.1).
    private static readonly string[] _headersToStrip =
    [
        "host", "content-length", "connection", "content-type", "accept-encoding",
        ":method", ":path", ":scheme", ":authority",
    ];

    /// <summary>
    /// Convertit le journal HAR fourni en source C# d'un scenario scripte.
    /// <para>
    /// Seules les requetes vers l'hote le plus frequent du HAR sont converties (voir
    /// <see cref="MostCommonHost"/>) : un HAR capture souvent des appels tiers (analytics,
    /// polices, CDN) qu'un tir de charge contre la cible n'a pas vocation a rejouer. Les requetes
    /// ignorees sont comptees, jamais silencieuses.
    /// </para>
    /// </summary>
    public static HarConversionResult Convert(HarLog log, string workflowName)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);

        List<ConvertedStep> steps = [];
        HashSet<string> usedLabels = new(StringComparer.Ordinal);
        int skippedStaticAsset = 0;
        int skippedOtherHost = 0;

        // L'hote cible est celui qui revient le plus souvent, jamais "le premier rencontre" :
        // un HAR reel intercale presque toujours un appel tiers (police, analytics, CDN) avant
        // le premier appel a l'API testee, et ce tiers n'a le plus souvent aucune extension
        // reconnue dans son chemin — rien ne l'arrete au filtre d'actifs statiques. Le prendre
        // pour hote de base ferait passer la cible reelle elle-meme pour "un autre hote".
        string? baseHost = MostCommonHost(log.Entries);

        foreach (HarEntry entry in log.Entries)
        {
            HarRequest request = entry.Request;
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri))
            {
                continue;
            }

            if (IsStaticAsset(uri.AbsolutePath))
            {
                skippedStaticAsset++;
                continue;
            }

            string host = uri.GetLeftPart(UriPartial.Authority);

            if (!string.Equals(host, baseHost, StringComparison.OrdinalIgnoreCase))
            {
                skippedOtherHost++;
                continue;
            }

            string method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.ToUpperInvariant();
            string label = UniqueLabel($"{method} {uri.PathAndQuery}", usedLabels);

            List<(string Name, string Value)> headers = [];
            foreach (HarHeader header in request.Headers)
            {
                if (Array.IndexOf(_headersToStrip, header.Name.ToLowerInvariant()) >= 0)
                {
                    continue;
                }

                headers.Add((header.Name, header.Value));
            }

            string? body = string.IsNullOrEmpty(request.PostData?.Text) ? null : request.PostData.Text;
            string? contentType = body is null ? null : (string.IsNullOrWhiteSpace(request.PostData?.MimeType) ? "text/plain" : request.PostData.MimeType);

            steps.Add(new ConvertedStep(label, method, uri.PathAndQuery, body, contentType, headers));
        }

        string code = Render(workflowName, steps);
        return new HarConversionResult(code, steps.Count, skippedStaticAsset, skippedOtherHost, baseHost);
    }

    /// <summary>
    /// Hote le plus frequent parmi les requetes non-actif-statique du HAR, dans l'ordre de
    /// premiere apparition en cas d'egalite — deterministe, sans dependre de l'ordre d'iteration
    /// d'un dictionnaire.
    /// </summary>
    private static string? MostCommonHost(IReadOnlyList<HarEntry> entries)
    {
        List<string> hostsInOrder = [];
        Dictionary<string, int> countByHost = new(StringComparer.OrdinalIgnoreCase);

        foreach (HarEntry entry in entries)
        {
            if (!Uri.TryCreate(entry.Request.Url, UriKind.Absolute, out Uri? uri) || IsStaticAsset(uri.AbsolutePath))
            {
                continue;
            }

            string host = uri.GetLeftPart(UriPartial.Authority);
            if (countByHost.TryGetValue(host, out int count))
            {
                countByHost[host] = count + 1;
            }
            else
            {
                countByHost[host] = 1;
                hostsInOrder.Add(host);
            }
        }

        string? best = null;
        int bestCount = 0;
        foreach (string host in hostsInOrder)
        {
            if (countByHost[host] > bestCount)
            {
                best = host;
                bestCount = countByHost[host];
            }
        }

        return best;
    }

    private static bool IsStaticAsset(string path)
    {
        string extension = GetExtension(path);
        return extension.Length > 0 && Array.IndexOf(_staticAssetExtensions, extension) >= 0;
    }

    private static string GetExtension(string path)
    {
        int lastSlash = path.LastIndexOf('/');
        int lastDot = path.LastIndexOf('.');
        return lastDot > lastSlash ? path[lastDot..].ToLowerInvariant() : string.Empty;
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
        code.AppendLine("// Genere par Sirocco.HarConvert : verifier l'authentification et les cookies avant de");
        code.AppendLine("// rejouer, ce sont des valeurs de session enregistrees, probablement expirees.");
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
        List<(string Name, string Value)> Headers);
}

/// <summary>Resultat d'une conversion HAR : le script genere, plus de quoi rapporter ce qui a ete retenu ou ignore.</summary>
public sealed record HarConversionResult(
    string Code,
    int StepCount,
    int SkippedStaticAssetCount,
    int SkippedOtherHostCount,
    string? BaseHost);