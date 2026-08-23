using System.Text.Json;
using Sirocco.HarConvert;

// Convertit un HAR (export "Enregistrer tout en HAR" des outils de developpement d'un
// navigateur) en scenario scripte C# (.csx), directement jouable via WorkflowFileLoader
// (--scenario/Sirocco:ScenarioFile) sans aucun cablage supplementaire.

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage : Sirocco.HarConvert <entree.har> <sortie.csx> [--name <nom>]");
    return 1;
}

string harPath = args[0];
string outputPath = args[1];
string workflowName = Path.GetFileNameWithoutExtension(outputPath);

for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--name" when i + 1 < args.Length:
            workflowName = args[++i];
            break;

        default:
            Console.Error.WriteLine($"Option non reconnue ou incomplete : '{args[i]}'.");
            return 1;
    }
}

try
{
    if (!File.Exists(harPath))
    {
        throw new FileNotFoundException($"Fichier HAR introuvable : '{harPath}'.", harPath);
    }

    JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    HarDocument document = JsonSerializer.Deserialize<HarDocument>(File.ReadAllText(harPath), jsonOptions)
        ?? throw new FormatException($"HAR vide ou invalide dans '{harPath}'.");

    HarConversionResult result = HarConverter.Convert(document.Log, workflowName);
    File.WriteAllText(outputPath, result.Code);

    Console.WriteLine(
        $"Scenario ecrit : {outputPath} ({result.StepCount} etape(s) retenue(s) sur {document.Log.Entries.Count} requete(s) du HAR).");

    if (result.SkippedStaticAssetCount > 0)
    {
        Console.WriteLine($"  {result.SkippedStaticAssetCount} requete(s) d'actif statique ignoree(s) (css/js/image/police).");
    }

    if (result.SkippedOtherHostCount > 0)
    {
        Console.WriteLine($"  {result.SkippedOtherHostCount} requete(s) vers un autre hote que '{result.BaseHost}' ignoree(s).");
    }

    Console.WriteLine(
        "Verifier l'authentification et les cookies avant de rejouer : ce sont des valeurs de session enregistrees, probablement expirees.");

    return 0;
}
catch (Exception ex) when (ex is FileNotFoundException or JsonException or FormatException)
{
    Console.Error.WriteLine($"Erreur : {ex.Message}");
    return 1;
}