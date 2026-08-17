using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Primitives;
using Tempest.HarConvert;
using Tempest.RecorderProxy;

// Proxy d'enregistrement a cible unique : retransmet fidelement chaque requete vers --target-url
// tout en l'enregistrant, puis genere un scenario scripte (.csx) a l'arret, via la meme
// HarConverter.Convert que Tempest.HarConvert - aucune capture manuelle de HAR entre les deux.

RecorderOptions options;
try
{
    options = RecorderOptions.Parse(args);
}
catch (FormatException ex)
{
    Console.Error.WriteLine($"Erreur : {ex.Message}");
    Console.Error.WriteLine(
        "Usage : Tempest.RecorderProxy --target-url <url> --out <scenario.csx> [--listen <url>] [--name <nom>]");
    return 1;
}

List<HarEntry> capturedEntries = [];
Lock capturedLock = new();

using HttpClient httpClient = new() { BaseAddress = new Uri(options.TargetUrl) };

WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
builder.WebHost.UseUrls(options.ListenUrl);
WebApplication app = builder.Build();

app.MapPost("/__tempest-recorder/stop", (IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.Ok();
});

app.MapFallback(async context =>
{
    await ForwardAndRecordAsync(context, httpClient, options.TargetUrl, capturedEntries, capturedLock);
});

await app.StartAsync();
Console.WriteLine($"Proxy d'enregistrement demarre sur {options.ListenUrl}, retransmission vers {options.TargetUrl}.");
Console.WriteLine("Ctrl+C pour arreter et generer le scenario (ou POST /__tempest-recorder/stop).");

await app.WaitForShutdownAsync();

List<HarEntry> entriesSnapshot;
lock (capturedLock)
{
    entriesSnapshot = [.. capturedEntries];
}

HarLog log = new() { Entries = entriesSnapshot };
HarConversionResult result = HarConverter.Convert(log, options.WorkflowName);
File.WriteAllText(options.OutputPath, result.Code);

Console.WriteLine(
    $"Scenario ecrit : {options.OutputPath} ({result.StepCount} etape(s) retenue(s) sur {entriesSnapshot.Count} requete(s) enregistree(s)).");

if (result.SkippedStaticAssetCount > 0)
{
    Console.WriteLine($"  {result.SkippedStaticAssetCount} requete(s) d'actif statique ignoree(s) (css/js/image/police).");
}

Console.WriteLine(
    "Verifier l'authentification et les cookies avant de rejouer : ce sont des valeurs de session enregistrees, probablement expirees.");

return 0;

static async Task ForwardAndRecordAsync(
    HttpContext context, HttpClient httpClient, string targetUrl, List<HarEntry> capturedEntries, Lock capturedLock)
{
    string method = context.Request.Method;
    string pathAndQuery = context.Request.Path + context.Request.QueryString;
    string? requestContentType = context.Request.ContentType;

    using MemoryStream bodyBuffer = new();
    await context.Request.Body.CopyToAsync(bodyBuffer, context.RequestAborted);
    byte[] bodyBytes = bodyBuffer.ToArray();

    HttpRequestMessage outbound = new(new HttpMethod(method), pathAndQuery);
    if (bodyBytes.Length > 0)
    {
        outbound.Content = new ByteArrayContent(bodyBytes);
        if (requestContentType is not null)
        {
            outbound.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(requestContentType);
        }
    }

    foreach (KeyValuePair<string, StringValues> header in context.Request.Headers)
    {
        if (ProxyHeaders.ShouldForward(header.Key))
        {
            outbound.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]);
        }
    }

    HttpResponseMessage response;
    try
    {
        response = await httpClient.SendAsync(outbound, context.RequestAborted);
    }
    catch (HttpRequestException ex)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsync($"Proxy Tempest : echec de retransmission vers la cible : {ex.Message}", context.RequestAborted);
        return;
    }

    context.Response.StatusCode = (int)response.StatusCode;
    foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
    {
        if (ProxyHeaders.ShouldForward(header.Key))
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
    if (response.Content.Headers.ContentType is not null)
    {
        context.Response.ContentType = response.Content.Headers.ContentType.ToString();
    }

    await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted);

    string? capturedBody = RecordedEntryBuilder.IsTextContent(requestContentType) && bodyBytes.Length > 0
        ? Encoding.UTF8.GetString(bodyBytes)
        : null;

    List<(string Name, string Value)> capturedHeaders = [.. context.Request.Headers.Select(h => (h.Key, string.Join(", ", h.Value.ToArray())))];
    HarEntry entry = RecordedEntryBuilder.Build(method, pathAndQuery, capturedHeaders, capturedBody, requestContentType, targetUrl);

    lock (capturedLock)
    {
        capturedEntries.Add(entry);
    }
}