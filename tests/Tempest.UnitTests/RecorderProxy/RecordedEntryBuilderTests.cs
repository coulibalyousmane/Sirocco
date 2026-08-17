using Tempest.HarConvert;
using Tempest.RecorderProxy;

namespace Tempest.UnitTests.RecorderProxy;

public sealed class RecordedEntryBuilderTests
{
    [Fact]
    public void Builds_an_entry_with_method_url_and_headers()
    {
        HarEntry entry = RecordedEntryBuilder.Build(
            "GET", "/api/products", [("Authorization", "Bearer abc")], body: null, contentType: null, targetBaseUrl: "http://localhost:5299");

        Assert.Equal("GET", entry.Request.Method);
        Assert.Equal("http://localhost:5299/api/products", entry.Request.Url);
        Assert.Contains(entry.Request.Headers, h => h.Name == "Authorization" && h.Value == "Bearer abc");
        Assert.Null(entry.Request.PostData);
    }

    [Fact]
    public void A_trailing_slash_on_the_target_base_url_does_not_duplicate_the_separator()
    {
        HarEntry entry = RecordedEntryBuilder.Build(
            "GET", "/api/products", [], body: null, contentType: null, targetBaseUrl: "http://localhost:5299/");

        Assert.Equal("http://localhost:5299/api/products", entry.Request.Url);
    }

    [Fact]
    public void A_null_or_empty_body_produces_no_post_data()
    {
        HarEntry entry = RecordedEntryBuilder.Build(
            "POST", "/api/checkout", [], body: null, contentType: "application/json", targetBaseUrl: "http://localhost:5299");

        Assert.Null(entry.Request.PostData);
    }

    [Fact]
    public void A_non_empty_body_sets_post_data_with_the_given_content_type()
    {
        HarEntry entry = RecordedEntryBuilder.Build(
            "POST", "/api/checkout", [], body: """{"a":1}""", contentType: "application/json", targetBaseUrl: "http://localhost:5299");

        Assert.NotNull(entry.Request.PostData);
        Assert.Equal("""{"a":1}""", entry.Request.PostData.Text);
        Assert.Equal("application/json", entry.Request.PostData.MimeType);
    }

    [Fact]
    public void A_missing_content_type_defaults_to_text_plain()
    {
        HarEntry entry = RecordedEntryBuilder.Build(
            "POST", "/api/checkout", [], body: "raw", contentType: null, targetBaseUrl: "http://localhost:5299");

        Assert.Equal("text/plain", entry.Request.PostData!.MimeType);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/xml")]
    [InlineData("text/plain")]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/graphql")]
    public void IsTextContent_is_true_for_known_text_media_types(string contentType)
    {
        Assert.True(RecordedEntryBuilder.IsTextContent(contentType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("multipart/form-data")]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    public void IsTextContent_is_false_for_binary_or_unknown_media_types(string? contentType)
    {
        Assert.False(RecordedEntryBuilder.IsTextContent(contentType));
    }
}