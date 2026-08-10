using Tempest.Cli;
using Tempest.Domain.Metrics;
using Tempest.Host;
using Tempest.Host.Configuration;

// Une seule commande pour l'instant : "run". Pas de framework de sous-commandes en attendant
// d'en avoir une deuxieme — en ajouter une n'engagerait a rien de plus que ce que ce fichier fait
// deja a la main.
const string USAGE = """
    Usage : tempest run [scenario.yaml] [options]

    Lance un tir de charge autonome et se termine a la fin, avec un code de sortie refletant le
    verdict des seuils (0 si tous respectes ou si aucun n'est configure, 1 sinon).

      --workflow <nom>          Scenario integre a utiliser en l'absence de fichier :
                                 dynamic-checkout (par defaut), websocket-echo, grpc-echo,
                                 grpc-stream-echo, grpc-client-stream-echo, grpc-bidi-stream-echo.
                                 Sans effet si un fichier de scenario est fourni.
      --target-url <url>        Adresse de base de la cible. Requis, sauf si deja fourni via
                                 Tempest:TargetBaseUrl dans un appsettings.json du repertoire courant.
      --rps <n>                 Debit cible constant, en requetes par seconde (avec --duration).
                                 Modele ouvert, mutuellement exclusif avec --vus.
      --from-rps <n>            Debit cible au debut de la rampe (avec --to-rps et --duration).
      --to-rps <n>               Debit cible a la fin de la rampe (avec --from-rps et --duration).
      --duration <duree>        Duree du palier de charge : '30s', '5m', '1h', '500ms', ou un
                                 nombre de secondes.
      --max-vus <n>              Plafond d'utilisateurs virtuels concurrents en modele ouvert
                                 (par defaut 200). Mutuellement exclusif avec --vus.
      --vus <n>                  Modele ferme : exactement n utilisateurs virtuels enchainent les
                                 iterations sans pause pendant --duration (obligatoire), sans
                                 aucune notion de debit cible. Le rapport porte alors une mise en
                                 garde explicite : ces chiffres ne corrigent pas le coordinated
                                 omission et ne sont pas comparables a un tir en modele ouvert.
                                 Mutuellement exclusif avec --rps/--from-rps/--to-rps/--max-vus.
      --vus-from <n>             Montee (ou descente) d'utilisateurs : l'effectif concurrent
      --vus-to <n>                passe lineairement de --vus-from a --vus-to sur --duration
                                 (obligatoire). Meme mise en garde de rapport que --vus.
                                 Mutuellement exclusif avec --vus/--rps/--from-rps/--to-rps/--max-vus.
      --threshold <regle>       Seuil de succes/echec, repetable :
                                 'etape:grandeur:comparaison:limite[:nom]', ex.
                                 '__iteration:ResponseP95Milliseconds:LessThan:200'.
      --report-html <fichier>   Ecrit le rapport final en HTML a la fin du tir.
      --report-json <fichier>   Ecrit le rapport final en JSON a la fin du tir (meme format que
                                 /report ; lisible par Tempest.Compare).

    Sans --rps ni --from-rps/--to-rps, le profil de charge est lu depuis la section
    Tempest:Profile d'un appsettings.json du repertoire courant, s'il existe — de meme pour les
    seuils (Tempest:Thresholds) en l'absence de --threshold, et pour les options avancees d'un
    workflow integre (sections WebSocketEcho, GrpcEcho, DynamicCheckout).

    Limites de cette premiere version : un seul processus autonome — pas de mode distribue
    (Master/Workers), qui reste l'affaire de Tempest.Host. Le port d'ecoute suit les conventions
    ASP.NET Core habituelles (variable d'environnement ASPNETCORE_URLS).
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(USAGE);
    return 0;
}

if (args[0] != "run")
{
    Console.Error.WriteLine($"Commande inconnue : '{args[0]}'. Seule 'run' existe pour l'instant.");
    Console.Error.WriteLine();
    Console.Error.WriteLine(USAGE);
    return 1;
}

CliOptions options;
try
{
    options = CliOptions.Parse(args[1..]);
}
catch (FormatException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

WebApplicationBuilder builder = WebApplication.CreateBuilder([]);

string? targetUrl = options.TargetUrl ?? builder.Configuration["Tempest:TargetBaseUrl"];
if (string.IsNullOrWhiteSpace(targetUrl))
{
    Console.Error.WriteLine(
        "--target-url est requis (ou une valeur Tempest:TargetBaseUrl dans un appsettings.json du repertoire courant).");
    return 1;
}

TempestHostOptions tempestOptions;
if (options.Vus is int vus)
{
    // Modele ferme : --vus fixe l'effectif exact, pas un plafond, et --duration devient
    // obligatoire faute de profil de debit dont deriver une duree de tir.
    if (options.Duration is not { } closedModelDuration)
    {
        Console.Error.WriteLine("--vus exige --duration.");
        return 1;
    }

    tempestOptions = new TempestHostOptions
    {
        TargetBaseUrl = targetUrl,
        MaxVirtualUsers = vus,
        ClosedModelDuration = closedModelDuration,
        ScenarioFile = options.ScenarioPath ?? builder.Configuration["Tempest:ScenarioFile"],
        Workflow = options.Workflow ?? builder.Configuration["Tempest:Workflow"] ?? TempestHostOptions.DYNAMIC_CHECKOUT_WORKFLOW,
        Thresholds = options.Thresholds.Count > 0 ? options.Thresholds : ReadThresholds(builder.Configuration),
        ExitAfterRun = true,
        ReportHtmlPath = options.ReportHtmlPath,
        ReportJsonPath = options.ReportJsonPath,
    };
}
else if (options.VusFrom is int vusFrom && options.VusTo is int vusTo)
{
    // Montee d'utilisateurs : meme contrainte que --vus, --duration derive la duree du seul
    // palier que la CLI sait exprimer (un profil multi-paliers reste l'affaire d'un
    // appsettings.json, section Tempest:RampVus).
    if (options.Duration is not { } rampVusDuration)
    {
        Console.Error.WriteLine("--vus-from/--vus-to exigent --duration.");
        return 1;
    }

    tempestOptions = new TempestHostOptions
    {
        TargetBaseUrl = targetUrl,
        RampVus = [new VirtualUserStageOptions { FromVus = vusFrom, ToVus = vusTo, DurationSeconds = rampVusDuration.TotalSeconds }],
        ScenarioFile = options.ScenarioPath ?? builder.Configuration["Tempest:ScenarioFile"],
        Workflow = options.Workflow ?? builder.Configuration["Tempest:Workflow"] ?? TempestHostOptions.DYNAMIC_CHECKOUT_WORKFLOW,
        Thresholds = options.Thresholds.Count > 0 ? options.Thresholds : ReadThresholds(builder.Configuration),
        ExitAfterRun = true,
        ReportHtmlPath = options.ReportHtmlPath,
        ReportJsonPath = options.ReportJsonPath,
    };
}
else
{
    IReadOnlyList<LoadStageOptions> profile;
    try
    {
        profile = BuildProfile(options, builder.Configuration);
    }
    catch (FormatException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    if (profile.Count == 0)
    {
        Console.Error.WriteLine(
            "Profil de charge requis : --rps <n> --duration <d>, --from-rps <n> --to-rps <n> --duration <d>, " +
            "--vus <n> --duration <d> (modele ferme), --vus-from <n> --vus-to <n> --duration <d> (montee " +
            "d'utilisateurs), ou une section Tempest:Profile dans un appsettings.json du repertoire courant.");
        return 1;
    }

    tempestOptions = new TempestHostOptions
    {
        TargetBaseUrl = targetUrl,
        MaxVirtualUsers = options.MaxVirtualUsers
            ?? builder.Configuration.GetValue("Tempest:MaxVirtualUsers", TempestHostOptions.DEFAULT_MAX_VIRTUAL_USERS),
        Profile = profile,
        ScenarioFile = options.ScenarioPath ?? builder.Configuration["Tempest:ScenarioFile"],
        Workflow = options.Workflow ?? builder.Configuration["Tempest:Workflow"] ?? TempestHostOptions.DYNAMIC_CHECKOUT_WORKFLOW,
        Thresholds = options.Thresholds.Count > 0 ? options.Thresholds : ReadThresholds(builder.Configuration),
        ExitAfterRun = true,
        ReportHtmlPath = options.ReportHtmlPath,
        ReportJsonPath = options.ReportJsonPath,
    };
}

try
{
    StandaloneHost.Run(builder, tempestOptions);
}
catch (Exception ex) when (ex is FileNotFoundException or NotSupportedException or FormatException)
{
    // Recouvre les erreurs de chargement du scenario (fichier introuvable, extension non
    // reconnue, script scripte incompatible avec un publish self-contained fichier unique...) :
    // un message clair plutot que la trace d'une exception non geree.
    Console.Error.WriteLine(ex.Message);
    return 1;
}

return Environment.ExitCode;

static IReadOnlyList<LoadStageOptions> BuildProfile(CliOptions options, IConfiguration configuration)
{
    if (options.Rps is double constantRps)
    {
        if (options.Duration is not { } constantDuration)
        {
            throw new FormatException("--rps exige --duration.");
        }

        return [new LoadStageOptions { FromRps = constantRps, ToRps = constantRps, DurationSeconds = constantDuration.TotalSeconds }];
    }

    if (options.FromRps is double from && options.ToRps is double to)
    {
        if (options.Duration is not { } rampDuration)
        {
            throw new FormatException("--from-rps/--to-rps exigent --duration.");
        }

        return [new LoadStageOptions { FromRps = from, ToRps = to, DurationSeconds = rampDuration.TotalSeconds }];
    }

    return configuration.GetSection("Tempest:Profile").Get<List<LoadStageOptions>>() ?? [];
}

static IReadOnlyList<ThresholdRule> ReadThresholds(IConfiguration configuration) =>
    configuration.GetSection("Tempest:Thresholds").Get<List<ThresholdRule>>() ?? [];