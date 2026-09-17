using System.Text;
using SqlShell.Core.Connection;
using SqlShell.Core.Export;

namespace SqlShell.Core.Tests;

public class ExportTests
{
    [Fact]
    public void Parse_csv_redirection_and_quoted_path()
    {
        var parsed = RedirectionParser.Parse("select * from equipment > \"output files/results.csv\"");
        Assert.NotNull(parsed);
        Assert.Equal("select * from equipment", parsed!.Sql);
        Assert.Equal(Path.Combine("output files", "results.csv"), parsed.FilePath);
        Assert.False(parsed.Append);
    }

    [Fact]
    public void Parse_append_redirection()
    {
        var parsed = RedirectionParser.Parse("select 1 >> results.tsv");
        Assert.NotNull(parsed);
        Assert.True(parsed!.Append);
    }

    [Fact]
    public void Sql_comparison_is_not_redirection()
    {
        Assert.Null(RedirectionParser.Parse("select * from equipment where quantity > 10"));
        Assert.Null(RedirectionParser.Parse("select * from equipment where filename > 'results.csv'"));
    }

    [Fact]
    public void Export_csv_quotes_values_and_formats_null_and_bytes()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "results.csv");
        var result = new ResultSet(
            new[] { "name", "note", "payload" },
            new object?[][]
            {
                new object?[] { "one", "a,b", new byte[] { 0x01 } },
                new object?[] { "two", null, Array.Empty<byte>() },
            });

        Assert.Equal(2, ResultExporter.Export(new[] { result }, path));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Equal("name,note,payload\r\none,\"a,b\",0x01\r\ntwo,,0x\r\n", text);
    }

    [Fact]
    public void Append_does_not_repeat_header()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "results.csv");
        var result = new ResultSet(new[] { "value" }, new object?[][] { new object?[] { 1 } });
        ResultExporter.Export(new[] { result }, path);
        ResultExporter.Export(new[] { result }, path, append: true);
        Assert.Equal("value\r\n1\r\n1\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Export_requires_one_tabular_result()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "results.csv");

        var nonTabular = Assert.Throws<ArgumentException>(
            () => ResultExporter.Export(new[] { new ResultSet([], [], 3) }, path));
        Assert.Contains("did not return", nonTabular.Message);

        var result = new ResultSet(new[] { "value" }, new object?[][] { new object?[] { 1 } });
        var multiple = Assert.Throws<ArgumentException>(
            () => ResultExporter.Export(new[] { result, result }, path));
        Assert.Contains("multiple", multiple.Message);
    }
}
