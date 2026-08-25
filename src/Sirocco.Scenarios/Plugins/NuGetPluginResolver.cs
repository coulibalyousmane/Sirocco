using System.IO.Compression;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
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
/// Limite assumee : aucune resolution de dependances transitives du paquet — seule sa propre
/// bibliotheque est extraite. Un plugin qui depend d'un paquet tiers au-dela de
/// <c>Sirocco.Domain</c> doit le publier en assembly unique (fusionnee) ou accepter que
/// <see cref="PluginWorkflowLoader.Load"/> echoue au chargement de type si une reference ne se
/// resout pas — meme classe de limite que documentee sur <c>FindWorkflowTypes</c>.
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
                return await ExtractAssemblyAsync(
                    cachedNupkgPath, cachedDirectory, packageId, cachedVersion, allowUnsignedPlugins, cancellationToken)
                    .ConfigureAwait(false);
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

        return await ExtractAssemblyAsync(nupkgPath, packageDirectory, packageId, resolvedVersion, allowUnsignedPlugins, cancellationToken)
            .ConfigureAwait(false);
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

        FrameworkReducer reducer = new();
        NuGetFramework? bestMatch = reducer.GetNearest(_targetFramework, libItems.Select(static group => group.TargetFramework));
        FrameworkSpecificGroup? group = bestMatch is null
            ? null
            : libItems.FirstOrDefault(candidate => candidate.TargetFramework.Equals(bestMatch));

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
    /// SEC-7 (AUDIT.md) : rejette un paquet non signe (sauf <paramref name="allowUnsignedPlugins"/>)
    /// ou dont le contenu ne correspond plus a ce qui a ete signe. Voir la remarque de classe pour
    /// la portee exacte de cette verification — presence et integrite, pas confiance du certificat.
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