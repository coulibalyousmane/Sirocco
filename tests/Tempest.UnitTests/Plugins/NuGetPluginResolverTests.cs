using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NuGet.Packaging;
using NuGet.Versioning;
using Tempest.Domain.Execution;
using Tempest.Scenarios.Plugins;

namespace Tempest.UnitTests.Plugins;

/// <summary>
/// Verifie <see cref="NuGetPluginResolver"/> contre un vrai flux NuGet local — un dossier de
/// <c>.nupkg</c> reels, construits via <see cref="PackageBuilder"/> (la meme API que <c>nuget
/// pack</c>/<c>dotnet pack</c>), jamais un double du protocole NuGet lui-meme. Un dossier local
/// est un type de source NuGet a part entiere (celui d'un flux d'entreprise hors ligne), pas une
/// approximation de nuget.org.
/// </summary>
public sealed class NuGetPluginResolverTests
{
    [Fact]
    public async Task An_explicit_version_resolves_and_the_assembly_loads_as_a_real_workflow()
    {
        (string feed, string cache) = CreateDirectories();
        BuildLocalPackage(feed, "Tempest.Test.Plugin", "1.2.3", "resolved-explicit-version");

        string assemblyPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            "Tempest.Test.Plugin", "1.2.3", [feed], cache);

        Assert.True(File.Exists(assemblyPath));
        IWorkflow workflow = PluginWorkflowLoader.Load(assemblyPath);
        Assert.Equal("resolved-explicit-version", workflow.Name);
    }

    [Fact]
    public async Task No_version_resolves_the_latest_stable_version()
    {
        (string feed, string cache) = CreateDirectories();
        BuildLocalPackage(feed, "Tempest.Test.Plugin", "1.0.0", "old-version");
        BuildLocalPackage(feed, "Tempest.Test.Plugin", "2.0.0", "new-version");

        string assemblyPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            "Tempest.Test.Plugin", version: null, [feed], cache);

        IWorkflow workflow = PluginWorkflowLoader.Load(assemblyPath);
        Assert.Equal("new-version", workflow.Name);
    }

    [Fact]
    public async Task A_second_resolution_reuses_the_cache_without_contacting_the_feed_again()
    {
        (string feed, string cache) = CreateDirectories();
        BuildLocalPackage(feed, "Tempest.Test.Plugin", "1.0.0", "cached-version");

        string firstPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            "Tempest.Test.Plugin", "1.0.0", [feed], cache);

        // Le flux devient inutilisable : si la deuxieme resolution y retournait, elle echouerait.
        Directory.Delete(feed, recursive: true);

        string secondPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            "Tempest.Test.Plugin", "1.0.0", [feed], cache);

        Assert.Equal(firstPath, secondPath);
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task A_source_that_lacks_the_package_falls_through_to_the_next_one()
    {
        (string emptyFeed, string cache) = CreateDirectories();
        string populatedFeed = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tempest-nuget-feed-{Guid.NewGuid():N}")).FullName;
        BuildLocalPackage(populatedFeed, "Tempest.Test.Plugin", "1.0.0", "from-second-source");

        string assemblyPath = await NuGetPluginResolver.ResolveAssemblyPathAsync(
            "Tempest.Test.Plugin", "1.0.0", [emptyFeed, populatedFeed], cache);

        IWorkflow workflow = PluginWorkflowLoader.Load(assemblyPath);
        Assert.Equal("from-second-source", workflow.Name);
    }

    [Fact]
    public async Task An_unknown_package_across_all_sources_is_rejected()
    {
        (string feed, string cache) = CreateDirectories();

        FormatException ex = await Assert.ThrowsAsync<FormatException>(
            () => NuGetPluginResolver.ResolveAssemblyPathAsync("Does.Not.Exist", "1.0.0", [feed], cache));
        Assert.Contains("Does.Not.Exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_with_no_compatible_framework_is_rejected()
    {
        (string feed, string cache) = CreateDirectories();
        BuildLocalPackage(feed, "Tempest.Test.Plugin", "1.0.0", "unreachable", targetFrameworkFolder: "net40");

        FormatException ex = await Assert.ThrowsAsync<FormatException>(
            () => NuGetPluginResolver.ResolveAssemblyPathAsync("Tempest.Test.Plugin", "1.0.0", [feed], cache));
        Assert.Contains("net10.0", ex.Message, StringComparison.Ordinal);
    }

    private static (string Feed, string Cache) CreateDirectories()
    {
        string feed = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tempest-nuget-feed-{Guid.NewGuid():N}")).FullName;
        string cache = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tempest-nuget-cache-{Guid.NewGuid():N}")).FullName;
        return (feed, cache);
    }

    /// <summary>
    /// Compile un <see cref="IWorkflow"/> minimal nomme <paramref name="workflowName"/> via
    /// Roslyn puis l'empaquette dans un vrai <c>.nupkg</c> (<see cref="PackageBuilder"/>, meme API
    /// que <c>dotnet pack</c>) depose dans <paramref name="feedDirectory"/> — un dossier local est
    /// une source NuGet a part entiere pour <c>Repository.Factory.GetCoreV3</c>.
    /// </summary>
    private static void BuildLocalPackage(
        string feedDirectory, string packageId, string version, string workflowName, string targetFrameworkFolder = "net10.0")
    {
        string dllPath = CompileWorkflowAssembly(workflowName);

        PackageBuilder builder = new()
        {
            Id = packageId,
            Version = NuGetVersion.Parse(version),
            Description = "Paquet de test Tempest, jamais publie.",
        };
        builder.Authors.Add("Tempest.UnitTests");
        builder.Files.Add(new PhysicalPackageFile
        {
            SourcePath = dllPath,
            TargetPath = $"lib/{targetFrameworkFolder}/{Path.GetFileName(dllPath)}",
        });

        string nupkgPath = Path.Combine(feedDirectory, $"{packageId}.{version}.nupkg");
        using FileStream stream = File.Create(nupkgPath);
        builder.Save(stream);
    }

    private static string CompileWorkflowAssembly(string workflowName)
    {
        string source = $$"""
            public sealed class GeneratedWorkflow : Tempest.Domain.Execution.IWorkflow
            {
                public string Name => "{{workflowName}}";
                public void RegisterSteps(Tempest.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Tempest.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }
            """;

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);

        IEnumerable<MetadataReference> references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(static assembly => (MetadataReference)MetadataReference.CreateFromFile(assembly.Location));

        string assemblyName = $"TempestNuGetPluginTest_{Guid.NewGuid():N}";
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        string path = Path.Combine(Path.GetTempPath(), $"{assemblyName}.dll");
        using (FileStream output = File.Create(path))
        {
            EmitResult result = compilation.Emit(output);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Echec de compilation du plugin de test : {string.Join(" | ", result.Diagnostics)}");
            }
        }

        return path;
    }
}