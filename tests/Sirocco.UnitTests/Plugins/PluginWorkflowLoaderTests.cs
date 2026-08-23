using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Sirocco.Domain.Execution;
using Sirocco.Scenarios.Plugins;

namespace Sirocco.UnitTests.Plugins;

/// <summary>
/// Verifie <see cref="PluginWorkflowLoader"/> contre de vraies assemblies .NET compilees a la
/// volee (Roslyn, <c>Emit</c> vers un fichier temporaire) — pas des doublures : le contrat de
/// plugin n'a de sens que verifie contre une assembly reellement chargeable par
/// <see cref="System.Reflection.Assembly.LoadFrom"/>, exactement comme le ferait un plugin tiers.
/// <para>
/// Aucun fichier compile n'est supprime apres un <see cref="PluginWorkflowLoader.Load"/> reussi
/// (ou echoue apres chargement) : <c>Assembly.LoadFrom</c> verrouille le fichier sur Windows pour
/// toute la duree du processus, sans mecanisme de dechargement ici (un
/// <c>AssemblyLoadContext</c> collectible sortirait du scope du contrat de plugin de cette
/// premiere version) — les fichiers restent dans le repertoire temporaire, purges par le systeme.
/// </para>
/// </summary>
public sealed class PluginWorkflowLoaderTests
{
    [Fact]
    public void A_single_candidate_is_selected_without_a_type_name()
    {
        string path = CompileWorkflowAssembly("""
            public sealed class OnlyWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                public string Name => "only-workflow";
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }
            """);

        IWorkflow workflow = PluginWorkflowLoader.Load(path);

        Assert.Equal("only-workflow", workflow.Name);
    }

    [Fact]
    public void An_explicit_full_type_name_selects_among_several_candidates()
    {
        string path = CompileWorkflowAssembly("""
            public sealed class FirstWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                public string Name => "first";
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }

            public sealed class SecondWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                public string Name => "second";
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }
            """);

        IWorkflow workflow = PluginWorkflowLoader.Load(path, "SecondWorkflow");

        Assert.Equal("second", workflow.Name);
    }

    [Fact]
    public void An_internal_type_implementing_IWorkflow_is_not_a_candidate()
    {
        string path = CompileWorkflowAssembly("""
            internal sealed class HiddenWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                public string Name => "hidden";
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }

            public sealed class VisibleWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                public string Name => "visible";
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }
            """);

        IWorkflow workflow = PluginWorkflowLoader.Load(path);

        Assert.Equal("visible", workflow.Name);
    }

    [Fact]
    public void No_IWorkflow_type_is_rejected()
    {
        string path = CompileWorkflowAssembly("public sealed class NotAWorkflow { }");

        FormatException ex = Assert.Throws<FormatException>(() => PluginWorkflowLoader.Load(path));
        Assert.Contains("Aucun type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_candidates_without_a_type_name_are_rejected()
    {
        string path = CompileWorkflowAssembly("""
            public sealed class FirstWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                public string Name => "first";
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }

            public sealed class SecondWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                public string Name => "second";
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }
            """);

        FormatException ex = Assert.Throws<FormatException>(() => PluginWorkflowLoader.Load(path));
        Assert.Contains("Plusieurs types", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_type_name_is_rejected()
    {
        string path = CompileWorkflowAssembly("""
            public sealed class OnlyWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                public string Name => "only-workflow";
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }
            """);

        FormatException ex = Assert.Throws<FormatException>(() => PluginWorkflowLoader.Load(path, "DoesNotExist"));
        Assert.Contains("DoesNotExist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_without_a_public_parameterless_constructor_is_rejected()
    {
        string path = CompileWorkflowAssembly("""
            public sealed class RequiresArgumentWorkflow : Sirocco.Domain.Execution.IWorkflow
            {
                public RequiresArgumentWorkflow(string mandatory) { }
                public string Name => "requires-argument";
                public void RegisterSteps(Sirocco.Domain.Metrics.StepRegistry registry) { }
                public System.Threading.Tasks.ValueTask ExecuteAsync(
                    Sirocco.Domain.Execution.IVirtualUserContext context, System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.ValueTask.CompletedTask;
            }
            """);

        FormatException ex = Assert.Throws<FormatException>(() => PluginWorkflowLoader.Load(path));
        Assert.Contains("constructeur public sans", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_is_reported_as_such() =>
        Assert.Throws<FileNotFoundException>(() => PluginWorkflowLoader.Load("does-not-exist.dll"));

    [Fact]
    public void An_invalid_assembly_file_is_rejected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03]);

        Assert.Throws<FormatException>(() => PluginWorkflowLoader.Load(path));
    }

    /// <summary>
    /// Compile <paramref name="source"/> en une vraie assembly .NET, ecrite dans un fichier
    /// temporaire, referencant les memes assemblies que le processus de test — dont
    /// <c>Sirocco.Domain</c>, pour que le source puisse implementer <c>IWorkflow</c>. Meme
    /// technique de resolution des references que <c>ScriptedWorkflowLoader</c> (toutes les
    /// assemblies deja chargees dans le processus), reutilisee ici pour produire une vraie DLL
    /// plutot qu'evaluer un script.
    /// </summary>
    private static string CompileWorkflowAssembly(string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);

        IEnumerable<MetadataReference> references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(static assembly => (MetadataReference)MetadataReference.CreateFromFile(assembly.Location));

        string assemblyName = $"SiroccoPluginTest_{Guid.NewGuid():N}";
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