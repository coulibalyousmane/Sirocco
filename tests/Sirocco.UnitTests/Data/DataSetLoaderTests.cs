using Sirocco.Domain.Data;
using Sirocco.Scenarios.Data;

namespace Sirocco.UnitTests.Data;

public sealed class DataSetLoaderTests
{
    [Fact]
    public void ParseCsv_reads_one_row_per_line_keyed_by_the_header()
    {
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows = DataSetLoader.ParseCsv("username,password\nalice,pw1\nbob,pw2\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal("alice", rows[0]["username"]);
        Assert.Equal("pw1", rows[0]["password"]);
        Assert.Equal("bob", rows[1]["username"]);
    }

    [Fact]
    public void ParseCsv_supports_quoted_fields_with_embedded_commas_and_escaped_quotes()
    {
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows =
            DataSetLoader.ParseCsv("name,note\n\"Doe, Jane\",\"She said \"\"hi\"\"\"\n");

        Assert.Equal("Doe, Jane", rows[0]["name"]);
        Assert.Equal("She said \"hi\"", rows[0]["note"]);
    }

    [Fact]
    public void ParseCsv_tolerates_a_trailing_blank_line()
    {
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows = DataSetLoader.ParseCsv("a\n1\n2\n");

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ParseCsv_rejects_an_empty_file() =>
        Assert.Throws<FormatException>(() => DataSetLoader.ParseCsv(string.Empty));

    [Fact]
    public void ParseJson_reads_one_row_per_array_element()
    {
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows =
            DataSetLoader.ParseJson("""[{"productId":1,"quantity":2},{"productId":3,"quantity":"5"}]""");

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["productId"]);
        Assert.Equal("2", rows[0]["quantity"]);
        Assert.Equal("5", rows[1]["quantity"]);
    }

    [Fact]
    public void ParseJson_rejects_a_non_array_root() =>
        Assert.Throws<FormatException>(() => DataSetLoader.ParseJson("""{"productId":1}"""));

    [Fact]
    public void ParseJson_rejects_a_non_object_element() =>
        Assert.Throws<FormatException>(() => DataSetLoader.ParseJson("[1, 2]"));

    [Fact]
    public void ParseJson_rejects_invalid_json() =>
        Assert.Throws<FormatException>(() => DataSetLoader.ParseJson("not json"));

    [Fact]
    public void LoadFromFile_rejects_a_missing_file() =>
        Assert.Throws<FileNotFoundException>(() => DataSetLoader.LoadFromFile("does-not-exist.csv"));

    [Fact]
    public void LoadFromFile_rejects_an_unrecognized_extension()
    {
        string path = Path.GetTempFileName();
        try
        {
            Assert.Throws<NotSupportedException>(() => DataSetLoader.LoadFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromFile_loads_a_real_csv_file_end_to_end()
    {
        string path = Path.GetTempFileName() + ".csv";
        File.WriteAllText(path, "username\nalice\nbob\n");
        try
        {
            DataSet dataSet = DataSetLoader.LoadFromFile(path, DataSetIterationStrategy.Circular);

            Assert.Equal(2, dataSet.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromFile_loads_a_real_json_file_end_to_end()
    {
        string path = Path.GetTempFileName() + ".json";
        File.WriteAllText(path, """[{"username":"alice"},{"username":"bob"}]""");
        try
        {
            DataSet dataSet = DataSetLoader.LoadFromFile(path, DataSetIterationStrategy.Circular);

            Assert.Equal(2, dataSet.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromFile_wraps_an_empty_data_set_as_a_format_exception()
    {
        string path = Path.GetTempFileName() + ".json";
        File.WriteAllText(path, "[]");
        try
        {
            Assert.Throws<FormatException>(() => DataSetLoader.LoadFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}