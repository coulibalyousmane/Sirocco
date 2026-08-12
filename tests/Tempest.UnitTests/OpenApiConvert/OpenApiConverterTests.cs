using System.Text.Json.Nodes;
using Tempest.OpenApiConvert;

namespace Tempest.UnitTests.OpenApiConvert;

public sealed class OpenApiConverterTests
{
    private static OpenApiParameter Param(string name, string @in, bool required = false, OpenApiSchema? schema = null, JsonNode? example = null) =>
        new() { Name = name, In = @in, Required = required, Schema = schema, Example = example };

    private static OpenApiSchema Schema(string? type = null, string? @ref = null, Dictionary<string, OpenApiSchema>? properties = null, OpenApiSchema? items = null, JsonNode? example = null) =>
        new() { Type = type, Ref = @ref, Properties = properties ?? [], Items = items, Example = example };

    private static OpenApiOperation Op(string? operationId = null, List<OpenApiParameter>? parameters = null, OpenApiRequestBody? requestBody = null) =>
        new() { OperationId = operationId, Parameters = parameters ?? [], RequestBody = requestBody };

    private static OpenApiRequestBody JsonBody(OpenApiSchema? schema = null, JsonNode? example = null) =>
        new() { Content = { ["application/json"] = new OpenApiMediaType { Schema = schema, Example = example } } };

    [Fact]
    public void A_null_document_or_blank_name_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => OpenApiConverter.Convert(null!, "scenario"));
        Assert.Throws<ArgumentException>(() => OpenApiConverter.Convert(new OpenApiDocument(), " "));
    }

    [Fact]
    public void A_simple_get_operation_produces_a_workflow_that_registers_and_executes_the_step()
    {
        OpenApiDocument document = new() { Paths = { ["/products"] = new OpenApiPathItem { Get = Op("listProducts") } } };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "my-scenario");

        Assert.Equal(1, result.StepCount);
        Assert.Contains(": IWorkflow", result.Code);
        Assert.Contains("registry.Register(\"listProducts\")", result.Code);
        Assert.Contains("new HttpMethod(\"GET\")", result.Code);
        Assert.Contains("\"/products\"", result.Code);
        Assert.Contains("context.HttpClient.SendAsync(request0, cancellationToken)", result.Code);
        Assert.Contains("scope0.CompleteHttp((int)response0.StatusCode)", result.Code);
        Assert.EndsWith(Environment.NewLine, result.Code);
    }

    [Fact]
    public void The_script_ends_with_an_expression_instantiating_the_generated_class()
    {
        OpenApiDocument document = new() { Paths = { ["/x"] = new OpenApiPathItem { Get = Op("x") } } };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "checkout");

        Assert.Matches(@"new [A-Za-z_][A-Za-z0-9_]*\(\)\s*$", result.Code.TrimEnd());
    }

    [Fact]
    public void Path_parameters_are_substituted_with_a_type_based_placeholder_value()
    {
        OpenApiDocument document = new()
        {
            Paths =
            {
                ["/products/{id}"] = new OpenApiPathItem
                {
                    Get = Op("getProduct", [Param("id", "path", required: true, schema: Schema("integer"))]),
                },
            },
        };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "scenario");

        Assert.Contains("\"/products/0\"", result.Code);
    }

    [Fact]
    public void Only_required_query_parameters_are_appended_to_the_path()
    {
        OpenApiDocument document = new()
        {
            Paths =
            {
                ["/products"] = new OpenApiPathItem
                {
                    Get = Op(
                        "listProducts",
                        [
                            Param("page", "query", required: true, schema: Schema("integer")),
                            Param("sort", "query", required: false, schema: Schema("string")),
                        ]),
                },
            },
        };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "scenario");

        Assert.Contains("\"/products?page=0\"", result.Code);
        Assert.DoesNotContain("sort=", result.Code);
    }

    [Fact]
    public void A_ref_schema_is_resolved_against_components_and_rendered_as_json_body()
    {
        OpenApiDocument document = new()
        {
            Paths =
            {
                ["/checkout"] = new OpenApiPathItem
                {
                    Post = Op("checkout", requestBody: JsonBody(Schema(@ref: "#/components/schemas/CheckoutRequest"))),
                },
            },
            Components = new OpenApiComponents
            {
                Schemas =
                {
                    ["CheckoutRequest"] = Schema(
                        "object",
                        properties: new Dictionary<string, OpenApiSchema> { ["orderId"] = Schema("string"), ["quantity"] = Schema("integer") }),
                },
            },
        };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "scenario");

        Assert.Contains("orderId", result.Code);
        Assert.Contains("chaine", result.Code);
        Assert.Contains("quantity", result.Code);
        Assert.Contains("application/json", result.Code);
    }

    [Fact]
    public void A_cyclic_schema_reference_does_not_throw_and_produces_an_empty_object_rather_than_looping()
    {
        OpenApiDocument document = new()
        {
            Paths = { ["/nodes"] = new OpenApiPathItem { Post = Op("createNode", requestBody: JsonBody(Schema(@ref: "#/components/schemas/Node"))) } },
            Components = new OpenApiComponents
            {
                Schemas =
                {
                    ["Node"] = Schema("object", properties: new Dictionary<string, OpenApiSchema> { ["child"] = Schema(@ref: "#/components/schemas/Node") }),
                },
            },
        };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "scenario");

        Assert.Equal(1, result.StepCount);
        Assert.Contains("child", result.Code);
    }

    [Fact]
    public void An_operation_without_a_json_media_type_is_generated_without_a_body_and_counted()
    {
        OpenApiDocument document = new()
        {
            Paths =
            {
                ["/upload"] = new OpenApiPathItem
                {
                    Post = Op("upload", requestBody: new OpenApiRequestBody { Content = { ["multipart/form-data"] = new OpenApiMediaType() } }),
                },
            },
        };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "scenario");

        Assert.Equal(1, result.StepCount);
        Assert.Equal(1, result.OperationsWithUnsupportedBodyCount);
        Assert.DoesNotContain("StringContent", result.Code);
        Assert.Contains("multipart/form-data", result.Code);
    }

    [Fact]
    public void A_path_item_with_no_supported_verb_is_skipped_and_counted()
    {
        OpenApiDocument document = new()
        {
            Paths =
            {
                ["/products"] = new OpenApiPathItem { Get = Op("listProducts") },
                ["/legacy"] = new OpenApiPathItem(),
            },
        };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "scenario");

        Assert.Equal(1, result.StepCount);
        Assert.Equal(1, result.SkippedOperationlessPathCount);
    }

    [Fact]
    public void Operations_without_an_operation_id_fall_back_to_method_and_path()
    {
        OpenApiDocument document = new()
        {
            Paths =
            {
                ["/poll"] = new OpenApiPathItem { Get = Op(), Post = Op() },
            },
        };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "scenario");

        Assert.Contains("registry.Register(\"GET /poll\")", result.Code);
        Assert.Contains("registry.Register(\"POST /poll\")", result.Code);
    }

    [Fact]
    public void Repeated_operation_ids_across_different_paths_get_unique_step_labels()
    {
        OpenApiDocument document = new()
        {
            Paths =
            {
                ["/products"] = new OpenApiPathItem { Get = Op("list") },
                ["/legacy-products"] = new OpenApiPathItem { Get = Op("list") },
            },
        };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "scenario");

        Assert.Contains("registry.Register(\"list\")", result.Code);
        Assert.Contains("registry.Register(\"list (2)\")", result.Code);
    }

    [Fact]
    public void Header_parameters_are_added_to_the_request_as_placeholders()
    {
        OpenApiDocument document = new()
        {
            Paths =
            {
                ["/products"] = new OpenApiPathItem
                {
                    Get = Op("listProducts", [Param("X-Api-Key", "header", required: true, schema: Schema("string"))]),
                },
            },
        };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "scenario");

        Assert.Contains("TryAddWithoutValidation(\"X-Api-Key\", \"chaine\")", result.Code);
    }

    [Fact]
    public void A_declared_example_overrides_the_type_based_placeholder()
    {
        OpenApiDocument document = new()
        {
            Paths =
            {
                ["/products/{id}"] = new OpenApiPathItem
                {
                    Get = Op("getProduct", [Param("id", "path", required: true, schema: Schema("integer"), example: JsonValue.Create(42))]),
                },
            },
        };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "scenario");

        Assert.Contains("\"/products/42\"", result.Code);
    }

    [Fact]
    public void A_workflow_name_with_non_identifier_characters_still_produces_a_valid_class_name()
    {
        OpenApiDocument document = new() { Paths = { ["/x"] = new OpenApiPathItem { Get = Op("x") } } };

        OpenApiConversionResult result = OpenApiConverter.Convert(document, "my checkout flow! (v2)");

        Assert.Matches(@"public sealed class [A-Za-z_][A-Za-z0-9_]* : IWorkflow", result.Code);
        Assert.Contains("Name => \"my checkout flow! (v2)\"", result.Code);
    }

    [Fact]
    public void An_empty_document_produces_a_workflow_with_no_steps_rather_than_failing()
    {
        OpenApiConversionResult result = OpenApiConverter.Convert(new OpenApiDocument(), "empty");

        Assert.Equal(0, result.StepCount);
        Assert.Contains("public void RegisterSteps(StepRegistry registry)", result.Code);
        Assert.Contains("public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)", result.Code);
    }
}