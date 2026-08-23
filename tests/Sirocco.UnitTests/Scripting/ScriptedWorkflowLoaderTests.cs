using Sirocco.Domain.Execution;
using Sirocco.Scenarios.Scripting;

namespace Sirocco.UnitTests.Scripting;

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
            new Sirocco.Scenarios.DynamicCheckoutWorkflow()
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

    /// <summary>
    /// Un script qui charge un jeu de donnees (roadmap phase 2) recoit forcement une ligne sous
    /// <c>IReadOnlyDictionary&lt;string,string&gt;</c> : sans <c>System.Collections.Generic</c> dans
    /// les imports par defaut, cette forme la plus commune d'un scenario scripte avec jeu de
    /// donnees ne compile pas — un vrai tir contre scenarios/scripted-checkout.csv l'a revele.
    /// </summary>
    [Fact]
    public async Task A_script_can_declare_a_generic_collection_without_an_explicit_using()
    {
        const string script = """
            public sealed class GenericCollectionWorkflow : IWorkflow
            {
                public string Name => "generic-collection";

                public void RegisterSteps(StepRegistry registry) { }

                public ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
                {
                    IReadOnlyDictionary<string, string> row = new Dictionary<string, string> { ["k"] = "v" };
                    return ValueTask.CompletedTask;
                }
            }

            new GenericCollectionWorkflow()
            """;

        IWorkflow workflow = await ScriptedWorkflowLoader.LoadFromSourceAsync(script);

        Assert.Equal("generic-collection", workflow.Name);
    }

    /// <summary>
    /// Bout en bout : un script charge un vrai fichier CSV via <c>DataSetLoader</c> (imports
    /// <c>Sirocco.Domain.Data</c>/<c>Sirocco.Scenarios.Data</c>), sans aucun <c>using</c>
    /// explicite — le chemin qu'un scenario scripte avec jeu de donnees emprunte reellement
    /// (voir scenarios/scripted-checkout.csx).
    /// </summary>
    [Fact]
    public async Task A_script_can_load_a_real_dataset_file_without_an_explicit_using()
    {
        string path = Path.GetTempFileName() + ".csv";
        File.WriteAllText(path, "value\na\nb\n");
        try
        {
            const string scriptTemplate = """
                public sealed class DatasetWorkflow : IWorkflow
                {
                    private readonly DataSet _rows = DataSetLoader.LoadFromFile(@"__PATH__", DataSetIterationStrategy.Circular);

                    public string Name => "dataset-count-" + _rows.Count;

                    public void RegisterSteps(StepRegistry registry) { }

                    public ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken) =>
                        ValueTask.CompletedTask;
                }

                new DatasetWorkflow()
                """;
            string script = scriptTemplate.Replace("__PATH__", path, StringComparison.Ordinal);

            IWorkflow workflow = await ScriptedWorkflowLoader.LoadFromSourceAsync(script);

            Assert.Equal("dataset-count-2", workflow.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}