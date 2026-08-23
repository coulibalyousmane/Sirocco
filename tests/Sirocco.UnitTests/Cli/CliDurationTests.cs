using Sirocco.Cli;

namespace Sirocco.UnitTests.Cli;

public sealed class CliDurationTests
{
    [Theory]
    [InlineData("30s", 30_000)]
    [InlineData("5m", 300_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("500ms", 500)]
    [InlineData("1.5s", 1_500)]
    [InlineData("30", 30_000)]
    public void Known_formats_are_parsed_to_the_right_number_of_milliseconds(string text, double expectedMilliseconds) =>
        Assert.Equal(expectedMilliseconds, CliDuration.Parse(text).TotalMilliseconds, precision: 3);

    [Fact]
    public void The_ms_suffix_is_not_shadowed_by_the_s_suffix() =>
        Assert.Equal(TimeSpan.FromMilliseconds(500), CliDuration.Parse("500ms"));

    [Theory]
    [InlineData("thirty seconds")]
    [InlineData("30x")]
    [InlineData("s")]
    public void Unrecognized_input_throws_a_format_exception(string text) =>
        Assert.Throws<FormatException>(() => CliDuration.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_throws_an_argument_exception(string text) =>
        Assert.Throws<ArgumentException>(() => CliDuration.Parse(text));
}