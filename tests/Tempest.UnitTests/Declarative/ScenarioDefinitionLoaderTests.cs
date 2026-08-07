using Tempest.Domain.Data;
using Tempest.Domain.Declarative;
using Tempest.Scenarios.Declarative;

namespace Tempest.UnitTests.Declarative;

public sealed class ScenarioDefinitionLoaderTests
{
    private const string YAML_SCENARIO = """
        name: smoke-test
        steps:
          - name: login
            method: POST
            path: /api/auth/login
            body: '{"username":"demo","password":"demo"}'
            contentType: application/json
            headers:
              X-Client: tempest
            extract:
              - variable: token
                regex: '"token":"([^"]+)"'
          - name: browse
            method: GET
            path: /api/catalog/products
            expectedStatusCodes: [200, 203]
        """;

    private const string JSON_SCENARIO = """
        {
          "name": "smoke-test",
          "steps": [
            {
              "name": "login",
              "method": "POST",
              "path": "/api/auth/login",
              "body": "{\"username\":\"demo\",\"password\":\"demo\"}",
              "contentType": "application/json",
              "headers": { "X-Client": "tempest" },
              "extract": [
                { "variable": "token", "regex": "\"token\":\"([^\"]+)\"" }
              ]
            },
            {
              "name": "browse",
              "method": "GET",
              "path": "/api/catalog/products",
              "expectedStatusCodes": [200, 203]
            }
          ]
        }
        """;

    private static void AssertParsedCorrectly(ScenarioDefinition scenario)
    {
        Assert.Equal("smoke-test", scenario.Name);
        Assert.Equal(2, scenario.Steps.Count);

        HttpStepDefinition login = scenario.Steps[0];
        Assert.Equal("login", login.Name);
        Assert.Equal("POST", login.Method);
        Assert.Equal("/api/auth/login", login.Path);
        Assert.Equal("""{"username":"demo","password":"demo"}""", login.Body);
        Assert.Equal("application/json", login.ContentType);
        Assert.Equal("tempest", login.Headers["X-Client"]);
        Assert.Empty(login.ExpectedStatusCodes);

        ExtractionRule extraction = Assert.Single(login.Extract);
        Assert.Equal("token", extraction.Variable);
        Assert.Equal("\"token\":\"([^\"]+)\"", extraction.Regex);
        Assert.Null(extraction.XPath);

        HttpStepDefinition browse = scenario.Steps[1];
        Assert.Equal("GET", browse.Method);
        Assert.Equal([200, 203], browse.ExpectedStatusCodes);
        Assert.Null(browse.Body);
    }

    [Fact]
    public void Yaml_and_json_produce_an_equivalent_definition_for_the_same_scenario()
    {
        AssertParsedCorrectly(ScenarioDefinitionLoader.Parse(YAML_SCENARIO, ScenarioFormat.Yaml));
        AssertParsedCorrectly(ScenarioDefinitionLoader.Parse(JSON_SCENARIO, ScenarioFormat.Json));
    }

    [Fact]
    public void LoadFromFile_infers_the_format_from_the_extension()
    {
        string yamlPath = Path.GetTempFileName() + ".yaml";
        string jsonPath = Path.GetTempFileName() + ".json";

        try
        {
            File.WriteAllText(yamlPath, YAML_SCENARIO);
            File.WriteAllText(jsonPath, JSON_SCENARIO);

            AssertParsedCorrectly(ScenarioDefinitionLoader.LoadFromFile(yamlPath));
            AssertParsedCorrectly(ScenarioDefinitionLoader.LoadFromFile(jsonPath));
        }
        finally
        {
            File.Delete(yamlPath);
            File.Delete(jsonPath);
        }
    }

    [Fact]
    public void An_unrecognized_extension_is_rejected()
    {
        string path = Path.GetTempFileName() + ".txt";
        File.WriteAllText(path, YAML_SCENARIO);

        try
        {
            Assert.Throws<NotSupportedException>(() => ScenarioDefinitionLoader.LoadFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_is_reported_clearly() =>
        Assert.Throws<FileNotFoundException>(() => ScenarioDefinitionLoader.LoadFromFile("does-not-exist.yaml"));

    [Fact]
    public void ReadRaw_returns_the_content_and_format_without_parsing_it()
    {
        string yamlPath = Path.GetTempFileName() + ".yaml";

        try
        {
            File.WriteAllText(yamlPath, YAML_SCENARIO);

            (string content, ScenarioFormat format) = ScenarioDefinitionLoader.ReadRaw(yamlPath);

            Assert.Equal(YAML_SCENARIO, content);
            Assert.Equal(ScenarioFormat.Yaml, format);
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    [Fact]
    public void ReadRaw_reports_a_missing_file_clearly() =>
        Assert.Throws<FileNotFoundException>(() => ScenarioDefinitionLoader.ReadRaw("does-not-exist.yaml"));

    [Fact]
    public void ReadRaw_rejects_an_unrecognized_extension()
    {
        string path = Path.GetTempFileName() + ".txt";
        File.WriteAllText(path, YAML_SCENARIO);

        try
        {
            Assert.Throws<NotSupportedException>(() => ScenarioDefinitionLoader.ReadRaw(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Malformed_yaml_is_wrapped_in_a_format_exception() =>
        Assert.Throws<FormatException>(() => ScenarioDefinitionLoader.Parse("not: [valid: yaml: at all", ScenarioFormat.Yaml));

    [Fact]
    public void Malformed_json_is_wrapped_in_a_format_exception() =>
        Assert.Throws<FormatException>(() => ScenarioDefinitionLoader.Parse("{not valid json", ScenarioFormat.Json));

    [Fact]
    public void A_scenario_missing_required_fields_fails_validation_after_parsing() =>
        Assert.Throws<ArgumentException>(() => ScenarioDefinitionLoader.Parse("name: incomplete\nsteps: []", ScenarioFormat.Yaml));

    [Fact]
    public void Parsing_rejects_blank_content()
    {
        Assert.Throws<ArgumentException>(() => ScenarioDefinitionLoader.Parse("", ScenarioFormat.Yaml));
        Assert.Throws<ArgumentException>(() => ScenarioDefinitionLoader.Parse("   ", ScenarioFormat.Json));
    }

    [Fact]
    public void A_jsonPath_extraction_rule_survives_the_yaml_and_json_dto_round_trip()
    {
        const string yaml = """
            name: jsonpath-scenario
            steps:
              - name: login
                method: POST
                path: /api/auth/login
                extract:
                  - variable: token
                    jsonPath: $.token
            """;

        const string json = """
            {
              "name": "jsonpath-scenario",
              "steps": [
                {
                  "name": "login",
                  "method": "POST",
                  "path": "/api/auth/login",
                  "extract": [
                    { "variable": "token", "jsonPath": "$.token" }
                  ]
                }
              ]
            }
            """;

        foreach ((string content, ScenarioFormat format) in new[] { (yaml, ScenarioFormat.Yaml), (json, ScenarioFormat.Json) })
        {
            ExtractionRule extraction = Assert.Single(ScenarioDefinitionLoader.Parse(content, format).Steps[0].Extract);
            Assert.Equal("$.token", extraction.JsonPath);
            Assert.Null(extraction.Regex);
            Assert.Null(extraction.XPath);
        }
    }

    [Fact]
    public void A_dataset_section_survives_the_yaml_and_json_dto_round_trip()
    {
        const string yaml = """
            name: dataset-scenario
            steps:
              - name: ping
                method: GET
                path: /api/ping
            datasets:
              - name: users
                path: users.csv
                strategy: uniquePerVirtualUser
            """;

        const string json = """
            {
              "name": "dataset-scenario",
              "steps": [{ "name": "ping", "method": "GET", "path": "/api/ping" }],
              "datasets": [{ "name": "users", "path": "users.csv", "strategy": "uniquePerVirtualUser" }]
            }
            """;

        foreach ((string content, ScenarioFormat format) in new[] { (yaml, ScenarioFormat.Yaml), (json, ScenarioFormat.Json) })
        {
            DataSetDefinition dataset = Assert.Single(ScenarioDefinitionLoader.Parse(content, format).Datasets);
            Assert.Equal("users", dataset.Name);
            Assert.Equal("users.csv", dataset.Path);
            Assert.Equal(DataSetIterationStrategy.UniquePerVirtualUser, dataset.Strategy);
        }
    }

    [Fact]
    public void A_dataset_without_a_strategy_defaults_to_circular()
    {
        const string yaml = """
            name: dataset-scenario
            steps:
              - name: ping
                method: GET
                path: /api/ping
            datasets:
              - name: users
                path: users.csv
            """;

        DataSetDefinition dataset = Assert.Single(ScenarioDefinitionLoader.Parse(yaml, ScenarioFormat.Yaml).Datasets);
        Assert.Equal(DataSetIterationStrategy.Circular, dataset.Strategy);
    }

    [Fact]
    public void An_unknown_dataset_strategy_is_wrapped_in_a_format_exception()
    {
        const string yaml = """
            name: dataset-scenario
            steps:
              - name: ping
                method: GET
                path: /api/ping
            datasets:
              - name: users
                path: users.csv
                strategy: not-a-real-strategy
            """;

        Assert.Throws<FormatException>(() => ScenarioDefinitionLoader.Parse(yaml, ScenarioFormat.Yaml));
    }
}