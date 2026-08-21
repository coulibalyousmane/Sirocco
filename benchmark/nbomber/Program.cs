using System.Text;
using System.Text.Json;
using NBomber;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using NBomber.Http.CSharp;

// Scenario NBomber du benchmark comparatif (voir benchmark/README.md) : meme sequence exacte que
// benchmark/scenarios/tempest-checkout.yaml, benchmark/k6/checkout.js et
// benchmark/gatling/CheckoutSimulation.java (login puis checkout, meme panier), meme rampe
// 20 -> 150 iterations/s sur 90s. Simulation.Inject(20) puis Simulation.RampingInject(150) :
// RampingInject rampe depuis le debit de la simulation precedente, donc cette paire reproduit le
// palier 20 -> 150 des trois autres outils.
//
// NBomber.WithReportFormats ne propose pas de format JSON a ce jour (seulement Csv/Html/Md/Txt) —
// plutot que de contourner cette limite silencieusement, on serialise nous-memes les stats
// retournees par NBomberRunner.Run() (NodeStats) vers benchmark/results/nbomber.json, au format
// attendu par benchmark/normalize.

// Ancre sur AppContext.BaseDirectory (bin/<config>/net10.0 sous ce projet), pas sur
// Directory.GetCurrentDirectory() : ce dernier reste le repertoire d'ou `dotnet run` a ete
// invoque (pas forcement benchmark/nbomber), ce qui a deja fait ecrire un run reel dans un
// dossier "results" cree par erreur en dehors du depot.
string benchmarkDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

string targetUrl = Environment.GetEnvironmentVariable("TARGET_URL") ?? "http://localhost:5281";
string resultsPath = Environment.GetEnvironmentVariable("RESULTS_PATH")
    ?? Path.Combine(benchmarkDir, "results", "nbomber.json");
string reportFolder = Environment.GetEnvironmentVariable("REPORT_FOLDER")
    ?? Path.Combine(benchmarkDir, "results", "nbomber");

using var httpClient = Http.CreateDefaultClient();

var scenario = Scenario.Create("benchmark_checkout", async context =>
{
    string? token = null;

    var login = await Step.Run("login", context, async () =>
    {
        var request = Http.CreateRequest("POST", $"{targetUrl}/api/auth/login")
            .WithHeader("Content-Type", "application/json")
            .WithBody(new StringContent("""{"username":"demo","password":"demo"}""", Encoding.UTF8, "application/json"));

        var response = await Http.Send(httpClient, request);

        if (!response.IsError && response.Payload.IsSome())
        {
            string body = await response.Payload.Value.Content.ReadAsStringAsync();
            token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString();
        }

        return response;
    });

    if (login.IsError || token is null)
    {
        return Response.Fail();
    }

    var checkout = await Step.Run("checkout", context, async () =>
    {
        var request = Http.CreateRequest("POST", $"{targetUrl}/api/checkout")
            .WithHeader("Content-Type", "application/json")
            .WithHeader("Authorization", $"Bearer {token}")
            .WithBody(new StringContent("""{"items":[{"productId":1,"quantity":2}]}""", Encoding.UTF8, "application/json"));

        return await Http.Send(httpClient, request);
    });

    return checkout;
})
.WithoutWarmUp()
.WithLoadSimulations(
    Simulation.Inject(rate: 20, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(1)),
    Simulation.RampingInject(rate: 150, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(89))
);

var stats = NBomberRunner
    .RegisterScenarios(scenario)
    .WithReportFolder(reportFolder)
    .WithReportFormats(ReportFormat.Txt)
    .Run();

WriteJsonReport(stats, resultsPath);

static void WriteJsonReport(NodeStats stats, string path)
{
    var report = new
    {
        durationSeconds = stats.Duration.TotalSeconds,
        scenarios = stats.ScenarioStats.Select(scenarioStats => new
        {
            name = scenarioStats.ScenarioName,
            okCount = scenarioStats.Ok.Request.Count,
            failCount = scenarioStats.Fail.Request.Count,
            steps = scenarioStats.StepStats.Select(step => new
            {
                name = step.StepName,
                okCount = step.Ok.Request.Count,
                failCount = step.Fail.Request.Count,
                okLatencyMs = new
                {
                    min = step.Ok.Latency.MinMs,
                    mean = step.Ok.Latency.MeanMs,
                    max = step.Ok.Latency.MaxMs,
                    p50 = step.Ok.Latency.Percent50,
                    p75 = step.Ok.Latency.Percent75,
                    p95 = step.Ok.Latency.Percent95,
                    p99 = step.Ok.Latency.Percent99,
                },
            }),
        }),
    };

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Rapport JSON ecrit : {path}");
}
