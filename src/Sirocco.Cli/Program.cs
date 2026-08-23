using Sirocco.Cli;
using Sirocco.Domain.Metrics;
using Sirocco.Host;
using Sirocco.Host.Configuration;

// Une seule commande pour l'instant : "run". Pas de framework de sous-commandes en attendant
// d'en avoir une deuxieme — en ajouter une n'engagerait a rien de plus que ce que ce fichier fait
// deja a la main.
const string USAGE = """
    Usage : sirocco run [scenario.yaml] [options]

    Lance un tir de charge autonome et se termine a la fin, avec un code de sortie refletant le
    verdict des seuils (0 si tous respectes ou si aucun n'est configure, 1 sinon).

      --workflow <nom>          Scenario integre a utiliser en l'absence de fichier :
                                 dynamic-checkout (par defaut), websocket-echo, grpc-echo,
                                 grpc-stream-echo, grpc-client-stream-echo, grpc-bidi-stream-echo.
                                 Sans effet si un fichier de scenario est fourni.
      --plugin-type <nom>       Type IWorkflow a instancier si [scenario.yaml] designe une
                                 assembly compilee (.dll) — le contrat de plugin de la roadmap
                                 phase 6. Optionnel si l'assembly n'expose qu'un seul type
                                 implementant IWorkflow. Sans effet pour les autres formats.
      --plugin-package <id>     Identifiant d'un paquet NuGet contenant le plugin a charger, a la
                                 place de [scenario.yaml]. Sans effet si un fichier de scenario
                                 est fourni, qui garde la priorite.
      --plugin-package-version <v>  Version du paquet --plugin-package. Derniere version stable
                                 si omis.
      --plugin-source <url>     Source NuGet interrogee pour --plugin-package, repetable. nuget.org
                                 seul si omis.
      --target-url <url>        Adresse de base de la cible. Requis, sauf si deja fourni via
                                 Sirocco:TargetBaseUrl dans un appsettings.json du repertoire courant.
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
      --iterations-per-vu <k>    Iterations par utilisateur : avec --vus <n> (a la place de
                                 --duration), chacun des n utilisateurs virtuels execute
                                 exactement k iterations, independamment des autres, sans notion
                                 de debit ni de duree. Meme mise en garde de rapport que --vus.
      --iterations <n>           Iterations partagees : n iterations au total, disputees par au
                                 plus --max-vus utilisateurs virtuels (premier arrive, premier
                                 servi). Meme mise en garde de rapport que --vus. Mutuellement
                                 exclusif avec --vus/--vus-from/--vus-to/--rps/--from-rps/--to-rps/--duration.
      --max-rps <n>              Plafond de debit global, en requetes par seconde, applique par-
                                 dessus n'importe lequel des modeles ci-dessus (bridage) : le debit
                                 reellement transmis ne depasse jamais cette valeur, meme si le
                                 modele choisi en produirait davantage. Compatible avec tout le
                                 reste, y compris Sirocco:Scenarios. Le retard ainsi impose se
                                 mesure comme une dette d'ordonnancement.
      --threshold <regle>       Seuil de succes/echec, repetable :
                                 'etape:grandeur:comparaison:limite[:nom]', ex.
                                 '__iteration:ResponseP95Milliseconds:LessThan:200'.
      --report-html <fichier>   Ecrit le rapport final en HTML a la fin du tir.
      --report-json <fichier>   Ecrit le rapport final en JSON a la fin du tir (meme format que
                                 /report ; lisible par Sirocco.Compare).

    Sans --rps ni --from-rps/--to-rps, le profil de charge est lu depuis la section
    Sirocco:Profile d'un appsettings.json du repertoire courant, s'il existe — de meme pour les
    seuils (Sirocco:Thresholds) en l'absence de --threshold, pour les options avancees d'un
    workflow integre (sections WebSocketEcho, GrpcEcho, DynamicCheckout), et pour le plafond de
    debit global (Sirocco:MaxRequestsPerSecond) en l'absence de --max-rps.

    Scenarios concurrents : sans aucun des indicateurs ci-dessus, une section Sirocco:Scenarios
    (tableau) d'un appsettings.json du repertoire courant fait tourner plusieurs scenarios dans le
    meme tir, chacun avec son propre profil/modele de charge, ses etiquettes et ses seuils — meme
    convention que Sirocco:RampVus pour un profil multi-paliers, pas d'equivalent en ligne de
    commande. Limites : mode distribue non pris en charge, /report/live et /metrics non alimentes.

    Limites de cette premiere version : un seul processus autonome — pas de mode distribue
    (Master/Workers), qui reste l'affaire de Sirocco.Host. Le port d'ecoute suit les conventions
    ASP.NET Core habituelles (variable d'environnement ASPNETCORE_URLS).
    """;

// L'aide est reconnue n'importe ou dans la ligne de commande, pas seulement en premier argument :
// la documentation (README et docs/demarrer/cli.md) ecrit `sirocco run --help`, forme qui partait
// auparavant dans CliOptions.Parse et ressortait en "Option non reconnue". La commande de
// decouverte la plus evidente de l'outil echouait donc.
if (args.Length == 0 || args.Any(argument => argument is "-h" or "--help"))
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

string? targetUrl = options.TargetUrl ?? builder.Configuration["Sirocco:TargetBaseUrl"];
if (string.IsNullOrWhiteSpace(targetUrl))
{
    Console.Error.WriteLine(
        "--target-url est requis (ou une valeur Sirocco:TargetBaseUrl dans un appsettings.json du repertoire courant).");
    return 1;
}

// Plafond de debit global (bridage) : un overlay independant du modele choisi ci-dessous,
// jamais un cinquieme modele — s'applique donc identiquement dans toutes les branches.
double? maxRequestsPerSecond = options.MaxRequestsPerSecond
    ?? builder.Configuration.GetValue<double?>("Sirocco:MaxRequestsPerSecond");

SiroccoHostOptions siroccoOptions;
if (options.Vus is int vus)
{
    // Modele ferme : --vus fixe l'effectif exact, pas un plafond. Il lui faut une condition
    // d'arret, une duree ou un nombre d'iterations par utilisateur virtuel — jamais les deux
    // (deja verifie par CliOptions).
    if (options.Duration is null && options.IterationsPerVirtualUser is null)
    {
        Console.Error.WriteLine("--vus exige --duration ou --iterations-per-vu.");
        return 1;
    }

    siroccoOptions = new SiroccoHostOptions
    {
        TargetBaseUrl = targetUrl,
        MaxVirtualUsers = vus,
        ClosedModelDuration = options.Duration,
        IterationsPerVirtualUser = options.IterationsPerVirtualUser,
        MaxRequestsPerSecond = maxRequestsPerSecond,
        ScenarioFile = options.ScenarioPath ?? builder.Configuration["Sirocco:ScenarioFile"],
        Workflow = options.Workflow ?? builder.Configuration["Sirocco:Workflow"] ?? SiroccoHostOptions.DYNAMIC_CHECKOUT_WORKFLOW,
        PluginWorkflowType = options.PluginWorkflowType ?? builder.Configuration["Sirocco:PluginWorkflowType"],
        PluginPackageId = options.PluginPackageId ?? builder.Configuration["Sirocco:PluginPackageId"],
        PluginPackageVersion = options.PluginPackageVersion ?? builder.Configuration["Sirocco:PluginPackageVersion"],
        PluginPackageSources = options.PluginPackageSources.Count > 0 ? options.PluginPackageSources : ReadPluginPackageSources(builder.Configuration),
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
    // appsettings.json, section Sirocco:RampVus).
    if (options.Duration is not { } rampVusDuration)
    {
        Console.Error.WriteLine("--vus-from/--vus-to exigent --duration.");
        return 1;
    }

    siroccoOptions = new SiroccoHostOptions
    {
        TargetBaseUrl = targetUrl,
        RampVus = [new VirtualUserStageOptions { FromVus = vusFrom, ToVus = vusTo, DurationSeconds = rampVusDuration.TotalSeconds }],
        MaxRequestsPerSecond = maxRequestsPerSecond,
        ScenarioFile = options.ScenarioPath ?? builder.Configuration["Sirocco:ScenarioFile"],
        Workflow = options.Workflow ?? builder.Configuration["Sirocco:Workflow"] ?? SiroccoHostOptions.DYNAMIC_CHECKOUT_WORKFLOW,
        PluginWorkflowType = options.PluginWorkflowType ?? builder.Configuration["Sirocco:PluginWorkflowType"],
        PluginPackageId = options.PluginPackageId ?? builder.Configuration["Sirocco:PluginPackageId"],
        PluginPackageVersion = options.PluginPackageVersion ?? builder.Configuration["Sirocco:PluginPackageVersion"],
        PluginPackageSources = options.PluginPackageSources.Count > 0 ? options.PluginPackageSources : ReadPluginPackageSources(builder.Configuration),
        Thresholds = options.Thresholds.Count > 0 ? options.Thresholds : ReadThresholds(builder.Configuration),
        ExitAfterRun = true,
        ReportHtmlPath = options.ReportHtmlPath,
        ReportJsonPath = options.ReportJsonPath,
    };
}
else if (options.Iterations is long sharedIterations)
{
    // Iterations partagees : un total dispute par --max-vus utilisateurs virtuels au plus, pas
    // un effectif fixe — meme convention de plafond que le modele ouvert, pas de --duration.
    siroccoOptions = new SiroccoHostOptions
    {
        TargetBaseUrl = targetUrl,
        MaxVirtualUsers = options.MaxVirtualUsers
            ?? builder.Configuration.GetValue("Sirocco:MaxVirtualUsers", SiroccoHostOptions.DEFAULT_MAX_VIRTUAL_USERS),
        SharedIterations = sharedIterations,
        MaxRequestsPerSecond = maxRequestsPerSecond,
        ScenarioFile = options.ScenarioPath ?? builder.Configuration["Sirocco:ScenarioFile"],
        Workflow = options.Workflow ?? builder.Configuration["Sirocco:Workflow"] ?? SiroccoHostOptions.DYNAMIC_CHECKOUT_WORKFLOW,
        PluginWorkflowType = options.PluginWorkflowType ?? builder.Configuration["Sirocco:PluginWorkflowType"],
        PluginPackageId = options.PluginPackageId ?? builder.Configuration["Sirocco:PluginPackageId"],
        PluginPackageVersion = options.PluginPackageVersion ?? builder.Configuration["Sirocco:PluginPackageVersion"],
        PluginPackageSources = options.PluginPackageSources.Count > 0 ? options.PluginPackageSources : ReadPluginPackageSources(builder.Configuration),
        Thresholds = options.Thresholds.Count > 0 ? options.Thresholds : ReadThresholds(builder.Configuration),
        ExitAfterRun = true,
        ReportHtmlPath = options.ReportHtmlPath,
        ReportJsonPath = options.ReportJsonPath,
    };
}
else
{
    // Scenarios concurrents : comme le profil multi-paliers (Sirocco:Profile), reste l'affaire
    // d'un appsettings.json du repertoire courant plutot que d'une syntaxe --scenario a inventer
    // sur la ligne de commande — un tableau de scenarios n'a pas d'equivalent plat raisonnable.
    IReadOnlyList<ScenarioOptions> scenarios = ReadScenarios(builder.Configuration);
    if (scenarios.Count > 0)
    {
        siroccoOptions = new SiroccoHostOptions
        {
            TargetBaseUrl = targetUrl,
            Scenarios = scenarios,
            MaxRequestsPerSecond = maxRequestsPerSecond,
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
                "d'utilisateurs), --vus <n> --iterations-per-vu <k> (iterations par utilisateur), " +
                "--iterations <n> (iterations partagees), une section Sirocco:Profile, ou une section " +
                "Sirocco:Scenarios (scenarios concurrents) dans un appsettings.json du repertoire courant.");
            return 1;
        }

        siroccoOptions = new SiroccoHostOptions
        {
            TargetBaseUrl = targetUrl,
            MaxVirtualUsers = options.MaxVirtualUsers
                ?? builder.Configuration.GetValue("Sirocco:MaxVirtualUsers", SiroccoHostOptions.DEFAULT_MAX_VIRTUAL_USERS),
            Profile = profile,
            MaxRequestsPerSecond = maxRequestsPerSecond,
            ScenarioFile = options.ScenarioPath ?? builder.Configuration["Sirocco:ScenarioFile"],
            Workflow = options.Workflow ?? builder.Configuration["Sirocco:Workflow"] ?? SiroccoHostOptions.DYNAMIC_CHECKOUT_WORKFLOW,
            PluginWorkflowType = options.PluginWorkflowType ?? builder.Configuration["Sirocco:PluginWorkflowType"],
            PluginPackageId = options.PluginPackageId ?? builder.Configuration["Sirocco:PluginPackageId"],
            PluginPackageVersion = options.PluginPackageVersion ?? builder.Configuration["Sirocco:PluginPackageVersion"],
            PluginPackageSources = options.PluginPackageSources.Count > 0 ? options.PluginPackageSources : ReadPluginPackageSources(builder.Configuration),
            Thresholds = options.Thresholds.Count > 0 ? options.Thresholds : ReadThresholds(builder.Configuration),
            ExitAfterRun = true,
            ReportHtmlPath = options.ReportHtmlPath,
            ReportJsonPath = options.ReportJsonPath,
        };
    }
}

try
{
    StandaloneHost.Run(builder, siroccoOptions);
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

    return configuration.GetSection("Sirocco:Profile").Get<List<LoadStageOptions>>() ?? [];
}

static IReadOnlyList<ThresholdRule> ReadThresholds(IConfiguration configuration) =>
    configuration.GetSection("Sirocco:Thresholds").Get<List<ThresholdRule>>() ?? [];

static IReadOnlyList<string> ReadPluginPackageSources(IConfiguration configuration) =>
    configuration.GetSection("Sirocco:PluginPackageSources").Get<List<string>>() ?? [];

static IReadOnlyList<ScenarioOptions> ReadScenarios(IConfiguration configuration) =>
    configuration.GetSection("Sirocco:Scenarios").Get<List<ScenarioOptions>>() ?? [];