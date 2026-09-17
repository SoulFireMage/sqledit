using SqlShell.Core.Connection;

namespace SqlShell.Ide.Tests;

public class ResultTableSourceTests
{
    [Fact]
    public void Exposes_rows_columns_and_names()
    {
        var result = new ResultSet(
            new[] { "a", "b" },
            new object?[][] { new object?[] { 1, "x" } });

        var source = new ResultTableSource(result);

        Assert.Equal(1, source.Rows);
        Assert.Equal(2, source.Columns);
        Assert.Equal(new[] { "a", "b" }, source.ColumnNames);
        Assert.Equal(1, source[0, 0]);
        Assert.Equal("x", source[0, 1]);
    }

    [Fact]
    public void Formats_bytes_as_hex_and_passes_null_through()
    {
        var result = new ResultSet(
            new[] { "payload", "empty" },
            new object?[][] { new object?[] { new byte[] { 0x01, 0xAB }, null } });

        var source = new ResultTableSource(result);

        Assert.Equal("0x01ab", source[0, 0]);
        Assert.Null(source[0, 1]);
    }
}