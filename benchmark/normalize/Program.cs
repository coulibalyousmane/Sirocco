using System.Globalization;
using System.Text;
using System.Text.Json;

// Normalise les 4 sorties du benchmark comparatif (voir benchmark/README.md) en un seul
// RESULTS.md. Parsing manuel volontaire (pas de nouvelle dependance lourde), meme choix que
// tools/Tempest.Compare — mais ici en JsonDocument brut plutot qu'un DTO type, car les 4 formats
// sont trop heterogenes pour un seul schema commun (JSON structure, JSON k6, texte console
// Gatling, JSON maison NBomber).
//
// Asymetrie reelle et volontairement documentee plutot que masquee : les 4 outils n'exposent pas
// la meme granularite. Voir les notes dans GenerateMarkdown ci-dessous pour le detail exact de ce
// qui est comparable et ce qui ne l'est pas.

string benchmarkDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string resultsDir = args.Length > 0 ? args[0] : Path.Combine(benchmarkDir, "results");

string tempestPath = Path.Combine(resultsDir, "tempest.json");
string k6Path = Path.Combine(resultsDir, "k6.json");
string gatlingConsolePath = Path.Combine(resultsDir, "gatling", "console.log");
string nbomberPath = Path.Combine(resultsDir, "nbomber.json");
string outputPath = Path.Combine(resultsDir, "RESULTS.md");

foreach (string path in new[] { tempestPath, k6Path, gatlingConsolePath, nbomberPath })
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Erreur : fichier introuvable : '{path}'.");
        return 1;
    }
}

TempestResult tempest = ParseTempest(tempestPath);
K6Result k6 = ParseK6(k6Path);
GatlingResult gatling = ParseGatlingConsole(gatlingConsolePath);
NBomberResult nbomber = ParseNBomber(nbomberPath);

string markdown = GenerateMarkdown(tempest, k6, gatling, nbomber);
File.WriteAllText(outputPath, markdown);
Console.WriteLine($"Rapport ecrit : {outputPath}");
return 0;

static JsonDocument LoadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));

static CheckoutCounts Counts(long total, long ok, long fail) => new(total, ok, fail);

// ---- Tempest (benchmark/results/tempest.json, forme LoadTestReport exportee par /report) ----

static TempestResult ParseTempest(string path)
{
    using JsonDocument doc = LoadJson(path);
    JsonElement root = doc.RootElement;

    JsonElement iteration = root.GetProperty("iteration");
    JsonElement checkout = root.GetProperty("steps").EnumerateArray()
        .Single(step => step.GetProperty("name").GetString() == "checkout");

    return new TempestResult(
        Iteration: Counts(
            iteration.GetProperty("count").GetInt64(),
            iteration.GetProperty("successCount").GetInt64(),
            iteration.GetProperty("failureCount").GetInt64()),
        IterationResponse: LatencyFromMilliseconds(iteration.GetProperty("response")),
        IterationServiceP99Ms: iteration.GetProperty("service").GetProperty("p99Milliseconds").GetDouble(),
        MaxSchedulingDelayMs: iteration.GetProperty("maxSchedulingDelayMilliseconds").GetDouble(),
        Checkout: Counts(
            checkout.GetProperty("count").GetInt64(),
            checkout.GetProperty("successCount").GetInt64(),
            checkout.GetProperty("failureCount").GetInt64()),
        CheckoutResponse: LatencyFromMilliseconds(checkout.GetProperty("response")));
}

static LatencyMs LatencyFromMilliseconds(JsonElement latency) => new(
    latency.GetProperty("p50Milliseconds").GetDouble(),
    latency.GetProperty("p95Milliseconds").GetDouble(),
    latency.GetProperty("p99Milliseconds").GetDouble());

// ---- k6 (benchmark/results/k6.json, --summary-export) ----

static K6Result ParseK6(string path)
{
    using JsonDocument doc = LoadJson(path);
    JsonElement metrics = doc.RootElement.GetProperty("metrics");
    JsonElement checks = doc.RootElement.GetProperty("root_group").GetProperty("checks");
    JsonElement checkoutCheck = checks.GetProperty("checkout 200");

    JsonElement iterationDuration = metrics.GetProperty("iteration_duration");

    return new K6Result(
        Checkout: Counts(
            checkoutCheck.GetProperty("passes").GetInt64() + checkoutCheck.GetProperty("fails").GetInt64(),
            checkoutCheck.GetProperty("passes").GetInt64(),
            checkoutCheck.GetProperty("fails").GetInt64()),
        IterationDuration: new LatencyMs(
            iterationDuration.GetProperty("p(50)").GetDouble(),
            iterationDuration.GetProperty("p(95)").GetDouble(),
            iterationDuration.GetProperty("p(99)").GetDouble()));
}

// ---- Gatling (benchmark/results/gatling/console.log, sortie console capturee) ----
//
// Pas de stats.json dans cette version du bundle OSS (voir benchmark/gatling/Dockerfile) : on
// parse le texte de la console. Deux informations distinctes y sont disponibles :
//   1. Le dernier releve "> login" / "> checkout" avant "Parsing log file(s)..." : le compte
//      final par type de requete (total/OK/KO), fiable pour le taux d'echec par etape.
//   2. Le bloc final "---- Global Information ----" : percentiles de latence, mais uniquement
//      agreges login+checkout ensemble (colonne Total) — Gatling ne separe pas les percentiles
//      par nom de requete dans la console (seulement dans le rapport HTML complet, pas parse ici).

static GatlingResult ParseGatlingConsole(string path)
{
    string text = File.ReadAllText(path);
    string[] lines = text.Split('\n');

    CheckoutCounts login = LastRequestLineCounts(lines, "login");
    CheckoutCounts checkout = LastRequestLineCounts(lines, "checkout");

    int globalStart = text.IndexOf("---- Global Information", StringComparison.Ordinal);
    int globalEnd = text.IndexOf("---- Response Time Distribution", StringComparison.Ordinal);

    if (globalStart < 0 || globalEnd < 0 || globalEnd <= globalStart)
    {
        throw new FormatException($"Bloc 'Global Information' introuvable ou mal forme dans '{path}'.");
    }

    string globalBlock = text[globalStart..globalEnd];
    Dictionary<string, double[]> globalRows = ParsePipeTable(globalBlock);

    double[] requestCount = globalRows["request count"];
    double[] p50 = globalRows["response time 50th percentile (ms)"];
    double[] p95 = globalRows["response time 95th percentile (ms)"];
    double[] p99 = globalRows["response time 99th percentile (ms)"];

    return new GatlingResult(
        Login: login,
        Checkout: checkout,
        Global: Counts((long)requestCount[0], (long)requestCount[1], (long)requestCount[2]),
        GlobalLatency: new LatencyMs(p50[0], p95[0], p99[0]));
}

static CheckoutCounts LastRequestLineCounts(string[] lines, string requestName)
{
    string? lastMatch = lines
        .Where(line => line.TrimStart().StartsWith($"> {requestName}", StringComparison.Ordinal) && line.Contains('|'))
        .LastOrDefault();

    if (lastMatch is null)
    {
        throw new FormatException($"Aucune ligne '> {requestName}' trouvee dans la sortie console Gatling.");
    }

    double[] values = ParsePipeValues(lastMatch);
    return Counts((long)values[0], (long)values[1], (long)values[2]);
}

// Une ligne de tableau Gatling ressemble a :
//   > response time 50th percentile (ms)                                                 |       125 |       132 |        53
// Colonne 0 = Total, 1 = OK, 2 = KO.
static double[] ParsePipeValues(string line)
{
    string[] parts = line.Split('|');
    return parts.Skip(1).Select(part => double.Parse(part.Replace(",", "").Trim(), CultureInfo.InvariantCulture)).ToArray();
}

static Dictionary<string, double[]> ParsePipeTable(string block)
{
    var rows = new Dictionary<string, double[]>();

    foreach (string line in block.Split('\n'))
    {
        string trimmed = line.TrimStart();

        if (!trimmed.StartsWith('>') || !trimmed.Contains('|'))
        {
            continue;
        }

        int pipeIndex = trimmed.IndexOf('|', StringComparison.Ordinal);
        string label = trimmed[1..pipeIndex].Trim();
        rows[label] = ParsePipeValues(trimmed[pipeIndex..]);
    }

    return rows;
}

// ---- NBomber (benchmark/results/nbomber.json, ecrit par benchmark/nbomber/Program.cs) ----
//
// Format maison (pas de ReportFormat.Json cote NBomber, voir benchmark/nbomber/Program.cs) :
// on connait donc exactement sa forme, pas de tolerance a ajouter.

static NBomberResult ParseNBomber(string path)
{
    using JsonDocument doc = LoadJson(path);
    JsonElement scenario = doc.RootElement.GetProperty("scenarios").EnumerateArray().Single();
    JsonElement checkout = scenario.GetProperty("steps").EnumerateArray()
        .Single(step => step.GetProperty("name").GetString() == "checkout");

    long okCount = checkout.GetProperty("okCount").GetInt64();
    long failCount = checkout.GetProperty("failCount").GetInt64();
    JsonElement latency = checkout.GetProperty("okLatencyMs");

    return new NBomberResult(
        Checkout: Counts(okCount + failCount, okCount, failCount),
        CheckoutLatency: new LatencyMs(
            latency.GetProperty("p50").GetDouble(),
            latency.GetProperty("p95").GetDouble(),
            latency.GetProperty("p99").GetDouble()));
}

// ---- Rendu Markdown ----

static string GenerateMarkdown(TempestResult tempest, K6Result k6, GatlingResult gatling, NBomberResult nbomber)
{
    var sb = new StringBuilder();

    sb.AppendLine("# Résultats du benchmark comparatif");
    sb.AppendLine();
    sb.AppendLine("Généré par `benchmark/normalize` à partir des sorties réelles de");
    sb.AppendLine("`benchmark/results/{tempest.json,k6.json,gatling/console.log,nbomber.json}`.");
    sb.AppendLine("Méthodologie complète, protocole exact et limites : voir [README](README.md).");
    sb.AppendLine();

    sb.AppendLine("## Vue d'ensemble — requêtes de checkout (le point de saturation)");
    sb.AppendLine();
    sb.AppendLine("| Outil | Requêtes | OK | Échecs | Taux d'échec |");
    sb.AppendLine("|---|---:|---:|---:|---:|");
    AppendCountsRow(sb, "Tempest", tempest.Checkout);
    AppendCountsRow(sb, "k6", k6.Checkout);
    AppendCountsRow(sb, "Gatling", gatling.Checkout);
    AppendCountsRow(sb, "NBomber", nbomber.Checkout);
    sb.AppendLine();

    sb.AppendLine("## Latence de bout en bout (itération complète : login + checkout)");
    sb.AppendLine();
    sb.AppendLine("Comparable seulement entre outils qui exposent un temps total par itération.");
    sb.AppendLine("NBomber agrège ses statistiques par étape et par scénario (pool des échantillons de");
    sb.AppendLine("chaque étape), pas en sommant les étapes d'une même itération — ce n'est donc pas la");
    sb.AppendLine("même grandeur, d'où son absence ci-dessous plutôt qu'un chiffre trompeur.");
    sb.AppendLine();
    sb.AppendLine("| Outil | Métrique | p50 (ms) | p95 (ms) | p99 (ms) |");
    sb.AppendLine("|---|---|---:|---:|---:|");
    AppendLatencyRowWithMetric(sb, "Tempest", "Response (avec attente d'ordonnancement)", tempest.IterationResponse);
    sb.AppendLine($"| Tempest | Service (p99 seul, traitement pur) | — | — | {tempest.IterationServiceP99Ms:F1} |");
    AppendLatencyRowWithMetric(sb, "k6", "iteration_duration", k6.IterationDuration);
    AppendLatencyRowWithMetric(sb, "Gatling", "Global Information (colonne Total)", gatling.GlobalLatency);
    sb.AppendLine("| NBomber | — (voir note ci-dessus) | — | — | — |");
    sb.AppendLine();

    sb.AppendLine("## Latence de l'étape checkout seule");
    sb.AppendLine();
    sb.AppendLine("Tempest et NBomber exposent un percentile par étape nommée. k6 (sans tags/groupes");
    sb.AppendLine("par requête dans `benchmark/k6/checkout.js`) et Gatling (dont la console ne détaille");
    sb.AppendLine("les percentiles que globalement, pas par nom de requête) ne le permettent pas avec les");
    sb.AppendLine("artefacts capturés ici — limite réelle documentée plutôt que contournée.");
    sb.AppendLine();
    sb.AppendLine("| Outil | p50 (ms) | p95 (ms) | p99 (ms) |");
    sb.AppendLine("|---|---:|---:|---:|");
    AppendLatencyRow(sb, "Tempest", tempest.CheckoutResponse);
    sb.AppendLine("| k6 | — | — | — |");
    sb.AppendLine("| Gatling | — | — | — |");
    AppendLatencyRow(sb, "NBomber", nbomber.CheckoutLatency);
    sb.AppendLine();

    sb.AppendLine("## Le différenciateur Tempest : Response vs Service, et la dette d'ordonnancement");
    sb.AppendLine();
    sb.AppendLine("Aucun des trois autres outils ne publie cette distinction. Sur l'itération complète :");
    sb.AppendLine();
    sb.AppendLine($"- **Response p99** (ce que l'appelant attend réellement, file d'attente incluse) : " +
                  $"{tempest.IterationResponse.P99:F1} ms");
    sb.AppendLine($"- **Service p99** (temps de traitement pur, une fois la requête prise en charge) : " +
                  $"{tempest.IterationServiceP99Ms:F1} ms");
    sb.AppendLine($"- **Dette d'ordonnancement maximale observée** : {tempest.MaxSchedulingDelayMs:F1} ms");
    sb.AppendLine();
    sb.AppendLine("L'écart entre Response et Service sous charge est exactement le signal que k6, Gatling");
    sb.AppendLine("et NBomber ne rendent jamais visible : le moment où les chiffres qu'ils annoncent ne");
    sb.AppendLine("reflètent plus la réalité de la cible, sans qu'aucun indicateur ne le signale.");
    sb.AppendLine();

    return sb.ToString();
}

static void AppendCountsRow(StringBuilder sb, string tool, CheckoutCounts counts)
{
    double failRate = counts.Total == 0 ? 0 : (double)counts.Fail / counts.Total;
    sb.AppendLine($"| {tool} | {counts.Total} | {counts.Ok} | {counts.Fail} | {failRate:P1} |");
}

static void AppendLatencyRow(StringBuilder sb, string tool, LatencyMs latency) =>
    sb.AppendLine($"| {tool} | {latency.P50:F1} | {latency.P95:F1} | {latency.P99:F1} |");

static void AppendLatencyRowWithMetric(StringBuilder sb, string tool, string metric, LatencyMs latency) =>
    sb.AppendLine($"| {tool} | {metric} | {latency.P50:F1} | {latency.P95:F1} | {latency.P99:F1} |");

readonly record struct CheckoutCounts(long Total, long Ok, long Fail);

readonly record struct LatencyMs(double P50, double P95, double P99);

readonly record struct TempestResult(
    CheckoutCounts Iteration,
    LatencyMs IterationResponse,
    double IterationServiceP99Ms,
    double MaxSchedulingDelayMs,
    CheckoutCounts Checkout,
    LatencyMs CheckoutResponse);

readonly record struct K6Result(CheckoutCounts Checkout, LatencyMs IterationDuration);

readonly record struct GatlingResult(
    CheckoutCounts Login,
    CheckoutCounts Checkout,
    CheckoutCounts Global,
    LatencyMs GlobalLatency);

readonly record struct NBomberResult(CheckoutCounts Checkout, LatencyMs CheckoutLatency);
