using System.Globalization;
using System.Text;
using System.Text.Json;

// Normalise les 4 sorties du benchmark comparatif (voir benchmark/README.md) en un seul
// RESULTS.md. Parsing manuel volontaire (pas de nouvelle dependance lourde), meme choix que
// tools/Sirocco.Compare — mais ici en JsonDocument brut plutot qu'un DTO type, car les 4 formats
// sont trop heterogenes pour un seul schema commun (JSON structure, JSON k6, texte console
// Gatling, JSON maison NBomber).
//
// Asymetrie reelle et volontairement documentee plutot que masquee : les 4 outils n'exposent pas
// la meme granularite. Voir les notes dans GenerateMarkdown ci-dessous pour le detail exact de ce
// qui est comparable et ce qui ne l'est pas.

string benchmarkDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string repoRoot = Path.GetFullPath(Path.Combine(benchmarkDir, ".."));
string resultsDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
    ? args[0]
    : Path.Combine(benchmarkDir, "results");

// Mode saturation : rend l'experience de l'article sur la dette d'ordonnancement
// (docs/articles/) au lieu du benchmark comparatif. Meme parseurs, autre rendu.
bool saturation = args.Contains("--saturation");
long plannedRequests = ReadLongArgument(args, "--planned");
int maxVirtualUsers = (int)ReadLongArgument(args, "--max-vus");

string siroccoPath = Path.Combine(resultsDir, "sirocco.json");
string k6Path = Path.Combine(resultsDir, "k6.json");
string gatlingConsolePath = Path.Combine(resultsDir, "gatling", "console.log");
string nbomberPath = Path.Combine(resultsDir, "nbomber.json");
string controlSiroccoPath = Path.Combine(resultsDir, "temoin", "sirocco.json");

string[] required = saturation
    ? [siroccoPath, k6Path, gatlingConsolePath, nbomberPath, controlSiroccoPath]
    : [siroccoPath, k6Path, gatlingConsolePath, nbomberPath];

foreach (string path in required)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Erreur : fichier introuvable : '{path}'.");
        return 1;
    }
}

SiroccoResult sirocco = ParseSirocco(siroccoPath);
K6Result k6 = ParseK6(k6Path);
GatlingResult gatling = ParseGatlingConsole(gatlingConsolePath);
NBomberResult nbomber = ParseNBomber(nbomberPath);

if (!saturation)
{
    string markdown = GenerateMarkdown(sirocco, k6, gatling, nbomber);
    string outputPath = Path.Combine(resultsDir, "RESULTS.md");
    File.WriteAllText(outputPath, markdown);
    Console.WriteLine($"Rapport ecrit : {outputPath}");
    return 0;
}

// Une seule structure de donnees, trois rendus : le rapport du dossier de resultats, et les deux
// fragments de mesures inclus par les articles FR et EN. Les deux articles n'ecrivent AUCUN
// chiffre a la main, ils incluent ces fragments — un chiffre juste dans une langue et faux dans
// l'autre devient structurellement impossible.
SaturationData data = new(
    Sirocco: sirocco,
    Control: ParseSirocco(controlSiroccoPath),
    K6: k6,
    Gatling: gatling,
    NBomber: nbomber,
    PlannedRequests: plannedRequests,
    MaxVirtualUsers: maxVirtualUsers);

string saturationPath = Path.Combine(resultsDir, "SATURATION.md");
File.WriteAllText(saturationPath, GenerateSaturationReport(data));
Console.WriteLine($"Rapport ecrit : {saturationPath}");

string frenchFragment = Path.Combine(repoRoot, "docs", "articles", "_mesures-fr.md");
string englishFragment = Path.Combine(repoRoot, "docs", "articles", "_mesures-en.md");
Directory.CreateDirectory(Path.GetDirectoryName(frenchFragment)!);
File.WriteAllText(frenchFragment, GenerateFrenchFragment(data));
File.WriteAllText(englishFragment, GenerateEnglishFragment(data));
Console.WriteLine($"Fragments ecrits : {frenchFragment}, {englishFragment}");
return 0;

static long ReadLongArgument(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length
        ? long.Parse(args[index + 1], CultureInfo.InvariantCulture)
        : 0L;
}

static JsonDocument LoadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));

static CheckoutCounts Counts(long total, long ok, long fail) => new(total, ok, fail);

// ---- Sirocco (benchmark/results/sirocco.json, forme LoadTestReport exportee par /report) ----

static SiroccoResult ParseSirocco(string path)
{
    using JsonDocument doc = LoadJson(path);
    JsonElement root = doc.RootElement;

    JsonElement iteration = root.GetProperty("iteration");
    JsonElement steps = root.GetProperty("steps");
    JsonElement checkout = steps.EnumerateArray()
        .Single(step => step.GetProperty("name").GetString() == "checkout");

    // Toutes les etapes, dans l'ordre de declaration : l'article a besoin de montrer que la dette
    // se loge sur le PREMIER pas de l'iteration et sur lui seul (VirtualUserContext.BeginStep).
    // Lue etape par etape sur le second pas, la saturation est invisible.
    List<StepLatency> allSteps = [];
    foreach (JsonElement step in steps.EnumerateArray())
    {
        allSteps.Add(new StepLatency(
            Name: step.GetProperty("name").GetString() ?? "?",
            Count: step.GetProperty("count").GetInt64(),
            ResponseP99Ms: step.GetProperty("response").GetProperty("p99Milliseconds").GetDouble(),
            ServiceP99Ms: step.GetProperty("service").GetProperty("p99Milliseconds").GetDouble(),
            MaxSchedulingDelayMs: step.GetProperty("maxSchedulingDelayMilliseconds").GetDouble()));
    }

    return new SiroccoResult(
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
        CheckoutResponse: LatencyFromMilliseconds(checkout.GetProperty("response")),
        IterationService: LatencyFromMilliseconds(iteration.GetProperty("service")),
        IterationDropped: iteration.GetProperty("droppedCount").GetInt64(),
        OmissionP99Ms: iteration.GetProperty("coordinatedOmissionP99Milliseconds").GetDouble(),
        DurationSeconds: TimeSpan.Parse(root.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture).TotalSeconds,
        AllSteps: allSteps);
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

    // k6 n'emet `dropped_iterations` que s'il a REELLEMENT dope au moins une iteration : la
    // metrique est absente du k6.json du benchmark publie (verifie). Absente == zero, ce qui est
    // justement le signal que l'article commente.
    return new K6Result(
        Checkout: Counts(
            checkoutCheck.GetProperty("passes").GetInt64() + checkoutCheck.GetProperty("fails").GetInt64(),
            checkoutCheck.GetProperty("passes").GetInt64(),
            checkoutCheck.GetProperty("fails").GetInt64()),
        IterationDuration: new LatencyMs(
            iterationDuration.GetProperty("p(50)").GetDouble(),
            iterationDuration.GetProperty("p(95)").GetDouble(),
            iterationDuration.GetProperty("p(99)").GetDouble()),
        Iterations: CounterValue(metrics, "iterations"),
        DroppedIterations: CounterValue(metrics, "dropped_iterations"));
}

static long CounterValue(JsonElement metrics, string name) =>
    metrics.TryGetProperty(name, out JsonElement metric) && metric.TryGetProperty("count", out JsonElement count)
        ? (long)count.GetDouble()
        : 0L;

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
        GlobalLatency: new LatencyMs(p50[0], p95[0], p99[0]),
        Errors: ParseGatlingErrors(text));
}

// Dernier bloc "---- Errors ----" de la console. Il n'apparait que si le tir a produit des KO, et
// la console le reimprime a chaque releve de progression : seule la derniere occurrence est le
// bilan final. C'est ce bloc qui dit POURQUOI une partie de la charge n'a pas ete delivree — par
// exemple une exception reseau quand un injecteur non borne epuise les sockets de la machine.
static IReadOnlyList<GatlingError> ParseGatlingErrors(string text)
{
    int start = text.LastIndexOf("---- Errors", StringComparison.Ordinal);
    if (start < 0)
    {
        return [];
    }

    List<GatlingError> errors = [];

    foreach (string line in text[start..].Split('\n').Skip(1))
    {
        string trimmed = line.Trim();

        if (!trimmed.StartsWith('>'))
        {
            break;
        }

        // "> j.n.NoRouteToHostException                    1,173    (50%)" — decoupe sur les
        // espaces multiples : un libelle d'erreur ne contient que des espaces simples, il reste
        // donc entier. Meme parsing manuel que le reste de ce fichier, pas de Regex ajoutee.
        string[] parts = trimmed[1..].Split(
            "  ",
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 2 && long.TryParse(
                parts[1].Replace(",", ""),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long count))
        {
            errors.Add(new GatlingError(parts[0], count));
        }
    }

    return errors;
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
//
// Gatling ecrit "-" et non "0" dans une colonne vide — typiquement KO quand le tir n'a produit
// AUCUN echec. Le benchmark publie sature toujours la cible en 503, donc ce cas n'etait jamais
// atteint ; l'experience de saturation (cible qui met en file, 0 % d'echec) l'a fait planter pour
// de vrai. Bug reel trouve en executant, pas en relisant.
static double[] ParsePipeValues(string line)
{
    string[] parts = line.Split('|');
    return parts.Skip(1).Select(ParseGatlingCell).ToArray();
}

static double ParseGatlingCell(string cell)
{
    string trimmed = cell.Replace(",", "").Trim();
    return trimmed is "-" or ""
        ? 0d
        : double.Parse(trimmed, CultureInfo.InvariantCulture);
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
            latency.GetProperty("p99").GetDouble()),
        ScenarioFailCount: scenario.GetProperty("failCount").GetInt64());
}

// ---- Rendu Markdown ----

static string GenerateMarkdown(SiroccoResult sirocco, K6Result k6, GatlingResult gatling, NBomberResult nbomber)
{
    var sb = new StringBuilder();

    sb.AppendLine("# Résultats du benchmark comparatif");
    sb.AppendLine();
    sb.AppendLine("Généré par `benchmark/normalize` à partir des sorties réelles de");
    sb.AppendLine("`benchmark/results/{sirocco.json,k6.json,gatling/console.log,nbomber.json}`.");
    sb.AppendLine("Méthodologie complète, protocole exact et limites : voir [README](README.md).");
    sb.AppendLine();

    sb.AppendLine("## Vue d'ensemble — requêtes de checkout (le point de saturation)");
    sb.AppendLine();
    sb.AppendLine("| Outil | Requêtes | OK | Échecs | Taux d'échec |");
    sb.AppendLine("|---|---:|---:|---:|---:|");
    AppendCountsRow(sb, "Sirocco", sirocco.Checkout);
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
    AppendLatencyRowWithMetric(sb, "Sirocco", "Response (avec attente d'ordonnancement)", sirocco.IterationResponse);
    sb.AppendLine($"| Sirocco | Service (p99 seul, traitement pur) | — | — | {sirocco.IterationServiceP99Ms:F1} |");
    AppendLatencyRowWithMetric(sb, "k6", "iteration_duration", k6.IterationDuration);
    AppendLatencyRowWithMetric(sb, "Gatling", "Global Information (colonne Total)", gatling.GlobalLatency);
    sb.AppendLine("| NBomber | — (voir note ci-dessus) | — | — | — |");
    sb.AppendLine();

    sb.AppendLine("## Latence de l'étape checkout seule");
    sb.AppendLine();
    sb.AppendLine("Sirocco et NBomber exposent un percentile par étape nommée. k6 (sans tags/groupes");
    sb.AppendLine("par requête dans `benchmark/k6/checkout.js`) et Gatling (dont la console ne détaille");
    sb.AppendLine("les percentiles que globalement, pas par nom de requête) ne le permettent pas avec les");
    sb.AppendLine("artefacts capturés ici — limite réelle documentée plutôt que contournée.");
    sb.AppendLine();
    sb.AppendLine("| Outil | p50 (ms) | p95 (ms) | p99 (ms) |");
    sb.AppendLine("|---|---:|---:|---:|");
    AppendLatencyRow(sb, "Sirocco", sirocco.CheckoutResponse);
    sb.AppendLine("| k6 | — | — | — |");
    sb.AppendLine("| Gatling | — | — | — |");
    AppendLatencyRow(sb, "NBomber", nbomber.CheckoutLatency);
    sb.AppendLine();

    sb.AppendLine("## Le différenciateur Sirocco : Response vs Service, et la dette d'ordonnancement");
    sb.AppendLine();
    sb.AppendLine("Aucun des trois autres outils ne publie cette distinction. Sur l'itération complète :");
    sb.AppendLine();
    sb.AppendLine($"- **Response p99** (ce que l'appelant attend réellement, file d'attente incluse) : " +
                  $"{sirocco.IterationResponse.P99:F1} ms");
    sb.AppendLine($"- **Service p99** (temps de traitement pur, une fois la requête prise en charge) : " +
                  $"{sirocco.IterationServiceP99Ms:F1} ms");
    sb.AppendLine($"- **Dette d'ordonnancement maximale observée** : {sirocco.MaxSchedulingDelayMs:F1} ms");
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

// ---- Rendu de l'experience de saturation (mode --saturation) ----
//
// Trois sorties, une seule source : SATURATION.md reprend mot pour mot le fragment francais, et les
// deux fragments sont construits depuis le meme SaturationData. Les articles FR et EN n'ecrivent
// aucun chiffre, ils incluent ces fragments.

static string Ms(double value, CultureInfo culture) => value.ToString("N1", culture);

static string Num(long value, CultureInfo culture) => value.ToString("N0", culture);

static string Rate(double value, CultureInfo culture) => value.ToString("P1", culture);

static string GenerateSaturationReport(SaturationData data)
{
    StringBuilder sb = new();

    sb.AppendLine("# Résultats de l'expérience de saturation de l'injecteur");
    sb.AppendLine();
    sb.AppendLine("Généré par `benchmark/normalize --saturation` à partir des sorties réelles de");
    sb.AppendLine("`benchmark/results-saturation/`. Protocole : `benchmark/saturation.sh`.");
    sb.AppendLine("Commentaire complet : [l'article](../../docs/articles/dette-ordonnancement.md).");
    sb.AppendLine();
    sb.Append(GenerateFrenchFragment(data));

    return sb.ToString();
}

static string GenerateFrenchFragment(SaturationData data)
{
    CultureInfo c = CultureInfo.GetCultureInfo("fr-FR");
    SiroccoResult t = data.Sirocco;
    StringBuilder sb = new();

    sb.AppendLine("## Ce que chaque outil rapporte de ce tir");
    sb.AppendLine();
    sb.AppendLine($"Débit demandé : **{Num(data.PlannedRequests, c)} itérations** " +
                  $"(débit constant au-dessus de la capacité de la cible). La cible ne refuse jamais : " +
                  $"elle fait attendre.");
    sb.AppendLine();
    sb.AppendLine("| Outil | Modèle ouvert | Plafond VUs | Requêtes | Échecs | Latence rapportée (p99) | Attente d'ordonnancement |");
    sb.AppendLine("|---|---|---:|---:|---:|---:|---|");
    sb.AppendLine($"| **Sirocco** | borné | {Num(data.MaxVirtualUsers, c)} | {Num(t.Checkout.Total, c)} | " +
                  $"{Rate(t.Checkout.FailRate, c)} | **{Ms(t.IterationResponse.P99, c)} ms** | " +
                  $"**mesurée** : dette max {Ms(t.MaxSchedulingDelayMs, c)} ms |");
    sb.AppendLine($"| k6 | borné | {Num(data.MaxVirtualUsers, c)} | {Num(data.K6.Checkout.Total, c)} | " +
                  $"{Rate(data.K6.Checkout.FailRate, c)} | {Ms(data.K6.IterationDuration.P99, c)} ms | " +
                  $"non ; `dropped_iterations` = {Num(data.K6.DroppedIterations, c)} |");
    sb.AppendLine($"| Gatling | non borné | — | {Num(data.Gatling.Checkout.Total, c)} | " +
                  $"{Rate(data.Gatling.Checkout.FailRate, c)} | {Ms(data.Gatling.GlobalLatency.P99, c)} ms | " +
                  $"non (aucune file interne) |");
    sb.AppendLine($"| NBomber | non borné | — | {Num(data.NBomber.Checkout.Total, c)} | " +
                  $"{Rate(data.NBomber.Checkout.FailRate, c)} | {Ms(data.NBomber.CheckoutLatency.P99, c)} ms | " +
                  $"non (aucune file interne) |");
    sb.AppendLine();
    sb.AppendLine("La colonne latence n'est pas la même grandeur partout, et c'est documenté plutôt que");
    sb.AppendLine("lissé : `__iteration` Response pour Sirocco, `iteration_duration` pour k6, bloc");
    sb.AppendLine("`Global Information` pour Gatling — trois façons de dire « l'itération complète ». Pour");
    sb.AppendLine("NBomber, c'est l'étape `checkout` seule : il n'agrège pas par itération.");
    sb.AppendLine();

    sb.AppendLine("## Charge réellement délivrée");
    sb.AppendLine();
    sb.AppendLine("Même grandeur pour les quatre : les requêtes `checkout` abouties, sur les");
    sb.AppendLine($"{Num(data.PlannedRequests, c)} demandées.");
    sb.AppendLine();
    sb.AppendLine("| Outil | Délivrées | Manquantes | Ce que l'outil en dit |");
    sb.AppendLine("|---|---:|---:|---|");
    AppendDeliveryRow(sb, c, "**Sirocco**", data.PlannedRequests, t.Checkout.Ok,
        $"`droppedCount` = {Num(t.IterationDropped, c)} ; dette publiée séparément");
    AppendDeliveryRow(sb, c, "k6", data.PlannedRequests, data.K6.Checkout.Ok,
        $"`dropped_iterations` = {Num(data.K6.DroppedIterations, c)}");
    AppendDeliveryRow(sb, c, "Gatling", data.PlannedRequests, data.Gatling.Checkout.Ok,
        FormatGatlingErrors(data.Gatling, c, "aucune erreur signalée"));
    AppendDeliveryRow(sb, c, "NBomber", data.PlannedRequests, data.NBomber.Checkout.Ok,
        $"`failCount` = {Num(data.NBomber.ScenarioFailCount, c)} sur le scénario");
    sb.AppendLine();

    sb.AppendLine("## Le même tir, les deux mesures de Sirocco");
    sb.AppendLine();
    sb.AppendLine("| Mesure | p50 | p95 | p99 |");
    sb.AppendLine("|---|---:|---:|---:|");
    sb.AppendLine($"| **Service** — la requête chronométrée depuis son envoi | {Ms(t.IterationService.P50, c)} ms | " +
                  $"{Ms(t.IterationService.P95, c)} ms | {Ms(t.IterationService.P99, c)} ms |");
    sb.AppendLine($"| **Response** — depuis l'instant où elle *devait* partir | {Ms(t.IterationResponse.P50, c)} ms | " +
                  $"{Ms(t.IterationResponse.P95, c)} ms | **{Ms(t.IterationResponse.P99, c)} ms** |");
    sb.AppendLine();
    sb.AppendLine($"- Écart au p99 : **{Ms(t.OmissionP99Ms, c)} ms**");
    sb.AppendLine($"- Dette d'ordonnancement maximale : **{Ms(t.MaxSchedulingDelayMs, c)} ms**");
    sb.AppendLine($"- Itérations mesurées : {Num(t.Iteration.Total, c)} sur {Num(data.PlannedRequests, c)} demandées, " +
                  $"dont {Num(t.IterationDropped, c)} abandonnées");
    sb.AppendLine($"- Taux d'échec : {Rate(t.Iteration.FailRate, c)}");
    sb.AppendLine($"- Durée réelle du tir : {Ms(t.DurationSeconds, c)} s — l'injecteur a continué à vider son " +
                  $"retard après la fin du profil");
    sb.AppendLine();

    sb.AppendLine("## Où la dette se loge");
    sb.AppendLine();
    sb.AppendLine("| Étape | Mesures | Response p99 | Service p99 | Dette max |");
    sb.AppendLine("|---|---:|---:|---:|---:|");
    foreach (StepLatency step in t.AllSteps)
    {
        sb.AppendLine($"| `{step.Name}` | {Num(step.Count, c)} | {Ms(step.ResponseP99Ms, c)} ms | " +
                      $"{Ms(step.ServiceP99Ms, c)} ms | {Ms(step.MaxSchedulingDelayMs, c)} ms |");
    }

    sb.AppendLine();

    sb.AppendLine("## Témoin : le même profil contre une cible qui déleste");
    sb.AppendLine();
    sb.AppendLine("Sirocco seul, exactement les mêmes paramètres. Une seule variable change : la cible");
    sb.AppendLine("refuse au bout de 50 ms au lieu de faire attendre.");
    sb.AppendLine();
    sb.AppendLine("| Cible | Échecs | Service p99 | Response p99 | Écart p99 | Dette max |");
    sb.AppendLine("|---|---:|---:|---:|---:|---:|");
    AppendControlRow(sb, c, "Met en file", t);
    AppendControlRow(sb, c, "Déleste (503)", data.Control);
    sb.AppendLine();

    return sb.ToString();
}

static void AppendDeliveryRow(
    StringBuilder sb,
    CultureInfo c,
    string tool,
    long planned,
    long delivered,
    string signal) =>
    sb.AppendLine($"| {tool} | {Num(delivered, c)} | {Num(Math.Max(planned - delivered, 0L), c)} | {signal} |");

static string FormatGatlingErrors(GatlingResult gatling, CultureInfo c, string none) =>
    gatling.Errors.Count == 0
        ? none
        : string.Join(" ; ", gatling.Errors.Select(error => $"{Num(error.Count, c)} × `{error.Label}`"));

static void AppendControlRow(StringBuilder sb, CultureInfo c, string label, SiroccoResult result) =>
    sb.AppendLine($"| {label} | {Rate(result.Iteration.FailRate, c)} | {Ms(result.IterationService.P99, c)} ms | " +
                  $"{Ms(result.IterationResponse.P99, c)} ms | {Ms(result.OmissionP99Ms, c)} ms | " +
                  $"{Ms(result.MaxSchedulingDelayMs, c)} ms |");

static string GenerateEnglishFragment(SaturationData data)
{
    CultureInfo c = CultureInfo.GetCultureInfo("en-US");
    SiroccoResult t = data.Sirocco;
    StringBuilder sb = new();

    sb.AppendLine("## What each tool reports about this run");
    sb.AppendLine();
    sb.AppendLine($"Requested load: **{Num(data.PlannedRequests, c)} iterations** at a constant rate above " +
                  $"the target's capacity. The target never refuses a request: it makes it wait.");
    sb.AppendLine();
    sb.AppendLine("| Tool | Open model | VU ceiling | Requests | Failures | Reported latency (p99) | Scheduling wait |");
    sb.AppendLine("|---|---|---:|---:|---:|---:|---|");
    sb.AppendLine($"| **Sirocco** | bounded | {Num(data.MaxVirtualUsers, c)} | {Num(t.Checkout.Total, c)} | " +
                  $"{Rate(t.Checkout.FailRate, c)} | **{Ms(t.IterationResponse.P99, c)} ms** | " +
                  $"**measured**: max debt {Ms(t.MaxSchedulingDelayMs, c)} ms |");
    sb.AppendLine($"| k6 | bounded | {Num(data.MaxVirtualUsers, c)} | {Num(data.K6.Checkout.Total, c)} | " +
                  $"{Rate(data.K6.Checkout.FailRate, c)} | {Ms(data.K6.IterationDuration.P99, c)} ms | " +
                  $"no; `dropped_iterations` = {Num(data.K6.DroppedIterations, c)} |");
    sb.AppendLine($"| Gatling | unbounded | — | {Num(data.Gatling.Checkout.Total, c)} | " +
                  $"{Rate(data.Gatling.Checkout.FailRate, c)} | {Ms(data.Gatling.GlobalLatency.P99, c)} ms | " +
                  $"no (no internal queue) |");
    sb.AppendLine($"| NBomber | unbounded | — | {Num(data.NBomber.Checkout.Total, c)} | " +
                  $"{Rate(data.NBomber.Checkout.FailRate, c)} | {Ms(data.NBomber.CheckoutLatency.P99, c)} ms | " +
                  $"no (no internal queue) |");
    sb.AppendLine();
    sb.AppendLine("The latency column is not the same quantity everywhere, and that is documented rather");
    sb.AppendLine("than smoothed over: `__iteration` Response for Sirocco, `iteration_duration` for k6, the");
    sb.AppendLine("`Global Information` block for Gatling — three ways of saying \"the whole iteration\". For");
    sb.AppendLine("NBomber it is the `checkout` step alone: it does not aggregate per iteration.");
    sb.AppendLine();

    sb.AppendLine("## Load actually delivered");
    sb.AppendLine();
    sb.AppendLine("The same quantity for all four: successful `checkout` requests, out of the");
    sb.AppendLine($"{Num(data.PlannedRequests, c)} requested.");
    sb.AppendLine();
    sb.AppendLine("| Tool | Delivered | Missing | What the tool says about it |");
    sb.AppendLine("|---|---:|---:|---|");
    AppendDeliveryRow(sb, c, "**Sirocco**", data.PlannedRequests, t.Checkout.Ok,
        $"`droppedCount` = {Num(t.IterationDropped, c)}; debt published separately");
    AppendDeliveryRow(sb, c, "k6", data.PlannedRequests, data.K6.Checkout.Ok,
        $"`dropped_iterations` = {Num(data.K6.DroppedIterations, c)}");
    AppendDeliveryRow(sb, c, "Gatling", data.PlannedRequests, data.Gatling.Checkout.Ok,
        FormatGatlingErrors(data.Gatling, c, "no error reported"));
    AppendDeliveryRow(sb, c, "NBomber", data.PlannedRequests, data.NBomber.Checkout.Ok,
        $"`failCount` = {Num(data.NBomber.ScenarioFailCount, c)} on the scenario");
    sb.AppendLine();

    sb.AppendLine("## The same run, Sirocco's two measurements");
    sb.AppendLine();
    sb.AppendLine("| Measurement | p50 | p95 | p99 |");
    sb.AppendLine("|---|---:|---:|---:|");
    sb.AppendLine($"| **Service** — the request timed from the moment it was sent | {Ms(t.IterationService.P50, c)} ms | " +
                  $"{Ms(t.IterationService.P95, c)} ms | {Ms(t.IterationService.P99, c)} ms |");
    sb.AppendLine($"| **Response** — timed from when it *should* have been sent | {Ms(t.IterationResponse.P50, c)} ms | " +
                  $"{Ms(t.IterationResponse.P95, c)} ms | **{Ms(t.IterationResponse.P99, c)} ms** |");
    sb.AppendLine();
    sb.AppendLine($"- Gap at p99: **{Ms(t.OmissionP99Ms, c)} ms**");
    sb.AppendLine($"- Maximum scheduling debt: **{Ms(t.MaxSchedulingDelayMs, c)} ms**");
    sb.AppendLine($"- Iterations measured: {Num(t.Iteration.Total, c)} of {Num(data.PlannedRequests, c)} requested, " +
                  $"{Num(t.IterationDropped, c)} of them abandoned");
    sb.AppendLine($"- Failure rate: {Rate(t.Iteration.FailRate, c)}");
    sb.AppendLine($"- Actual run duration: {Ms(t.DurationSeconds, c)} s — the injector kept draining its backlog " +
                  $"after the profile ended");
    sb.AppendLine();

    sb.AppendLine("## Where the debt lands");
    sb.AppendLine();
    sb.AppendLine("| Step | Samples | Response p99 | Service p99 | Max debt |");
    sb.AppendLine("|---|---:|---:|---:|---:|");
    foreach (StepLatency step in t.AllSteps)
    {
        sb.AppendLine($"| `{step.Name}` | {Num(step.Count, c)} | {Ms(step.ResponseP99Ms, c)} ms | " +
                      $"{Ms(step.ServiceP99Ms, c)} ms | {Ms(step.MaxSchedulingDelayMs, c)} ms |");
    }

    sb.AppendLine();

    sb.AppendLine("## Control: the same profile against a target that sheds load");
    sb.AppendLine();
    sb.AppendLine("Sirocco alone, exactly the same parameters. One single variable changes: the target");
    sb.AppendLine("refuses after 50 ms instead of making the caller wait.");
    sb.AppendLine();
    sb.AppendLine("| Target | Failures | Service p99 | Response p99 | Gap at p99 | Max debt |");
    sb.AppendLine("|---|---:|---:|---:|---:|---:|");
    AppendControlRow(sb, c, "Queues", t);
    AppendControlRow(sb, c, "Sheds load (503)", data.Control);
    sb.AppendLine();

    return sb.ToString();
}

readonly record struct CheckoutCounts(long Total, long Ok, long Fail)
{
    public double FailRate => Total == 0L ? 0d : Fail / (double)Total;
}

readonly record struct LatencyMs(double P50, double P95, double P99);

readonly record struct StepLatency(
    string Name,
    long Count,
    double ResponseP99Ms,
    double ServiceP99Ms,
    double MaxSchedulingDelayMs);

readonly record struct SiroccoResult(
    CheckoutCounts Iteration,
    LatencyMs IterationResponse,
    double IterationServiceP99Ms,
    double MaxSchedulingDelayMs,
    CheckoutCounts Checkout,
    LatencyMs CheckoutResponse,
    LatencyMs IterationService,
    long IterationDropped,
    double OmissionP99Ms,
    double DurationSeconds,
    IReadOnlyList<StepLatency> AllSteps);

readonly record struct K6Result(
    CheckoutCounts Checkout,
    LatencyMs IterationDuration,
    long Iterations,
    long DroppedIterations);

/// <summary>
/// Tout ce que l'experience de saturation a mesure, parse une seule fois. Les trois rendus
/// (SATURATION.md, fragment FR, fragment EN) lisent cette meme structure : aucun chiffre n'est
/// recopie a la main, donc aucun ne peut diverger d'une langue a l'autre.
/// </summary>
readonly record struct SaturationData(
    SiroccoResult Sirocco,
    SiroccoResult Control,
    K6Result K6,
    GatlingResult Gatling,
    NBomberResult NBomber,
    long PlannedRequests,
    int MaxVirtualUsers);

readonly record struct GatlingError(string Label, long Count);

readonly record struct GatlingResult(
    CheckoutCounts Login,
    CheckoutCounts Checkout,
    CheckoutCounts Global,
    LatencyMs GlobalLatency,
    IReadOnlyList<GatlingError> Errors);

readonly record struct NBomberResult(
    CheckoutCounts Checkout,
    LatencyMs CheckoutLatency,
    long ScenarioFailCount);
