using Sirocco.HarConvert;

namespace Sirocco.UnitTests.HarConvert;

public sealed class HarConverterTests
{
    private static HarEntry Entry(string method, string url, HarPostData? postData = null, params (string Name, string Value)[] headers)
    {
        HarRequest request = new()
        {
            Method = method,
            Url = url,
            PostData = postData,
        };

        foreach ((string name, string value) in headers)
        {
            request.Headers.Add(new HarHeader { Name = name, Value = value });
        }

        return new HarEntry { Request = request };
    }

    [Fact]
    public void A_null_log_or_blank_name_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => HarConverter.Convert(null!, "scenario"));
        Assert.Throws<ArgumentException>(() => HarConverter.Convert(new HarLog(), " "));
    }

    [Fact]
    public void A_simple_get_request_produces_a_workflow_that_registers_and_executes_the_step()
    {
        HarLog log = new() { Entries = [Entry("GET", "https://api.example.com/products")] };

        HarConversionResult result = HarConverter.Convert(log, "my-scenario");

        Assert.Equal(1, result.StepCount);
        Assert.Contains(": IWorkflow", result.Code);
        Assert.Contains("registry.Register(\"GET /products\")", result.Code);
        Assert.Contains("new HttpMethod(\"GET\")", result.Code);
        Assert.Contains("\"/products\"", result.Code);
        Assert.Contains("context.HttpClient.SendAsync(request0, cancellationToken)", result.Code);
        Assert.Contains("scope0.CompleteHttp((int)response0.StatusCode)", result.Code);
        Assert.EndsWith(Environment.NewLine, result.Code);
    }

    [Fact]
    public void The_script_ends_with_an_expression_instantiating_the_generated_class()
    {
        HarLog log = new() { Entries = [Entry("GET", "https://api.example.com/x")] };

        HarConversionResult result = HarConverter.Convert(log, "checkout");

        Assert.Matches(@"new [A-Za-z_][A-Za-z0-9_]*\(\)\s*$", result.Code.TrimEnd());
    }

    [Fact]
    public void Static_assets_are_skipped_and_counted_rather_than_converted()
    {
        HarLog log = new()
        {
            Entries =
            [
                Entry("GET", "https://api.example.com/app.js"),
                Entry("GET", "https://api.example.com/style.css"),
                Entry("GET", "https://api.example.com/logo.png"),
                Entry("GET", "https://api.example.com/products"),
            ],
        };

        HarConversionResult result = HarConverter.Convert(log, "scenario");

        Assert.Equal(1, result.StepCount);
        Assert.Equal(3, result.SkippedStaticAssetCount);
    }

    [Fact]
    public void Requests_to_a_less_common_host_than_the_target_are_skipped_and_counted()
    {
        HarLog log = new()
        {
            Entries =
            [
                Entry("GET", "https://api.example.com/products"),
                Entry("GET", "https://fonts.googleapis.com/css"),
                Entry("GET", "https://api.example.com/cart"),
            ],
        };

        HarConversionResult result = HarConverter.Convert(log, "scenario");

        Assert.Equal(2, result.StepCount);
        Assert.Equal(1, result.SkippedOtherHostCount);
        Assert.Equal("https://api.example.com", result.BaseHost);
    }

    /// <summary>
    /// Reproduit un bug reel trouve en verifiant un vrai HAR : un appel tiers sans extension
    /// reconnue dans son chemin (donc pas filtre comme actif statique) qui arrive *avant* le
    /// premier appel a la cible ne doit jamais devenir l'hote de base par accident — sans quoi
    /// c'est la cible elle-meme qui se retrouverait traitee comme "un autre hote" et ignoree.
    /// </summary>
    [Fact]
    public void A_third_party_call_without_a_recognized_extension_appearing_first_does_not_hijack_the_base_host()
    {
        HarLog log = new()
        {
            Entries =
            [
                Entry("GET", "https://fonts.googleapis.com/css?family=Inter"),
                Entry("POST", "https://api.example.com/login"),
                Entry("GET", "https://api.example.com/products"),
                Entry("POST", "https://api.example.com/checkout"),
            ],
        };

        HarConversionResult result = HarConverter.Convert(log, "scenario");

        Assert.Equal("https://api.example.com", result.BaseHost);
        Assert.Equal(3, result.StepCount);
        Assert.Equal(1, result.SkippedOtherHostCount);
    }

    [Fact]
    public void An_entry_with_an_unparsable_url_is_skipped_without_throwing()
    {
        HarLog log = new() { Entries = [Entry("GET", "not-a-url"), Entry("GET", "https://api.example.com/ok")] };

        HarConversionResult result = HarConverter.Convert(log, "scenario");

        Assert.Equal(1, result.StepCount);
    }

    [Fact]
    public void Repeated_method_and_path_combinations_get_unique_step_labels()
    {
        HarLog log = new()
        {
            Entries =
            [
                Entry("GET", "https://api.example.com/poll"),
                Entry("GET", "https://api.example.com/poll"),
            ],
        };

        HarConversionResult result = HarConverter.Convert(log, "scenario");

        Assert.Contains("registry.Register(\"GET /poll\")", result.Code);
        Assert.Contains("registry.Register(\"GET /poll (2)\")", result.Code);
    }

    [Fact]
    public void A_post_body_is_rendered_as_string_content_with_its_mime_type()
    {
        HarLog log = new()
        {
            Entries =
            [
                Entry(
                    "POST",
                    "https://api.example.com/checkout",
                    new HarPostData { MimeType = "application/json", Text = """{"items":[1]}""" }),
            ],
        };

        HarConversionResult result = HarConverter.Convert(log, "scenario");

        Assert.Contains("new StringContent(\"{\\\"items\\\":[1]}\", Encoding.UTF8, \"application/json\")", result.Code);
    }

    [Fact]
    public void Hop_by_hop_and_redundant_headers_are_stripped_but_custom_headers_survive()
    {
        HarLog log = new()
        {
            Entries =
            [
                Entry(
                    "GET",
                    "https://api.example.com/cart",
                    null,
                    ("Host", "api.example.com"),
                    ("Content-Length", "0"),
                    ("Connection", "keep-alive"),
                    ("Accept-Encoding", "gzip"),
                    ("Content-Type", "application/json"),
                    ("Authorization", "Bearer abc123")),
            ],
        };

        HarConversionResult result = HarConverter.Convert(log, "scenario");

        Assert.Contains("TryAddWithoutValidation(\"Authorization\", \"Bearer abc123\")", result.Code);
        Assert.DoesNotContain("\"Host\"", result.Code);
        Assert.DoesNotContain("\"Content-Length\"", result.Code);
        Assert.DoesNotContain("\"Connection\"", result.Code);
        Assert.DoesNotContain("\"Accept-Encoding\"", result.Code);
        Assert.DoesNotContain("\"Content-Type\"", result.Code);
    }

    [Fact]
    public void A_value_containing_quotes_and_backslashes_is_escaped_safely()
    {
        HarLog log = new()
        {
            Entries =
            [
                Entry(
                    "GET",
                    "https://api.example.com/cart",
                    null,
                    ("X-Custom", "a \"quoted\" value with a \\ backslash")),
            ],
        };

        HarConversionResult result = HarConverter.Convert(log, "scenario");

        Assert.Contains("""a \"quoted\" value with a \\ backslash""", result.Code);
    }

    [Fact]
    public void A_workflow_name_with_non_identifier_characters_still_produces_a_valid_class_name()
    {
        HarLog log = new() { Entries = [Entry("GET", "https://api.example.com/x")] };

        HarConversionResult result = HarConverter.Convert(log, "my checkout flow! (v2)");

        Assert.Matches(@"public sealed class [A-Za-z_][A-Za-z0-9_]* : IWorkflow", result.Code);
        Assert.Contains("Name => \"my checkout flow! (v2)\"", result.Code);
    }

    [Fact]
    public void An_empty_log_produces_a_workflow_with_no_steps_rather_than_failing()
    {
        HarConversionResult result = HarConverter.Convert(new HarLog(), "empty");

        Assert.Equal(0, result.StepCount);
        Assert.Contains("public void RegisterSteps(StepRegistry registry)", result.Code);
        Assert.Contains("public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)", result.Code);
    }
}