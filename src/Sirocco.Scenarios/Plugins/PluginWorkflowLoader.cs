using System.Reflection;
using System.Runtime.Loader;
using Sirocco.Domain.Execution;

namespace Sirocco.Scenarios.Plugins;

/// <summary>
/// Charge un <see cref="IWorkflow"/> depuis une assembly .NET compilee independamment de ce
/// depot — le contrat de plugin de la roadmap phase 6 : un protocole tiers s'ajoute sans toucher
/// au coeur, en s'appuyant seulement sur <see cref="IWorkflow"/>/<see cref="IVirtualUserContext"/>/
/// <c>StepScope</c>, deja publies dans le paquet NuGet <c>Sirocco.Domain</c> (roadmap phase 1).
/// <para>
/// Limite assumee de cette premiere version : aucune configuration n'est injectee dans le type
/// instancie (pas de section <c>appsettings.json</c>, pas d'options liees) — un plugin gere sa
/// propre configuration (variables d'environnement, fichier dedie...) exactement comme le ferait
/// un scenario scripte qui a besoin d'un reglage externe. Resolution NuGet (telecharger un paquet
/// par identifiant) hors scope : cette methode ne charge qu'un chemin de fichier deja present sur
/// le disque, voir ROADMAP.md.
/// </para>
/// <para>
/// SEC-7 (AUDIT.md) : chaque assembly de plugin est chargee dans son propre
/// <see cref="AssemblyLoadContext"/> collectible (<see cref="PluginLoadContext"/>), pas dans le
/// contexte par defaut via <c>Assembly.LoadFrom</c> — les dependances propres d'un plugin (une
/// version d'une bibliotheque tierce, par exemple) ne se melangent donc plus a celles de l'hote ni
/// de plugins charges precedemment. <c>Sirocco.Domain</c> reste explicitement exclue de cette
/// isolation : c'est le contrat partage (<see cref="IWorkflow"/> etc.), et le laisser resoudre
/// dans le contexte du plugin y chargerait une deuxieme copie du type, distincte de celle de
/// l'hote — le cast vers <see cref="IWorkflow"/> plus bas leverait alors
/// <see cref="InvalidCastException"/>. Aucun appel explicite a <c>Unload()</c> : le contexte est
/// collectible, le GC le reclame une fois l'instance de workflow et son type hors de portee — pas
/// de garantie de dechargement deterministe, mais un vrai progres sur le contexte par defaut, qui
/// ne se decharge jamais.
/// </para>
/// </summary>
public static class PluginWorkflowLoader
{
    private const string SHARED_CONTRACT_ASSEMBLY_NAME = "Sirocco.Domain";

    /// <summary>
    /// Contexte de chargement isole d'un plugin — un par appel a <see cref="Load"/>. Resout les
    /// dependances propres du plugin depuis son propre repertoire (<see cref="AssemblyDependencyResolver"/>,
    /// meme mecanisme qu'un <c>.deps.json</c> d'application), sauf <see cref="SHARED_CONTRACT_ASSEMBLY_NAME"/>
    /// (voir la remarque de classe ci-dessus).
    /// </summary>
    private sealed class PluginLoadContext(string pluginAssemblyPath)
        : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, SHARED_CONTRACT_ASSEMBLY_NAME, StringComparison.Ordinal))
            {
                return null;
            }

            string? resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return resolvedPath is null ? null : LoadFromAssemblyPath(resolvedPath);
        }
    }

    /// <summary>
    /// Charge l'assembly a <paramref name="assemblyPath"/> et instancie le type qui implemente
    /// <see cref="IWorkflow"/> — designe par <paramref name="typeName"/> (nom complet ou simple)
    /// si renseigne, sinon le seul candidat trouve si l'assembly n'en expose qu'un.
    /// </summary>
    /// <exception cref="FileNotFoundException"><paramref name="assemblyPath"/> n'existe pas.</exception>
    /// <exception cref="FormatException">
    /// L'assembly n'est pas une assembly .NET valide, aucun/plusieurs types implementant
    /// <see cref="IWorkflow"/> ne correspondent, ou le type resolu n'a pas de constructeur public
    /// sans parametre.
    /// </exception>
    public static IWorkflow Load(string assemblyPath, string? typeName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Assembly de plugin introuvable : '{assemblyPath}'.", assemblyPath);
        }

        PluginLoadContext context = new(assemblyPath);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
        {
            throw new FormatException($"Assembly de plugin invalide : '{assemblyPath}' : {ex.Message}", ex);
        }

        Type workflowType = ResolveWorkflowType(assembly, assemblyPath, typeName);

        ConstructorInfo? constructor = workflowType.GetConstructor(Type.EmptyTypes) ?? throw new FormatException(
                $"Le type de plugin '{workflowType.FullName}' doit exposer un constructeur public sans " +
                "parametre : Sirocco ne lui injecte aucune configuration dans cette premiere version, voir " +
                "la remarque de classe de PluginWorkflowLoader.");
        return (IWorkflow)constructor.Invoke(null);
    }

    /// <summary>
    /// Types publics, concrets, de <paramref name="assembly"/> qui implementent <see cref="IWorkflow"/>.
    /// <see cref="Assembly.GetTypes"/> peut echouer partiellement si une dependance de l'assembly ne
    /// se resout pas (<see cref="ReflectionTypeLoadException"/>) — un plugin qui reference un paquet
    /// absent au chargement reste un echec plausible, pas theorique, d'ou ce filtre explicite plutot
    /// que de laisser l'exception remonter brute.
    /// </summary>
    private static Type[] FindWorkflowTypes(Assembly assembly)
    {
        Type?[] loadedTypes;
        try
        {
            loadedTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            loadedTypes = ex.Types;
        }

        return [.. loadedTypes
            .Where(static type => type is { IsClass: true, IsAbstract: false, IsPublic: true })
            .Where(static type => typeof(IWorkflow).IsAssignableFrom(type))!];
    }

    private static Type ResolveWorkflowType(Assembly assembly, string assemblyPath, string? typeName)
    {
        Type[] candidates = FindWorkflowTypes(assembly);

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            Type? match = Array.Find(
                candidates,
                candidate => string.Equals(candidate.FullName, typeName, StringComparison.Ordinal)
                    || string.Equals(candidate.Name, typeName, StringComparison.Ordinal));

            return match ?? throw new FormatException(
                $"Aucun type '{typeName}' implementant IWorkflow trouve dans '{assemblyPath}'. " +
                $"Types disponibles : {DescribeCandidates(candidates)}.");
        }

        return candidates.Length switch
        {
            0 => throw new FormatException(
                $"Aucun type public implementant IWorkflow trouve dans '{assemblyPath}'."),
            1 => candidates[0],
            _ => throw new FormatException(
                $"Plusieurs types implementant IWorkflow trouves dans '{assemblyPath}' " +
                $"({DescribeCandidates(candidates)}) : precisez lequel via le nom de type."),
        };
    }

    private static string DescribeCandidates(Type[] candidates) =>
        candidates.Length == 0 ? "aucun" : string.Join(", ", candidates.Select(static candidate => candidate.FullName));
}