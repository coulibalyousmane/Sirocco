using System.IO.Compression;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Sirocco.Scenarios.Plugins;

/// <summary>
/// Resout un plugin par identifiant de paquet NuGet plutot que par chemin de fichier deja
/// present sur le disque — deuxieme moitie du bullet "chargement dynamique/resolution NuGet" de
/// la roadmap phase 6, apres <see cref="PluginWorkflowLoader"/> (le contrat de plugin lui-meme).
/// <para>
/// Telecharge le <c>.nupkg</c> depuis <paramref name="sources"/> (une par une, dans l'ordre :
/// la premiere a repondre gagne) dans un cache local persistant entre les appels — un plugin
/// deja resolu une fois ne redeclenche aucun trafic reseau — puis extrait la bibliotheque du
/// groupe <c>lib/&lt;tfm&gt;</c> le plus proche de <c>net10.0</c> (<see cref="FrameworkReducer"/>,
/// meme algorithme que celui utilise par NuGet/MSBuild eux-memes).
/// </para>
/// <para>
/// Les dependances transitives du paquet sont resolues aussi (<see cref="RestoreDependenciesAsync"/>) :
/// le graphe declare par <c>GetPackageDependencies</c> est parcouru en largeur, chaque paquet
/// atteint est telecharge dans le meme cache local, et ses assemblies du groupe <c>lib/&lt;tfm&gt;</c>
/// le plus proche sont extraites **a plat, a cote de l'assembly du plugin** — c'est de la que
/// <see cref="PluginWorkflowLoader"/> les resout au chargement. Sans ce parcours, un plugin
/// distribue par paquet NuGet n'obtenait que sa propre <c>.dll</c> et echouait des qu'il touchait
/// une de ses dependances, ce qui limitait la distribution par paquet aux seules extensions sans
/// aucune dependance (ROADMAP.md, ligne "Ecosysteme d'extensions communautaire").
/// </para>
/// <para>
/// Trois limites assumees de ce parcours, enoncees plutot que devinees :
/// <list type="bullet">
/// <item><description>
/// Les paquets que l'hote fournit deja (<see cref="PluginWorkflowLoader.HostProvidedAssemblyNames"/>,
/// liste exacte et non un prefixe <c>Sirocco.*</c>) sont ignores : les telecharger reviendrait a
/// exiger qu'ils soient publies sur la source pour que le moindre plugin se resolve, et en charger
/// une copie privee dedoublerait les types du contrat partage. Une extension tierce nommee
/// <c>Sirocco.Extensions.Quelquechose</c> est en revanche restauree normalement.
/// </description></item>
/// <item><description>
/// Seuls les actifs <c>lib/&lt;tfm&gt;</c> sont extraits, jamais <c>runtimes/&lt;rid&gt;/native</c> :
/// une dependance a bibliotheque native (SQLite, par exemple) n'est donc pas servie par ce chemin,
/// et reste a distribuer via <c>dotnet publish</c> comme documente dans le guide d'extension.
/// </description></item>
/// <item><description>
/// L'arbitrage de version est volontairement simple : premiere occurrence gagnante dans le
/// parcours en largeur, et la version retenue est la plus basse qui satisfait l'intervalle declare
/// (<see cref="VersionRange.FindBestMatch"/>, la regle de NuGet pour une dependance directe). Un
/// vrai solveur ferait du "nearest wins" sur le graphe complet ; deux dependances exigeant des
/// versions incompatibles du meme paquet ne sont pas detectees comme un conflit.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// SEC-7 (AUDIT.md) : un paquet resolu (fraichement telecharge ou relu du cache local) doit etre
/// signe et son contenu doit correspondre exactement a ce qui a ete signe
/// (<see cref="EnsurePackageIsTrustedAsync"/>), sans quoi la resolution echoue — comportement par
/// defaut, ecartable via <paramref name="allowUnsignedPlugins"/> pour une source privee qui ne
/// signe pas ses paquets. Cette verification prouve la presence d'une signature et l'integrite du
/// contenu depuis cette signature (voir <see cref="ISignedPackageReader.ValidateIntegrityAsync"/>) ;
/// elle ne prouve pas que le certificat signataire est de confiance, valide ou non revoque —
/// <see cref="SignatureUtility"/> le documente explicitement sur ses methodes de chaine
/// ("does not perform revocation, trust, or certificate validity checking"). L'alternative testee
/// (verification de chaine via <c>SignedCms.CheckSignature</c>) rejette de vrais paquets nuget.org
/// legitimement signes des que leur certificat de signature a expire depuis — nuget.org
/// authentifie ce cas via un contre-sceau RFC3161, que cette classe ne rejoue pas. Un plugin
/// valablement signe par son propre auteur sous un nom typosquatte n'est donc pas detecte — c'est
/// le residu deja enonce par SEC-7 : "inherent a un systeme de plugins".
/// </para>
/// </summary>
public static class NuGetPluginResolver
{
    /// <summary>Source par defaut si <see cref="ResolveAssemblyPathAsync"/> n'en reçoit aucune.</summary>
    public const string DEFAULT_SOURCE = "https://api.nuget.org/v3/index.json";

    /// <summary>
    /// Temoin ecrit a cote de l'assembly du plugin une fois son graphe de dependances entierement
    /// restaure — sa presence court-circuite tout le parcours aux resolutions suivantes.
    /// </summary>
    private const string DEPENDENCIES_MARKER_FILE_NAME = ".sirocco-dependencies";

    private static readonly NuGetFramework _targetFramework = NuGetFramework.ParseFolder("net10.0");

    /// <summary>
    /// Resout <paramref name="packageId"/> (derniere version stable si <paramref name="version"/>
    /// est <see langword="null"/>) et retourne le chemin de la <c>.dll</c> a charger via
    /// <see cref="PluginWorkflowLoader.Load"/>.
    /// </summary>
    /// <param name="allowUnsignedPlugins">
    /// <see langword="false"/> par defaut : un paquet sans signature, ou dont le contenu ne
    /// correspond plus a ce qui a ete signe, est rejete (SEC-7, AUDIT.md). A reserver a une
    /// source privee qui ne signe pas ses paquets.
    /// </param>
    /// <exception cref="FormatException">
    /// Le paquet ou la version demandee n'existe dans aucune des sources, le telechargement
    /// echoue, le paquet ne contient aucune bibliotheque compatible avec <c>net10.0</c>, ou (sauf
    /// <paramref name="allowUnsignedPlugins"/>) le paquet n'est pas signe ou son contenu ne
    /// correspond plus a sa signature.
    /// </exception>
    public static async Task<string> ResolveAssemblyPathAsync(
        string packageId,
        string? version = null,
        IReadOnlyList<string>? sources = null,
        string? cacheDirectory = null,
        bool allowUnsignedPlugins = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        IReadOnlyList<string> effectiveSources = sources is { Count: > 0 } ? sources : [DEFAULT_SOURCE];
        string effectiveCacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "sirocco-plugin-cache");
        string normalizedId = packageId.ToLowerInvariant();

        // Une version explicite deja en cache ne redeclenche aucun trafic reseau : une version
        // publiee est immuable, contrairement a "derniere version stable" qui doit toujours
        // retourner a la source (elle peut changer entre deux appels).
        if (!string.IsNullOrWhiteSpace(version))
        {
            NuGetVersion cachedVersion = NuGetVersion.Parse(version);
            (string cachedDirectory, string cachedNupkgPath) = CachePaths(effectiveCacheDirectory, normalizedId, cachedVersion);
            if (File.Exists(cachedNupkgPath))
            {
                string cachedAssemblyPath = await ExtractAssemblyAsync(
                    cachedNupkgPath, cachedDirectory, packageId, cachedVersion, allowUnsignedPlugins, cancellationToken)
                    .ConfigureAwait(false);
                await EnsureDependenciesAsync(
                    cachedNupkgPath, cachedAssemblyPath, packageId, effectiveSources, effectiveCacheDirectory,
                    allowUnsignedPlugins, cancellationToken).ConfigureAwait(false);
                return cachedAssemblyPath;
            }
        }

        using SourceCacheContext cache = new();
        ILogger logger = NullLogger.Instance;

        (SourceRepository repository, NuGetVersion resolvedVersion) = await FindPackageAsync(
            packageId, version, effectiveSources, cache, logger, cancellationToken).ConfigureAwait(false);

        (string packageDirectory, string nupkgPath) = CachePaths(effectiveCacheDirectory, normalizedId, resolvedVersion);

        if (!File.Exists(nupkgPath))
        {
            Directory.CreateDirectory(packageDirectory);
            await DownloadAsync(repository, packageId, resolvedVersion, nupkgPath, cache, logger, cancellationToken)
                .ConfigureAwait(false);
        }

        string assemblyPath = await ExtractAssemblyAsync(
            nupkgPath, packageDirectory, packageId, resolvedVersion, allowUnsignedPlugins, cancellationToken)
            .ConfigureAwait(false);

        await EnsureDependenciesAsync(
            nupkgPath, assemblyPath, packageId, effectiveSources, effectiveCacheDirectory, allowUnsignedPlugins, cancellationToken)
            .ConfigureAwait(false);

        return assemblyPath;
    }

    /// <summary>Chemin du repertoire et du <c>.nupkg</c> mis en cache pour une version donnee.</summary>
    private static (string Directory, string NupkgPath) CachePaths(string cacheDirectory, string normalizedId, NuGetVersion version)
    {
        string directory = Path.Combine(cacheDirectory, normalizedId, version.ToNormalizedString());
        string nupkgPath = Path.Combine(directory, $"{normalizedId}.{version.ToNormalizedString()}.nupkg");
        return (directory, nupkgPath);
    }

    /// <summary>
    /// Cherche <paramref name="packageId"/> dans chaque source, dans l'ordre, et s'arrete a la
    /// premiere qui le connait : une version explicite doit y exister telle quelle, l'absence de
    /// version demande la plus recente version stable (les versions preliminaires ne sont jamais
    /// choisies implicitement).
    /// </summary>
    private static async Task<(SourceRepository Repository, NuGetVersion Version)> FindPackageAsync(
        string packageId,
        string? version,
        IReadOnlyList<string> sources,
        SourceCacheContext cache,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        NuGetVersion? requestedVersion = string.IsNullOrWhiteSpace(version) ? null : NuGetVersion.Parse(version);

        foreach (string source in sources)
        {
            SourceRepository repository = Repository.Factory.GetCoreV3(source);
            FindPackageByIdResource? resource = await repository
                .GetResourceAsync<FindPackageByIdResource>(cancellationToken)
                .ConfigureAwait(false);
            if (resource is null)
            {
                // Une source qui n'expose pas la ressource NuGet v3 attendue (protocole
                // incompatible, source mal configuree) : on passe a la suivante plutot que
                // d'echouer tout de suite, exactement comme pour un paquet absent de cette source.
                continue;
            }

            if (requestedVersion is not null)
            {
                bool exists = await resource
                    .DoesPackageExistAsync(packageId, requestedVersion, cache, logger, cancellationToken)
                    .ConfigureAwait(false);
                if (exists)
                {
                    return (repository, requestedVersion);
                }

                continue;
            }

            IEnumerable<NuGetVersion> versions = await resource
                .GetAllVersionsAsync(packageId, cache, logger, cancellationToken)
                .ConfigureAwait(false);
            NuGetVersion? latestStable = versions.Where(static candidate => !candidate.IsPrerelease).Max();
            if (latestStable is not null)
            {
                return (repository, latestStable);
            }
        }

        throw new FormatException(
            $"Paquet NuGet introuvable : '{packageId}'{(version is null ? string.Empty : $" version '{version}'")} " +
            $"dans {(sources.Count == 1 ? "la source" : "les sources")} {string.Join(", ", sources)}.");
    }

    private static async Task DownloadAsync(
        SourceRepository repository,
        string packageId,
        NuGetVersion version,
        string nupkgPath,
        SourceCacheContext cache,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        FindPackageByIdResource resource = await repository
            .GetResourceAsync<FindPackageByIdResource>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new FormatException(
                $"La source '{repository.PackageSource.Source}' n'expose pas la ressource NuGet attendue pour telecharger '{packageId}'.");

        bool success;
        await using (FileStream fileStream = File.Create(nupkgPath))
        {
            success = await resource
                .CopyNupkgToStreamAsync(packageId, version, fileStream, cache, logger, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!success)
        {
            File.Delete(nupkgPath);
            throw new FormatException(
                $"Echec du telechargement du paquet '{packageId}' {version} depuis '{repository.PackageSource.Source}'.");
        }
    }

    /// <summary>
    /// Choisit le groupe <c>lib/&lt;tfm&gt;</c> le plus proche de <c>net10.0</c>
    /// (<see cref="FrameworkReducer"/>) puis extrait ses fichiers dans le cache local — une seule
    /// fois par version, les appels suivants reutilisent l'extraction existante. Verifie d'abord
    /// la signature du paquet (<see cref="EnsurePackageIsTrustedAsync"/>) — a chaque appel, meme
    /// sur un <c>.nupkg</c> deja en cache localement, pas seulement au premier telechargement.
    /// </summary>
    private static async Task<string> ExtractAssemblyAsync(
        string nupkgPath,
        string packageDirectory,
        string packageId,
        NuGetVersion version,
        bool allowUnsignedPlugins,
        CancellationToken cancellationToken)
    {
        List<FrameworkSpecificGroup> libItems;
        using (PackageArchiveReader reader = new(nupkgPath))
        {
            await EnsurePackageIsTrustedAsync(reader, packageId, version, allowUnsignedPlugins, cancellationToken)
                .ConfigureAwait(false);
            libItems = [.. reader.GetLibItems()];
        }

        FrameworkSpecificGroup? group = SelectNearestLibGroup(libItems);

        if (group is null)
        {
            string available = libItems.Count == 0
                ? "aucun"
                : string.Join(", ", libItems.Select(static candidate => candidate.TargetFramework.GetShortFolderName()));
            throw new FormatException(
                $"Le paquet '{packageId}' {version} ne contient aucune bibliotheque compatible avec " +
                $"{_targetFramework.GetShortFolderName()}. Frameworks disponibles : {available}.");
        }

        string extractDirectory = Path.Combine(packageDirectory, "content");
        string? assemblyPath = null;

        using ZipArchive archive = ZipFile.OpenRead(nupkgPath);
        foreach (string entryPath in group.Items)
        {
            ZipArchiveEntry? entry = archive.GetEntry(entryPath);
            if (entry is null)
            {
                continue;
            }

            string destination = Path.Combine(extractDirectory, entryPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                entry.ExtractToFile(destination, overwrite: true);
            }

            if (assemblyPath is null && destination.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                assemblyPath = destination;
            }
        }

        return assemblyPath ?? throw new FormatException(
            $"Le paquet '{packageId}' {version} ne contient aucune assembly .dll dans le groupe " +
            $"'{group.TargetFramework.GetShortFolderName()}'.");
    }

    /// <summary>
    /// Groupe <c>lib/&lt;tfm&gt;</c> le plus proche de <c>net10.0</c> parmi <paramref name="libItems"/>,
    /// ou <see langword="null"/> si aucun n'est compatible — <see cref="FrameworkReducer"/>, le meme
    /// algorithme que NuGet/MSBuild.
    /// </summary>
    private static FrameworkSpecificGroup? SelectNearestLibGroup(List<FrameworkSpecificGroup> libItems)
    {
        FrameworkReducer reducer = new();
        NuGetFramework? bestMatch = reducer.GetNearest(_targetFramework, libItems.Select(static group => group.TargetFramework));
        return bestMatch is null
            ? null
            : libItems.FirstOrDefault(candidate => candidate.TargetFramework.Equals(bestMatch));
    }

    /// <summary>
    /// Restaure les dependances transitives du plugin une seule fois par version en cache : la
    /// presence de <see cref="DEPENDENCIES_MARKER_FILE_NAME"/> a cote de l'assembly du plugin
    /// signale un parcours deja mene a bien, et evite tout trafic reseau aux resolutions suivantes
    /// — meme raisonnement que le <c>.nupkg</c> deja telecharge, dont l'existence suffit a court-circuiter
    /// la source.
    /// </summary>
    private static async Task EnsureDependenciesAsync(
        string nupkgPath,
        string pluginAssemblyPath,
        string pluginPackageId,
        IReadOnlyList<string> sources,
        string cacheDirectory,
        bool allowUnsignedPlugins,
        CancellationToken cancellationToken)
    {
        string pluginDirectory = Path.GetDirectoryName(pluginAssemblyPath)!;
        string markerPath = Path.Combine(pluginDirectory, DEPENDENCIES_MARKER_FILE_NAME);
        if (File.Exists(markerPath))
        {
            return;
        }

        using SourceCacheContext cache = new();
        await RestoreDependenciesAsync(
            nupkgPath, pluginDirectory, pluginPackageId, sources, cacheDirectory, allowUnsignedPlugins,
            cache, NullLogger.Instance, cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            markerPath,
            $"Dependances transitives de '{pluginPackageId}' restaurees par Sirocco dans ce repertoire." + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parcourt en largeur le graphe de dependances declare par le paquet de plugin et extrait les
    /// assemblies de chaque paquet atteint a plat dans <paramref name="pluginDirectory"/>. Voir la
    /// remarque de classe pour les trois limites assumees (paquets <c>Sirocco.*</c> ignores, actifs
    /// natifs non servis, arbitrage de version simple).
    /// </summary>
    /// <exception cref="FormatException">
    /// Une dependance declaree n'existe dans aucune des sources. Un paquet trouve mais sans aucune
    /// assembly compatible n'est pas une erreur : c'est le cas normal d'un metapaquet ou d'un paquet
    /// d'analyseurs, qui n'a rien a contribuer au repertoire du plugin.
    /// </exception>
    private static async Task RestoreDependenciesAsync(
        string nupkgPath,
        string pluginDirectory,
        string pluginPackageId,
        IReadOnlyList<string> sources,
        string cacheDirectory,
        bool allowUnsignedPlugins,
        SourceCacheContext cache,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Queue<PackageDependency> pending = new(ReadDependencies(nupkgPath));
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            PackageDependency dependency = pending.Dequeue();

            if (PluginWorkflowLoader.HostProvidedAssemblyNames.Contains(dependency.Id)
                || !visited.Add(dependency.Id))
            {
                continue;
            }

            (SourceRepository repository, NuGetVersion version) = await FindDependencyAsync(
                dependency, pluginPackageId, sources, cache, logger, cancellationToken).ConfigureAwait(false);

            (string dependencyDirectory, string dependencyNupkgPath) =
                CachePaths(cacheDirectory, dependency.Id.ToLowerInvariant(), version);

            if (!File.Exists(dependencyNupkgPath))
            {
                Directory.CreateDirectory(dependencyDirectory);
                await DownloadAsync(repository, dependency.Id, version, dependencyNupkgPath, cache, logger, cancellationToken)
                    .ConfigureAwait(false);
            }

            await ExtractDependencyLibrariesAsync(
                dependencyNupkgPath, pluginDirectory, dependency.Id, version, allowUnsignedPlugins, cancellationToken)
                .ConfigureAwait(false);

            foreach (PackageDependency transitive in ReadDependencies(dependencyNupkgPath))
            {
                pending.Enqueue(transitive);
            }
        }
    }

    /// <summary>
    /// Dependances declarees par un <c>.nupkg</c> pour le groupe de framework le plus proche de
    /// <c>net10.0</c> — un paquet declare un groupe par framework cible, et seul celui qui nous
    /// concerne doit etre restaure.
    /// </summary>
    private static IReadOnlyList<PackageDependency> ReadDependencies(string nupkgPath)
    {
        using PackageArchiveReader reader = new(nupkgPath);
        List<PackageDependencyGroup> groups = [.. reader.GetPackageDependencies()];

        FrameworkReducer reducer = new();
        NuGetFramework? bestMatch = reducer.GetNearest(_targetFramework, groups.Select(static group => group.TargetFramework));
        PackageDependencyGroup? group = bestMatch is null
            ? null
            : groups.FirstOrDefault(candidate => candidate.TargetFramework.Equals(bestMatch));

        return group is null ? [] : [.. group.Packages];
    }

    /// <summary>
    /// Cherche une dependance dans chaque source, dans l'ordre, et retient la plus basse version qui
    /// satisfait son intervalle (<see cref="VersionRange.FindBestMatch"/> — la regle de NuGet pour
    /// une dependance directe, pas "la plus recente").
    /// </summary>
    private static async Task<(SourceRepository Repository, NuGetVersion Version)> FindDependencyAsync(
        PackageDependency dependency,
        string pluginPackageId,
        IReadOnlyList<string> sources,
        SourceCacheContext cache,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (string source in sources)
        {
            SourceRepository repository = Repository.Factory.GetCoreV3(source);
            FindPackageByIdResource? resource = await repository
                .GetResourceAsync<FindPackageByIdResource>(cancellationToken)
                .ConfigureAwait(false);
            if (resource is null)
            {
                continue;
            }

            IEnumerable<NuGetVersion> versions = await resource
                .GetAllVersionsAsync(dependency.Id, cache, logger, cancellationToken)
                .ConfigureAwait(false);
            NuGetVersion? match = dependency.VersionRange.FindBestMatch(versions);
            if (match is not null)
            {
                return (repository, match);
            }
        }

        throw new FormatException(
            $"Le paquet de plugin '{pluginPackageId}' declare la dependance '{dependency.Id}' " +
            $"{dependency.VersionRange.PrettyPrint()}, introuvable dans " +
            $"{(sources.Count == 1 ? "la source" : "les sources")} {string.Join(", ", sources)}.");
    }

    /// <summary>
    /// Extrait les <c>.dll</c> du groupe <c>lib/&lt;tfm&gt;</c> le plus proche d'un paquet de
    /// dependance, **a plat** dans <paramref name="pluginDirectory"/> : c'est cette colocalisation
    /// qui permet a <see cref="PluginWorkflowLoader"/> de les resoudre au chargement. Un paquet sans
    /// groupe compatible ne contribue rien, sans erreur (metapaquet, paquet d'analyseurs).
    /// </summary>
    private static async Task ExtractDependencyLibrariesAsync(
        string nupkgPath,
        string pluginDirectory,
        string packageId,
        NuGetVersion version,
        bool allowUnsignedPlugins,
        CancellationToken cancellationToken)
    {
        List<FrameworkSpecificGroup> libItems;
        using (PackageArchiveReader reader = new(nupkgPath))
        {
            await EnsurePackageIsTrustedAsync(reader, packageId, version, allowUnsignedPlugins, cancellationToken)
                .ConfigureAwait(false);
            libItems = [.. reader.GetLibItems()];
        }

        FrameworkSpecificGroup? group = SelectNearestLibGroup(libItems);
        if (group is null)
        {
            return;
        }

        Directory.CreateDirectory(pluginDirectory);
        using ZipArchive archive = ZipFile.OpenRead(nupkgPath);
        foreach (string entryPath in group.Items)
        {
            if (!entryPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ZipArchiveEntry? entry = archive.GetEntry(entryPath);
            if (entry is null)
            {
                continue;
            }

            // A plat, et jamais en ecrasant : deux paquets exposant une assembly de meme nom laissent
            // gagner la premiere rencontree dans le parcours en largeur (limite de classe).
            string destination = Path.Combine(pluginDirectory, Path.GetFileName(entryPath));
            if (!File.Exists(destination))
            {
                entry.ExtractToFile(destination, overwrite: false);
            }
        }
    }

    /// <summary>
    /// SEC-7 (AUDIT.md) : rejette un paquet non signe (sauf <paramref name="allowUnsignedPlugins"/>)
    /// ou dont le contenu ne correspond plus a ce qui a ete signe. Voir la remarque de classe pour
    /// la portee exacte de cette verification — presence et integrite, pas confiance du certificat.
    /// Appliquee aux paquets de dependance comme au paquet de plugin : une dependance est du code qui
    /// s'executera dans le meme processus.
    /// </summary>
    private static async Task EnsurePackageIsTrustedAsync(
        PackageArchiveReader reader, string packageId, NuGetVersion version, bool allowUnsignedPlugins, CancellationToken cancellationToken)
    {
        if (!await reader.IsSignedAsync(cancellationToken).ConfigureAwait(false))
        {
            if (allowUnsignedPlugins)
            {
                return;
            }

            throw new FormatException(
                $"Le paquet de plugin '{packageId}' {version} n'est pas signe. Sirocco refuse par defaut de " +
                "charger un paquet de plugin sans signature (SEC-7, AUDIT.md) ; passez allowUnsignedPlugins " +
                "(--plugin-allow-unsigned en CLI, Sirocco:AllowUnsignedPlugins en configuration) pour une " +
                "source privee qui ne signe pas ses paquets.");
        }

        PrimarySignature signature = await reader.GetPrimarySignatureAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new FormatException(
                $"Le paquet de plugin '{packageId}' {version} se declare signe mais n'expose aucune signature primaire lisible.");

        try
        {
            await reader.ValidateIntegrityAsync(signature.SignatureContent, cancellationToken).ConfigureAwait(false);
        }
        catch (SignatureException ex)
        {
            throw new FormatException(
                $"Le paquet de plugin '{packageId}' {version} a change depuis sa signature (SEC-7, AUDIT.md) : {ex.Message}", ex);
        }
    }
}