using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using Sirocco.Domain.Execution;
using Sirocco.Scenarios.Plugins;

namespace Sirocco.UnitTests.Plugins;

/// <summary>
/// Verifie la resolution des dependances transitives d'un plugin distribue par paquet NuGet
/// (ROADMAP.md, ligne "Ecosysteme d'extensions communautaire") contre un vrai flux local — de vrais
/// <c>.nupkg</c> construits via <see cref="PackageBuilder"/>, avec de vrais groupes de dependances,
/// jamais un double du protocole NuGet.
/// <para>
/// Le plugin de ces tests ne se contente pas de *referencer* sa dependance : son
/// <see cref="IWorkflow.Name"/> lit une propriete de l'assembly dependante. Lire ce nom apres
/// chargement echouerait donc sur <see cref="FileNotFoundException"/> si la dependance n'avait pas
/// ete restauree a cote du plugin — c'est ce qui rend ces tests une preuve d'execution reelle, pas
/// un simple constat de presence de fichier.
/// </para>
/// <para>
/// Paquets deliberement non signes, donc <c>allowUnsignedPlugins: true</c> partout : la politique de
/// signature (SEC-7, AUDIT.md) a ses propres tests dans
/// <see cref="NuGetPluginResolverSignatureTests"/>. Elle s'applique aussi aux paquets de dependance,
/// ce qui est precisement pourquoi ce drapeau est necessaire ici.
/// </para>
/// </summary>
public sealed class NuGetPluginResolverDependencyTests
{
    private const string LIBRARY_ID = "Sirocco.Test.Library";
    private const string PLUGIN_ID = "Sirocco.Test.DependentPlugin";

    [Fact]
    public async Task A_direct_dependency_is_restored_and_really_executes()
    {
        (string feed, string cache) = NuGetPluginResolverTests.CreateDirectories();
        string libraryPath = BuildLibraryPackage(feed, LIBRARY_ID, "1.0.0", "SiroccoTestLibraryA", "depuis-la-dependance");
        BuildDependentPluginPackage(feed, PLUGIN_ID, "1.0.0", libraryPath, "SiroccoTestLibraryA", [(LIBRARY_ID, "1.0.0")]);

        string assemblyPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            PLUGIN_ID, "1.0.0", [feed], cache, allowUnsignedPlugins: true);

        IWorkflow workflow = PluginWorkflowLoader.Load(assemblyPath);

        // Lire Name execute du code de l'assembly dependante : sans restauration, FileNotFoundException.
        Assert.Equal("depuis-la-dependance", workflow.Name);
    }

    [Fact]
    public async Task A_dependency_of_a_dependency_is_restored_too()
    {
        (string feed, string cache) = NuGetPluginResolverTests.CreateDirectories();

        // Graphe reel sur trois niveaux : plugin -> intermediaire -> feuille, et le plugin n'utilise
        // que la feuille. Seul un parcours transitif la depose a cote du plugin.
        string leafPath = BuildLibraryPackage(feed, "Sirocco.Test.Leaf", "1.0.0", "SiroccoTestLeaf", "depuis-la-feuille");
        string intermediatePath = BuildLibraryPackage(feed, "Sirocco.Test.Intermediate", "1.0.0", "SiroccoTestIntermediate", "inutilisee");
        BuildPackage(feed, "Sirocco.Test.Intermediate", "1.0.0", intermediatePath, [("Sirocco.Test.Leaf", "1.0.0")]);
        BuildDependentPluginPackage(
            feed, PLUGIN_ID, "1.0.0", leafPath, "SiroccoTestLeaf", [("Sirocco.Test.Intermediate", "1.0.0")]);

        string assemblyPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            PLUGIN_ID, "1.0.0", [feed], cache, allowUnsignedPlugins: true);

        IWorkflow workflow = PluginWorkflowLoader.Load(assemblyPath);
        Assert.Equal("depuis-la-feuille", workflow.Name);
    }

    [Fact]
    public async Task The_shared_contract_package_is_never_fetched_from_the_source()
    {
        (string feed, string cache) = NuGetPluginResolverTests.CreateDirectories();

        // Sirocco.Domain n'est PAS dans le flux : tout plugin reel en depend, et l'exiger sur la
        // source rendrait la resolution impossible tant qu'il n'y est pas publie.
        string libraryPath = BuildLibraryPackage(feed, LIBRARY_ID, "1.0.0", "SiroccoTestLibraryB", "contrat-ignore");
        BuildDependentPluginPackage(
            feed, PLUGIN_ID, "1.0.0", libraryPath, "SiroccoTestLibraryB",
            [(LIBRARY_ID, "1.0.0"), ("Sirocco.Domain", "1.0.0")]);

        string assemblyPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            PLUGIN_ID, "1.0.0", [feed], cache, allowUnsignedPlugins: true);

        IWorkflow workflow = PluginWorkflowLoader.Load(assemblyPath);
        Assert.Equal("contrat-ignore", workflow.Name);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(assemblyPath)!, "Sirocco.Domain.dll")));
    }

    [Fact]
    public async Task A_dependency_missing_from_every_source_is_rejected_by_name()
    {
        (string feed, string cache) = NuGetPluginResolverTests.CreateDirectories();
        string libraryPath = BuildLibraryPackage(feed, LIBRARY_ID, "1.0.0", "SiroccoTestLibraryC", "jamais-atteint");
        BuildDependentPluginPackage(
            feed, PLUGIN_ID, "1.0.0", libraryPath, "SiroccoTestLibraryC", [("Absent.Package", "3.1.4")]);

        FormatException ex = await Assert.ThrowsAsync<FormatException>(
            () => NuGetPluginResolver.ResolveAssemblyPathAsync(PLUGIN_ID, "1.0.0", [feed], cache, allowUnsignedPlugins: true));

        Assert.Contains("Absent.Package", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PLUGIN_ID, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_lowest_version_satisfying_the_range_is_chosen()
    {
        (string feed, string cache) = NuGetPluginResolverTests.CreateDirectories();

        // Deux versions publiees, meme nom d'assembly, valeur differente : la regle NuGet pour une
        // dependance directe retient la plus BASSE qui satisfait l'intervalle, pas la plus recente.
        string oldLibrary = BuildLibraryPackage(feed, LIBRARY_ID, "1.0.0", "SiroccoTestLibraryD", "version-basse");
        BuildLibraryPackage(feed, LIBRARY_ID, "2.0.0", "SiroccoTestLibraryD", "version-haute");
        BuildDependentPluginPackage(
            feed, PLUGIN_ID, "1.0.0", oldLibrary, "SiroccoTestLibraryD", [(LIBRARY_ID, "1.0.0")]);

        string assemblyPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            PLUGIN_ID, "1.0.0", [feed], cache, allowUnsignedPlugins: true);

        IWorkflow workflow = PluginWorkflowLoader.Load(assemblyPath);
        Assert.Equal("version-basse", workflow.Name);
    }

    [Fact]
    public async Task A_second_resolution_restores_nothing_and_needs_no_source()
    {
        (string feed, string cache) = NuGetPluginResolverTests.CreateDirectories();
        string libraryPath = BuildLibraryPackage(feed, LIBRARY_ID, "1.0.0", "SiroccoTestLibraryE", "depuis-le-cache");
        BuildDependentPluginPackage(feed, PLUGIN_ID, "1.0.0", libraryPath, "SiroccoTestLibraryE", [(LIBRARY_ID, "1.0.0")]);

        string firstPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            PLUGIN_ID, "1.0.0", [feed], cache, allowUnsignedPlugins: true);

        // Le flux disparait : sans le temoin ecrit par la premiere resolution, le parcours de
        // dependances y retournerait et echouerait.
        Directory.Delete(feed, recursive: true);

        string secondPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            PLUGIN_ID, "1.0.0", [feed], cache, allowUnsignedPlugins: true);

        Assert.Equal(firstPath, secondPath);
        IWorkflow workflow = PluginWorkflowLoader.Load(secondPath);
        Assert.Equal("depuis-le-cache", workflow.Name);
    }

    [Fact]
    public void A_dependency_missing_next_to_the_assembly_gives_a_message_not_a_stack_trace()
    {
        // Cas rencontre pour de vrai en verifiant la resolution transitive : la dependance manque a
        // cote de l'assembly, et l'echec survient a la construction du type (initialiseur de champ).
        // Il remontait alors en TargetInvocationException non traduite — donc une trace de pile brute
        // et un code de sortie 127 cote CLI, au lieu d'un message.
        string libraryPath = Compile(
            """
            namespace SiroccoTestOrphanLibrary;

            public static class TestLibrary
            {
                public static string Greeting => "jamais-chargee";
            }
            """,
            "SiroccoTestOrphanLibrary",
            references: []);

        string pluginPath = Compile(
            """
            public sealed class GeneratedWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                // Initialiseur de champ : la dependance est touchee des la construction du type.
                private readonly string _name = SiroccoTestOrphanLibrary.TestLibrary.Greeting;

                public string Name => _name;
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }
            """,
            $"SiroccoOrphanPlugin_{Guid.NewGuid():N}",
            [libraryPath]);

        // La bibliotheque reste dans SON repertoire de compilation, jamais copiee a cote du plugin.
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(pluginPath)!, "SiroccoTestOrphanLibrary.dll")));

        FormatException ex = Assert.Throws<FormatException>(() => PluginWorkflowLoader.Load(pluginPath));

        Assert.Contains("SiroccoTestOrphanLibrary", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet publish", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compile une bibliotheque exposant <c>Greeting</c> et l'empaquette. Retourne le chemin de la
    /// <c>.dll</c> compilee, pour que le plugin puisse la referencer a la compilation.
    /// </summary>
    private static string BuildLibraryPackage(
        string feedDirectory, string packageId, string version, string assemblyName, string greeting)
    {
        string source = $$"""
            namespace {{assemblyName}};

            public static class TestLibrary
            {
                public static string Greeting => "{{greeting}}";
            }
            """;

        string dllPath = Compile(source, assemblyName, references: []);
        BuildPackage(feedDirectory, packageId, version, dllPath, dependencies: []);
        return dllPath;
    }

    /// <summary>
    /// Compile un <see cref="IWorkflow"/> dont le nom vient de <paramref name="libraryAssemblyName"/>
    /// — donc qui a reellement besoin de sa dependance a l'execution — et l'empaquette avec les
    /// groupes de dependances demandes.
    /// </summary>
    private static void BuildDependentPluginPackage(
        string feedDirectory,
        string packageId,
        string version,
        string libraryDllPath,
        string libraryAssemblyName,
        IReadOnlyList<(string Id, string Version)> dependencies)
    {
        string source = $$"""
            public sealed class GeneratedWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                public string Name => {{libraryAssemblyName}}.TestLibrary.Greeting;
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }
            """;

        string dllPath = Compile(source, $"SiroccoDependentPlugin_{Guid.NewGuid():N}", [libraryDllPath]);
        BuildPackage(feedDirectory, packageId, version, dllPath, dependencies);
    }

    /// <summary>
    /// Empaquette <paramref name="dllPath"/> dans un vrai <c>.nupkg</c> depose dans
    /// <paramref name="feedDirectory"/>, avec un groupe de dependances <c>net10.0</c> si
    /// <paramref name="dependencies"/> n'est pas vide.
    /// </summary>
    private static void BuildPackage(
        string feedDirectory,
        string packageId,
        string version,
        string dllPath,
        IReadOnlyList<(string Id, string Version)> dependencies)
    {
        PackageBuilder builder = new()
        {
            Id = packageId,
            Version = NuGetVersion.Parse(version),
            Description = "Paquet de test Sirocco, jamais publie.",
        };
        builder.Authors.Add("Sirocco.UnitTests");
        builder.Files.Add(new PhysicalPackageFile
        {
            SourcePath = dllPath,
            TargetPath = $"lib/net10.0/{Path.GetFileName(dllPath)}",
        });

        if (dependencies.Count > 0)
        {
            builder.DependencyGroups.Add(new PackageDependencyGroup(
                NuGetFramework.ParseFolder("net10.0"),
                [.. dependencies.Select(static dependency =>
                    new PackageDependency(dependency.Id, VersionRange.Parse(dependency.Version)))]));
        }

        string nupkgPath = Path.Combine(feedDirectory, $"{packageId}.{version}.nupkg");
        using FileStream stream = File.Create(nupkgPath);
        builder.Save(stream);
    }

    /// <summary>
    /// Compile <paramref name="source"/> en une <c>.dll</c> nommee exactement
    /// <paramref name="assemblyName"/> — le nom de fichier compte : c'est par lui que le contexte de
    /// chargement du plugin sonde son repertoire.
    /// </summary>
    private static string Compile(string source, string assemblyName, IReadOnlyList<string> references)
    {
        IEnumerable<MetadataReference> metadataReferences = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(static assembly => (MetadataReference)MetadataReference.CreateFromFile(assembly.Location))
            .Concat(references.Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)));

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            metadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        string directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"sirocco-plugin-build-{Guid.NewGuid():N}")).FullName;
        string path = Path.Combine(directory, $"{assemblyName}.dll");

        using (FileStream output = File.Create(path))
        {
            EmitResult result = compilation.Emit(output);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Echec de compilation de '{assemblyName}' : {string.Join(" | ", result.Diagnostics)}");
            }
        }

        return path;
    }
}