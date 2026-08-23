using Sirocco.RecorderProxy;

namespace Sirocco.UnitTests.RecorderProxy;

public sealed class RecorderOptionsTests
{
    [Fact]
    public void Parses_target_url_listen_out_and_name()
    {
        RecorderOptions options = RecorderOptions.Parse(
            ["--target-url", "http://localhost:5299", "--listen", "http://localhost:9000", "--out", "scenario.csx", "--name", "checkout"]);

        Assert.Equal("http://localhost:5299", options.TargetUrl);
        Assert.Equal("http://localhost:9000", options.ListenUrl);
        Assert.Equal("scenario.csx", options.OutputPath);
        Assert.Equal("checkout", options.WorkflowName);
    }

    [Fact]
    public void Applies_default_listen_url_and_derives_name_from_output_path()
    {
        RecorderOptions options = RecorderOptions.Parse(["--target-url", "http://localhost:5299", "--out", "recorded-checkout.csx"]);

        Assert.Equal("http://localhost:8888", options.ListenUrl);
        Assert.Equal("recorded-checkout", options.WorkflowName);
    }

    [Fact]
    public void Throws_when_target_url_is_missing()
    {
        FormatException ex = Assert.Throws<FormatException>(() => RecorderOptions.Parse(["--out", "scenario.csx"]));
        Assert.Contains("--target-url", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Throws_when_out_is_missing()
    {
        FormatException ex = Assert.Throws<FormatException>(() => RecorderOptions.Parse(["--target-url", "http://localhost:5299"]));
        Assert.Contains("--out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Throws_when_target_url_is_not_a_valid_absolute_url()
    {
        Assert.Throws<FormatException>(() => RecorderOptions.Parse(["--target-url", "not-a-url", "--out", "scenario.csx"]));
    }

    [Fact]
    public void Throws_when_listen_url_is_not_a_valid_absolute_url()
    {
        Assert.Throws<FormatException>(
            () => RecorderOptions.Parse(["--target-url", "http://localhost:5299", "--listen", "not-a-url", "--out", "scenario.csx"]));
    }

    [Fact]
    public void Throws_on_unrecognized_option()
    {
        Assert.Throws<FormatException>(
            () => RecorderOptions.Parse(["--target-url", "http://localhost:5299", "--out", "scenario.csx", "--bogus"]));
    }
}