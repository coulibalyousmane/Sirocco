using System.Text.Json;
using Tempest.OpenApiConvert;

// Convertit une specification OpenAPI 3.x (JSON) en scenario scripte C# (.csx), directement
// jouable via WorkflowFileLoader (--scenario/Tempest:ScenarioFile) sans aucun cablage
// supplementaire.

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage : Tempest.OpenApiConvert <spec.json> <sortie.csx> [--name <nom>]");
    return 1;
}

string specPath = args[0];
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
    if (!File.Exists(specPath))
    {
        throw new FileNotFoundException($"Specification OpenAPI introuvable : '{specPath}'.", specPath);
    }

    JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    OpenApiDocument document = JsonSerializer.Deserialize<OpenApiDocument>(File.ReadAllText(specPath), jsonOptions)
        ?? throw new FormatException($"Specification OpenAPI vide ou invalide dans '{specPath}'.");

    OpenApiConversionResult result = OpenApiConverter.Convert(document, workflowName);
    File.WriteAllText(outputPath, result.Code);

    Console.WriteLine(
        $"Scenario ecrit : {outputPath} ({result.StepCount} etape(s) generee(s) sur {document.Paths.Count} chemin(s) de la specification).");

    if (result.SkippedOperationlessPathCount > 0)
    {
        Console.WriteLine($"  {result.SkippedOperationlessPathCount} chemin(s) sans methode HTTP prise en charge ignore(s).");
    }

    if (result.OperationsWithUnsupportedBodyCount > 0)
    {
        Console.WriteLine($"  {result.OperationsWithUnsupportedBodyCount} operation(s) avec un corps non-JSON generee(s) sans corps.");
    }

    Console.WriteLine(
        "Verifier les valeurs de parametres (placeholders) et ajouter l'authentification necessaire avant de rejouer : "
        + "la specification ne fournit ni vraies donnees ni jeton de session valide.");

    return 0;
}
catch (Exception ex) when (ex is FileNotFoundException or JsonException or FormatException)
{
    Console.Error.WriteLine($"Erreur : {ex.Message}");
    return 1;
}