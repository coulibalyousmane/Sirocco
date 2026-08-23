using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sirocco.Compare;
using Sirocco.Domain.Metrics;

// Compare deux rapports de tir exportes en JSON (/report) : un tir de reference et le tir
// courant. Trois usages du meme calcul, pas trois outils :
//   1. table console — usage manuel ou dans un log CI ;
//   2. --html <fichier> — rapport comparatif ouvrable dans un navigateur ;
//   3. --max-regression-percent <p> — code de sortie 1 si une etape regresse au-dela de p % de
//      p95 par rapport a la reference, sans avoir a redefinir des seuils absolus a chaque tir.

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage : Sirocco.Compare <reference.json> <actuel.json> [--html <sortie.html>] [--max-regression-percent <p>]");
    return 1;
}

string baselinePath = args[0];
string currentPath = args[1];
string? htmlOutputPath = null;
double? maxRegressionPercent = null;

for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--html" when i + 1 < args.Length:
            htmlOutputPath = args[++i];
            break;

        case "--max-regression-percent" when i + 1 < args.Length:
            maxRegressionPercent = double.Parse(args[++i], CultureInfo.InvariantCulture) / 100d;
            break;

        default:
            Console.Error.WriteLine($"Option non reconnue ou incomplete : '{args[i]}'.");
            return 1;
    }
}

JsonSerializerOptions jsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
};
jsonOptions.Converters.Add(new JsonStringEnumConverter());

try
{
    LoadTestReport baseline = LoadReport(baselinePath, jsonOptions);
    LoadTestReport current = LoadReport(currentPath, jsonOptions);

    LoadTestReportComparison comparison = LoadTestReportComparison.Compare(baseline, current);

    Console.WriteLine(comparison.ToTable());

    if (htmlOutputPath is not null)
    {
        File.WriteAllText(htmlOutputPath, comparison.ToHtml());
        Console.WriteLine($"Rapport HTML ecrit : {htmlOutputPath}");
    }

    if (maxRegressionPercent is double limit)
    {
        double? worst = comparison.WorstP95RegressionPercent();

        if (worst is double regression && regression > limit)
        {
            Console.Error.WriteLine(
                $"Regression de p95 de {regression.ToString("+0.0%;-0.0%", CultureInfo.InvariantCulture)}, " +
                $"superieure a la limite de {limit.ToString("0.0%", CultureInfo.InvariantCulture)} : echec.");
            return 1;
        }

        string worstLabel = worst is double w ? w.ToString("+0.0%;-0.0%", CultureInfo.InvariantCulture) : "aucune etape comparable";
        Console.WriteLine($"Pire regression de p95 : {worstLabel} — sous la limite de {limit.ToString("0.0%", CultureInfo.InvariantCulture)}.");
    }

    return 0;
}
catch (Exception ex) when (ex is FileNotFoundException or JsonException or FormatException)
{
    Console.Error.WriteLine($"Erreur : {ex.Message}");
    return 1;
}

static LoadTestReport LoadReport(string path, JsonSerializerOptions options)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Fichier de rapport introuvable : '{path}'.", path);
    }

    string json = File.ReadAllText(path);
    LoadTestReportDto dto = JsonSerializer.Deserialize<LoadTestReportDto>(json, options)
        ?? throw new FormatException($"Rapport vide ou invalide dans '{path}'.");

    return dto.ToDomain();
}