using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Tempest.Domain.Execution;

namespace Tempest.Scenarios.Scripting;

/// <summary>
/// Charge un <see cref="IWorkflow"/> depuis un script C# (<c>.csx</c> ou <c>.cs</c>), compile a
/// la volee via Roslyn.
/// <para>
/// Decision structurante de la roadmap phase 2 (voir ROADMAP.md) : un scenario scripte devient
/// un <see cref="IWorkflow"/> ordinaire, sans couche d'interop a maintenir — le script declare une
/// classe qui implemente l'interface, exactement comme <c>Tempest.Scenarios</c> le fait pour ses
/// propres scenarios de reference, puis se termine par une expression qui en instancie une.
/// </para>
/// <para>
/// Un script est du code C# a part entiere, execute avec la confiance totale du processus qui
/// l'evalue — au meme titre qu'un script k6 (JavaScript) ou NBomber (C# egalement). Rien n'est
/// sandboxe : c'est une propriete inherente au choix de la roadmap, pas un oubli.
/// </para>
/// </summary>
public static class ScriptedWorkflowLoader
{
    // Suffisant pour ecrire un scenario HTTP/WebSocket/gRPC sans "using" manuel : le script
    // garde la possibilite d'en ajouter d'autres lui-meme, ces imports par defaut ne retirent
    // rien, ils evitent seulement le boilerplate le plus frequent.
    private static readonly string[] _defaultImports =
    [
        "System",
        "System.Collections.Generic",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks",
        "Tempest.Domain.Data",
        "Tempest.Domain.Execution",
        "Tempest.Domain.Metrics",
        "Tempest.Scenarios.Data",
    ];

    private static readonly ScriptOptions _scriptOptions = BuildScriptOptions();

    /// <summary>
    /// Charge un <see cref="IWorkflow"/> depuis un fichier <c>.csx</c>/<c>.cs</c>.
    /// <para>
    /// Bloquant deliberement (<c>GetAwaiter().GetResult()</c>) : le chargement d'un scenario n'a
    /// jamais lieu sur le chemin critique, une seule fois avant le premier tir — exactement comme
    /// <see cref="Declarative.ScenarioDefinitionLoader.LoadFromFile"/>, dont cette methode reprend
    /// volontairement la meme signature synchrone pour que les deux formats s'utilisent de la
    /// meme facon a l'appel.
    /// </para>
    /// </summary>
    /// <exception cref="FileNotFoundException">Le fichier n'existe pas.</exception>
    /// <exception cref="NotSupportedException">Le processus courant est un publish self-contained fichier unique.</exception>
    /// <exception cref="FormatException">Le script ne compile pas ou n'evalue pas vers un <see cref="IWorkflow"/>.</exception>
    public static IWorkflow LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fichier de scenario introuvable : '{path}'.", path);
        }

        try
        {
            return LoadFromSourceAsync(File.ReadAllText(path)).GetAwaiter().GetResult();
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Scenario scripte invalide dans '{path}' : {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Compile et evalue un script C# dont la derniere expression doit produire un
    /// <see cref="IWorkflow"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">Le processus courant est un publish self-contained fichier unique.</exception>
    /// <exception cref="FormatException">Le script ne compile pas ou n'evalue pas vers un <see cref="IWorkflow"/>.</exception>
    public static async Task<IWorkflow> LoadFromSourceAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (IsSingleFileBundle())
        {
            throw new NotSupportedException(
                "Les scenarios scriptes ne sont pas pris en charge depuis un binaire autonome " +
                "publie en fichier unique (PublishSingleFile) : la resolution des references a " +
                "besoin du chemin sur disque des assemblies chargees, qui n'existe pas dans ce " +
                "mode. Utilisez 'dotnet tool install'/'dotnet run' (dependant du framework) pour " +
                "un scenario scripte, ou un binaire autonome multi-fichiers (sans PublishSingleFile) " +
                "pour un scenario integre/declaratif.");
        }

        IWorkflow? workflow;
        try
        {
            workflow = await CSharpScript.EvaluateAsync<IWorkflow>(source, _scriptOptions, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CompilationErrorException ex)
        {
            throw new FormatException($"Le script ne compile pas : {string.Join(" | ", ex.Diagnostics)}", ex);
        }

        return workflow ?? throw new FormatException(
            "Le script doit se terminer par une expression qui produit un IWorkflow, ex. 'new MonScenario()'.");
    }

    private static ScriptOptions BuildScriptOptions()
    {
        // Toutes les assemblies deja chargees dans le processus hote plutot qu'une liste ecrite
        // a la main : un script qui reference Tempest.Scenarios (pour DynamicCheckoutWorkflow en
        // exemple), Grpc.Net.Client ou System.Net.WebSockets doit les trouver sans configuration
        // supplementaire, et cette liste evolue avec les dependances du processus hote sans
        // jamais avoir a etre tenue a jour ici.
        //
        // IL3000 supprime deliberement : Assembly.Location est vide pour toute assembly d'un
        // publish self-contained fichier unique (elles vivent dans le bundle, pas sur disque).
        // C'est exactement pourquoi IsSingleFileBundle() existe : le cas est detecte et rejete
        // explicitement avant d'arriver ici, plutot que de laisser ce filtre produire silencieusement
        // une liste de references vide.
#pragma warning disable IL3000
        IEnumerable<Assembly> hostAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location));
#pragma warning restore IL3000

        return ScriptOptions.Default
            .WithReferences(hostAssemblies)
            .WithImports(_defaultImports);
    }

    /// <summary>
    /// Un scenario scripte a besoin de resoudre ses references depuis le chemin sur disque des
    /// assemblies chargees (voir <see cref="BuildScriptOptions"/>) — impossible depuis un
    /// publish self-contained fichier unique, ou <see cref="Assembly.Location"/> est toujours
    /// vide puisque les assemblies vivent dans le bundle, jamais sur disque. Detecte via
    /// <see cref="Assembly.GetEntryAssembly"/>, la seule assembly dont l'emplacement est garanti
    /// significatif quel que soit le mode de publication.
    /// </summary>
    private static bool IsSingleFileBundle() =>
#pragma warning disable IL3000
        string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);
#pragma warning restore IL3000
}