using Sirocco.Domain.Declarative;

namespace Sirocco.UnitTests.Declarative;

public sealed class ExtractionRuleTests
{
    [Fact]
    public void A_rule_with_a_variable_and_exactly_one_expression_is_valid()
    {
        new ExtractionRule { Variable = "token", Regex = "\"token\":\"([^\"]+)\"" }.Validate();
        new ExtractionRule { Variable = "token", XPath = "/root/token" }.Validate();
        new ExtractionRule { Variable = "token", JsonPath = "$.token" }.Validate();
    }

    [Fact]
    public void A_blank_variable_name_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new ExtractionRule { Variable = " ", Regex = "x" }.Validate());

    [Fact]
    public void Providing_neither_expression_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new ExtractionRule { Variable = "token" }.Validate());

    [Fact]
    public void Providing_both_expressions_is_rejected() =>
        Assert.Throws<ArgumentException>(() =>
            new ExtractionRule { Variable = "token", Regex = "x", XPath = "/root" }.Validate());

    [Fact]
    public void A_malformed_regex_is_rejected_at_validation_time() =>
        Assert.Throws<ArgumentException>(() => new ExtractionRule { Variable = "token", Regex = "(unclosed" }.Validate());

    [Fact]
    public void A_malformed_xpath_is_rejected_at_validation_time() =>
        Assert.Throws<ArgumentException>(() => new ExtractionRule { Variable = "token", XPath = "///[[[" }.Validate());

    [Fact]
    public void Providing_all_three_expressions_is_rejected() =>
        Assert.Throws<ArgumentException>(() =>
            new ExtractionRule { Variable = "token", Regex = "x", XPath = "/root", JsonPath = "$.x" }.Validate());

    [Fact]
    public void A_jsonpath_not_starting_with_dollar_is_rejected_at_validation_time() =>
        Assert.Throws<ArgumentException>(() => new ExtractionRule { Variable = "token", JsonPath = "token" }.Validate());

    [Fact]
    public void A_jsonpath_with_an_unrecognized_segment_is_rejected_at_validation_time() =>
        Assert.Throws<ArgumentException>(() => new ExtractionRule { Variable = "token", JsonPath = "$.items[*]" }.Validate());

    [Fact]
    public void A_regex_with_a_capture_group_extracts_the_group_not_the_whole_match()
    {
        ExtractionRule rule = new() { Variable = "token", Regex = "\"token\":\"([^\"]+)\"" };

        bool found = rule.TryExtract("""{"token":"tok-123","other":"x"}""", out string? value);

        Assert.True(found);
        Assert.Equal("tok-123", value);
    }

    [Fact]
    public void A_regex_without_a_capture_group_extracts_the_whole_match()
    {
        ExtractionRule rule = new() { Variable = "digits", Regex = @"\d+" };

        bool found = rule.TryExtract("order-42", out string? value);

        Assert.True(found);
        Assert.Equal("42", value);
    }

    [Fact]
    public void A_regex_that_does_not_match_reports_no_extraction()
    {
        ExtractionRule rule = new() { Variable = "token", Regex = "\"token\":\"([^\"]+)\"" };

        bool found = rule.TryExtract("""{"other":"x"}""", out string? value);

        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void An_xpath_element_extracts_its_text_content()
    {
        ExtractionRule rule = new() { Variable = "token", XPath = "/root/token" };

        bool found = rule.TryExtract("<root><token>tok-xml</token></root>", out string? value);

        Assert.True(found);
        Assert.Equal("tok-xml", value);
    }

    [Fact]
    public void An_xpath_attribute_extracts_its_value()
    {
        ExtractionRule rule = new() { Variable = "id", XPath = "/root/@id" };

        bool found = rule.TryExtract("""<root id="7" />""", out string? value);

        Assert.True(found);
        Assert.Equal("7", value);
    }

    [Fact]
    public void An_xpath_string_expression_extracts_a_scalar_result()
    {
        ExtractionRule rule = new() { Variable = "token", XPath = "string(/root/token)" };

        bool found = rule.TryExtract("<root><token>tok-scalar</token></root>", out string? value);

        Assert.True(found);
        Assert.Equal("tok-scalar", value);
    }

    [Fact]
    public void An_xpath_with_no_matching_node_reports_no_extraction()
    {
        ExtractionRule rule = new() { Variable = "token", XPath = "/root/missing" };

        bool found = rule.TryExtract("<root><token>x</token></root>", out string? value);

        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void A_body_that_is_not_xml_reports_no_extraction_for_xpath_rules()
    {
        ExtractionRule rule = new() { Variable = "token", XPath = "/root/token" };

        bool found = rule.TryExtract("""{"token":"tok-123"}""", out string? value);

        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void A_jsonpath_property_access_extracts_a_top_level_string()
    {
        ExtractionRule rule = new() { Variable = "token", JsonPath = "$.token" };

        bool found = rule.TryExtract("""{"token":"tok-json","other":"x"}""", out string? value);

        Assert.True(found);
        Assert.Equal("tok-json", value);
    }

    [Fact]
    public void A_jsonpath_can_navigate_nested_properties_and_array_indices()
    {
        ExtractionRule rule = new() { Variable = "id", JsonPath = "$.data.items[1].id" };

        bool found = rule.TryExtract(
            """{"data":{"items":[{"id":"a"},{"id":"b"},{"id":"c"}]}}""",
            out string? value);

        Assert.True(found);
        Assert.Equal("b", value);
    }

    [Fact]
    public void A_jsonpath_extracts_a_number_as_its_textual_representation()
    {
        ExtractionRule rule = new() { Variable = "count", JsonPath = "$.count" };

        bool found = rule.TryExtract("""{"count":42}""", out string? value);

        Assert.True(found);
        Assert.Equal("42", value);
    }

    [Fact]
    public void A_jsonpath_with_no_matching_property_reports_no_extraction()
    {
        ExtractionRule rule = new() { Variable = "token", JsonPath = "$.missing" };

        bool found = rule.TryExtract("""{"token":"tok-json"}""", out string? value);

        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void A_jsonpath_index_out_of_range_reports_no_extraction()
    {
        ExtractionRule rule = new() { Variable = "item", JsonPath = "$.items[5]" };

        bool found = rule.TryExtract("""{"items":["a","b"]}""", out string? value);

        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void A_body_that_is_not_json_reports_no_extraction_for_jsonpath_rules()
    {
        ExtractionRule rule = new() { Variable = "token", JsonPath = "$.token" };

        bool found = rule.TryExtract("<root><token>tok-xml</token></root>", out string? value);

        Assert.False(found);
        Assert.Null(value);
    }
}