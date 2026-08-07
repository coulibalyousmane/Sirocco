using Tempest.Domain.Execution;
using Tempest.Scenarios.Scripting;

namespace Tempest.UnitTests.Scripting;

public sealed class ScriptedWorkflowLoaderTests
{
    private const string VALID_SCRIPT = """
        public sealed class MinimalScriptedWorkflow : IWorkflow
        {
            public string Name => "minimal-scripted";

            public void RegisterSteps(StepRegistry registry) { }

            public ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken) =>
                ValueTask.CompletedTask;
        }

        new MinimalScriptedWorkflow()
        """;

    [Fact]
    public async Task A_valid_script_evaluates_to_the_workflow_it_instantiates()
    {
        IWorkflow workflow = await ScriptedWorkflowLoader.LoadFromSourceAsync(VALID_SCRIPT);

        Assert.Equal("minimal-scripted", workflow.Name);
    }

    [Fact]
    public async Task A_script_can_reuse_an_existing_reference_workflow()
    {
        const string script = """
            new Tempest.Scenarios.DynamicCheckoutWorkflow()
            """;

        IWorkflow workflow = await ScriptedWorkflowLoader.LoadFromSourceAsync(script);

        Assert.Equal("dynamic-checkout", workflow.Name);
    }

    [Fact]
    public async Task A_syntax_error_is_reported_as_a_format_exception()
    {
        const string script = "this is not valid C#{{{";

        await Assert.ThrowsAsync<FormatException>(() => ScriptedWorkflowLoader.LoadFromSourceAsync(script));
    }

    [Fact]
    public async Task A_script_missing_its_trailing_instantiation_is_rejected()
    {
        // Une classe declaree mais jamais instanciee en derniere ligne : l'erreur la plus
        // probable en pratique (le "new MonScenario()" final oublie), pas une simple faute de
        // syntaxe.
        const string script = """
            public sealed class OrphanWorkflow : IWorkflow
            {
                public string Name => "orphan";

                public void RegisterSteps(StepRegistry registry) { }

                public ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken) =>
                    ValueTask.CompletedTask;
            }
            """;

        FormatException ex = await Assert.ThrowsAsync<FormatException>(
            () => ScriptedWorkflowLoader.LoadFromSourceAsync(script));
        Assert.Contains("IWorkflow", ex.Message);
    }

    [Fact]
    public void LoadFromFile_reads_and_compiles_a_real_file()
    {
        string path = Path.GetTempFileName() + ".csx";

        try
        {
            File.WriteAllText(path, VALID_SCRIPT);

            IWorkflow workflow = ScriptedWorkflowLoader.LoadFromFile(path);

            Assert.Equal("minimal-scripted", workflow.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromFile_wraps_compilation_errors_with_the_file_path()
    {
        string path = Path.GetTempFileName() + ".csx";

        try
        {
            File.WriteAllText(path, "this is not valid C#{{{");

            FormatException ex = Assert.Throws<FormatException>(() => ScriptedWorkflowLoader.LoadFromFile(path));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromFile_throws_when_the_file_does_not_exist() =>
        Assert.Throws<FileNotFoundException>(() => ScriptedWorkflowLoader.LoadFromFile("does-not-exist.csx"));
}