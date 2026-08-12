using System.Text.Json.Nodes;
using Tempest.PostmanConvert;

namespace Tempest.UnitTests.PostmanConvert;

public sealed class PostmanConverterTests
{
    private static PostmanHeader Header(string key, string value, bool disabled = false) =>
        new() { Key = key, Value = value, Disabled = disabled };

    private static PostmanRequest Req(string method, JsonNode url, List<PostmanHeader>? headers = null, PostmanBody? body = null) =>
        new() { Method = method, Url = url, Header = headers ?? [], Body = body };

    private static PostmanItem Item(string? name, PostmanRequest? request = null, List<PostmanItem>? children = null) =>
        new() { Name = name, Request = request, Item = children };

    private static PostmanBody RawBody(string raw, string? language = null) =>
        new() { Mode = "raw", Raw = raw, Options = new PostmanBodyOptions { Raw = new PostmanRawOptions { Language = language } } };

    private static PostmanBody UrlencodedBody(params PostmanHeader[] entries) =>
        new() { Mode = "urlencoded", Urlencoded = [.. entries] };

    private static PostmanVariable Variable(string key, string value) =>
        new() { Key = key, Value = JsonValue.Create(value) };

    [Fact]
    public void A_null_collection_or_blank_name_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => PostmanConverter.Convert(null!, "scenario"));
        Assert.Throws<ArgumentException>(() => PostmanConverter.Convert(new PostmanCollection(), " "));
    }

    [Fact]
    public void A_simple_request_produces_a_workflow_that_registers_and_executes_the_step()
    {
        PostmanCollection collection = new() { Item = [Item("List products", Req("GET", JsonValue.Create("/api/products")))] };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "my-scenario");

        Assert.Equal(1, result.StepCount);
        Assert.Contains(": IWorkflow", result.Code);
        Assert.Contains("registry.Register(\"List products\")", result.Code);
        Assert.Contains("new HttpMethod(\"GET\")", result.Code);
        Assert.Contains("\"/api/products\"", result.Code);
        Assert.Contains("context.HttpClient.SendAsync(request0, cancellationToken)", result.Code);
        Assert.Contains("scope0.CompleteHttp((int)response0.StatusCode)", result.Code);
        Assert.EndsWith(Environment.NewLine, result.Code);
    }

    [Fact]
    public void The_script_ends_with_an_expression_instantiating_the_generated_class()
    {
        PostmanCollection collection = new() { Item = [Item("x", Req("GET", JsonValue.Create("/x")))] };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "checkout");

        Assert.Matches(@"new [A-Za-z_][A-Za-z0-9_]*\(\)\s*$", result.Code.TrimEnd());
    }

    [Fact]
    public void Nested_folders_are_walked_recursively_and_prefixed_in_the_label()
    {
        PostmanCollection collection = new()
        {
            Item =
            [
                Item("Auth", children: [Item("Login", Req("POST", JsonValue.Create("/api/auth/login")))]),
            ],
        };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "scenario");

        Assert.Equal(1, result.StepCount);
        Assert.Contains("registry.Register(\"Auth / Login\")", result.Code);
    }

    [Fact]
    public void A_string_form_url_is_accepted_same_as_an_object_form_url()
    {
        JsonObject objectUrl = new() { ["raw"] = "/api/products" };
        PostmanCollection collection = new()
        {
            Item =
            [
                Item("string-form", Req("GET", JsonValue.Create("/api/products"))),
                Item("object-form", Req("GET", objectUrl)),
            ],
        };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "scenario");

        Assert.Equal(2, result.StepCount);
        Assert.Contains("registry.Register(\"string-form\")", result.Code);
        Assert.Contains("registry.Register(\"object-form\")", result.Code);
        Assert.Equal(2, CountOccurrences(result.Code, "\"/api/products\""));
    }

    [Fact]
    public void A_raw_json_body_sets_the_json_content_type()
    {
        PostmanCollection collection = new()
        {
            Item = [Item("checkout", Req("POST", JsonValue.Create("/api/checkout"), body: RawBody("""{"a":1}""", "json")))],
        };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "scenario");

        Assert.Contains("application/json", result.Code);
        Assert.Contains("StringContent", result.Code);
    }

    [Fact]
    public void An_urlencoded_body_is_rendered_as_key_value_pairs()
    {
        PostmanBody body = UrlencodedBody(Header("page", "1"), Header("sort", "desc", disabled: true));
        PostmanCollection collection = new()
        {
            Item = [Item("search", Req("POST", JsonValue.Create("/api/search"), body: body))],
        };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "scenario");

        Assert.Contains("application/x-www-form-urlencoded", result.Code);
        Assert.Contains("page=1", result.Code);
        Assert.DoesNotContain("sort=", result.Code);
    }

    [Fact]
    public void A_formdata_body_is_generated_without_a_body_and_counted()
    {
        PostmanCollection collection = new()
        {
            Item = [Item("upload", Req("POST", JsonValue.Create("/api/upload"), body: new PostmanBody { Mode = "formdata" }))],
        };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "scenario");

        Assert.Equal(1, result.StepCount);
        Assert.Equal(1, result.SkippedFormDataBodyCount);
        Assert.DoesNotContain("StringContent", result.Code);
    }

    [Fact]
    public void Collection_variables_are_substituted_into_the_url_and_headers()
    {
        PostmanCollection collection = new()
        {
            Variable = [Variable("token", "abc123")],
            Item =
            [
                Item(
                    "checkout",
                    Req("POST", JsonValue.Create("/api/checkout"), headers: [Header("Authorization", "Bearer {{token}}")])),
            ],
        };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "scenario");

        Assert.Equal(0, result.UnresolvedVariableCount);
        Assert.Contains("TryAddWithoutValidation(\"Authorization\", \"Bearer abc123\")", result.Code);
    }

    [Fact]
    public void An_unresolved_variable_becomes_a_placeholder_and_is_counted()
    {
        PostmanCollection collection = new()
        {
            Item = [Item("get-order", Req("GET", JsonValue.Create("/api/orders/{{orderId}}")))],
        };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "scenario");

        Assert.Equal(1, result.UnresolvedVariableCount);
        Assert.Contains("\"/api/orders/valeur\"", result.Code);
    }

    [Fact]
    public void Disabled_headers_are_ignored()
    {
        PostmanCollection collection = new()
        {
            Item =
            [
                Item(
                    "x",
                    Req("GET", JsonValue.Create("/api/x"), headers: [Header("X-Kept", "yes"), Header("X-Disabled", "no", disabled: true)])),
            ],
        };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "scenario");

        Assert.Contains("X-Kept", result.Code);
        Assert.DoesNotContain("X-Disabled", result.Code);
    }

    [Fact]
    public void Repeated_request_names_get_unique_step_labels()
    {
        PostmanCollection collection = new()
        {
            Item =
            [
                Item("poll", Req("GET", JsonValue.Create("/api/a"))),
                Item("poll", Req("GET", JsonValue.Create("/api/b"))),
            ],
        };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "scenario");

        Assert.Contains("registry.Register(\"poll\")", result.Code);
        Assert.Contains("registry.Register(\"poll (2)\")", result.Code);
    }

    [Fact]
    public void A_request_without_a_name_falls_back_to_method_and_path()
    {
        PostmanCollection collection = new() { Item = [Item(null, Req("GET", JsonValue.Create("/api/x")))] };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "scenario");

        Assert.Contains("registry.Register(\"GET /api/x\")", result.Code);
    }

    [Fact]
    public void A_workflow_name_with_non_identifier_characters_still_produces_a_valid_class_name()
    {
        PostmanCollection collection = new() { Item = [Item("x", Req("GET", JsonValue.Create("/x")))] };

        PostmanConversionResult result = PostmanConverter.Convert(collection, "my checkout flow! (v2)");

        Assert.Matches(@"public sealed class [A-Za-z_][A-Za-z0-9_]* : IWorkflow", result.Code);
        Assert.Contains("Name => \"my checkout flow! (v2)\"", result.Code);
    }

    [Fact]
    public void An_empty_collection_produces_a_workflow_with_no_steps_rather_than_failing()
    {
        PostmanConversionResult result = PostmanConverter.Convert(new PostmanCollection(), "empty");

        Assert.Equal(0, result.StepCount);
        Assert.Contains("public void RegisterSteps(StepRegistry registry)", result.Code);
        Assert.Contains("public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)", result.Code);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}