using Sirocco.Domain.Execution;
using Sirocco.Scenarios;
using Sirocco.Scenarios.Declarative;

namespace Sirocco.UnitTests.Scenarios;

public sealed class WorkflowFileLoaderTests
{
    private const string YAML_SCENARIO = """
        name: smoke-test
        steps:
          - name: browse
            method: GET
            path: /api/catalog/products
        """;

    private const string SCRIPT_SCENARIO = """
        public sealed class ScriptedProbe : IWorkflow
        {
            public string Name => "scripted-probe";

            public void RegisterSteps(StepRegistry registry) { }

            public ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken) =>
                ValueTask.CompletedTask;
        }

        new ScriptedProbe()
        """;

    [Theory]
    [InlineData(".yaml")]
    [InlineData(".yml")]
    [InlineData(".json")]
    public void A_declarative_extension_produces_a_DeclarativeWorkflow(string extension)
    {
        string path = Path.GetTempFileName() + extension;

        try
        {
            string content = extension == ".json"
                ? """{ "name": "smoke-test", "steps": [ { "name": "browse", "method": "GET", "path": "/api/catalog/products" } ] }"""
                : YAML_SCENARIO;
            File.WriteAllText(path, content);

            IWorkflow workflow = WorkflowFileLoader.LoadFromFile(path);

            Assert.IsType<DeclarativeWorkflow>(workflow);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".csx")]
    [InlineData(".cs")]
    public void A_script_extension_produces_the_workflow_the_script_instantiates(string extension)
    {
        string path = Path.GetTempFileName() + extension;

        try
        {
            File.WriteAllText(path, SCRIPT_SCENARIO);

            IWorkflow workflow = WorkflowFileLoader.LoadFromFile(path);

            Assert.Equal("scripted-probe", workflow.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_unrecognized_extension_is_rejected()
    {
        string path = Path.GetTempFileName() + ".txt";

        try
        {
            File.WriteAllText(path, YAML_SCENARIO);

            Assert.Throws<NotSupportedException>(() => WorkflowFileLoader.LoadFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}