using System.Text.Json;
using Tempest.PostmanConvert;

// Convertit une collection Postman (export v2.1) en scenario scripte C# (.csx), directement
// jouable via WorkflowFileLoader (--scenario/Tempest:ScenarioFile) sans aucun cablage
// supplementaire.

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage : Tempest.PostmanConvert <collection.json> <sortie.csx> [--name <nom>]");
    return 1;
}

string collectionPath = args[0];
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
    if (!File.Exists(collectionPath))
    {
        throw new FileNotFoundException($"Collection Postman introuvable : '{collectionPath}'.", collectionPath);
    }

    JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    PostmanCollection collection = JsonSerializer.Deserialize<PostmanCollection>(File.ReadAllText(collectionPath), jsonOptions)
        ?? throw new FormatException($"Collection Postman vide ou invalide dans '{collectionPath}'.");

    PostmanConversionResult result = PostmanConverter.Convert(collection, workflowName);
    File.WriteAllText(outputPath, result.Code);

    Console.WriteLine($"Scenario ecrit : {outputPath} ({result.StepCount} etape(s) generee(s)).");

    if (result.UnresolvedVariableCount > 0)
    {
        Console.WriteLine($"  {result.UnresolvedVariableCount} variable(s) {{{{...}}}} non resolue(s) remplacee(s) par un placeholder.");
    }

    if (result.SkippedFormDataBodyCount > 0)
    {
        Console.WriteLine($"  {result.SkippedFormDataBodyCount} requete(s) avec un corps \"formdata\" generee(s) sans corps.");
    }

    Console.WriteLine(
        "Verifier les placeholders et ajouter l'authentification necessaire avant de rejouer : "
        + "ni un environnement Postman ni de vraies donnees ne sont lus par ce convertisseur.");

    return 0;
}
catch (Exception ex) when (ex is FileNotFoundException or JsonException or FormatException)
{
    Console.Error.WriteLine($"Erreur : {ex.Message}");
    return 1;
}