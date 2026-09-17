using SqlShell.Cli.Shell;

namespace SqlShell.Cli.Tests;

public class CommandLineSplitterTests
{
    [Fact]
    public void Splits_plain_tokens()
    {
        Assert.Equal(new[] { ".run", "report.sql" }, CommandLineSplitter.Split(".run report.sql"));
    }

    [Fact]
    public void Collapses_whitespace_runs()
    {
        Assert.Equal(new[] { ".connect", "work" }, CommandLineSplitter.Split("  .connect   work  "));
    }

    [Fact]
    public void Preserves_quoted_path_with_spaces()
    {
        var tokens = CommandLineSplitter.Split("select 1 > \"my reports/results.csv\"");
        Assert.Equal("select", tokens[0]);
        Assert.Equal("1", tokens[1]);
        Assert.Equal(">", tokens[2]);
        Assert.Equal("\"my reports/results.csv\"", tokens[3]);
    }

    [Fact]
    public void Unbalanced_quote_throws()
        => Assert.Throws<FormatException>(() => CommandLineSplitter.Split(".run \"unterminated"));
}
