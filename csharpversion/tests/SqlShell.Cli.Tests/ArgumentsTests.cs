using SqlShell.Cli.Commands;

namespace SqlShell.Cli.Tests;

public class ArgumentsTests
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-p"] = "profile",
        ["--profile"] = "profile",
        ["-f"] = "file",
        ["-x"] = "full-width",
    };

    [Fact]
    public void Parses_short_alias_with_value()
    {
        var arguments = Arguments.Parse(["-p", "work"], Aliases, valued: ["profile"]);
        Assert.Equal("work", arguments.Value("profile"));
    }

    [Fact]
    public void Parses_long_equals_form()
    {
        var arguments = Arguments.Parse(["--profile=work"], Aliases, valued: ["profile"]);
        Assert.Equal("work", arguments.Value("profile"));
    }

    [Fact]
    public void Parses_flags_and_positionals()
    {
        var arguments = Arguments.Parse(
            ["edit", "-x", "query.sql"], Aliases, flags: ["full-width"], valued: ["file"]);
        Assert.True(arguments.Flag("full-width"));
        Assert.Equal("edit", arguments.Positional(0));
        Assert.Equal("query.sql", arguments.Positional(1));
    }

    [Fact]
    public void Parses_integer_option()
    {
        var arguments = Arguments.Parse(["--max-rows", "250"], valued: ["max-rows"]);
        Assert.Equal(250, arguments.IntValue("max-rows"));
    }

    [Fact]
    public void Unknown_option_throws()
        => Assert.Throws<ArgumentException>(() => Arguments.Parse(["--bogus"]));

    [Fact]
    public void Missing_value_throws()
        => Assert.Throws<ArgumentException>(() => Arguments.Parse(["-p"], Aliases, valued: ["profile"]));

    [Fact]
    public void Double_dash_stops_option_parsing()
    {
        var arguments = Arguments.Parse(["--", "-p", "work"], Aliases, valued: ["profile"]);
        Assert.Equal(2, arguments.Positionals.Count);
        Assert.Equal("-p", arguments.Positional(0));
        Assert.Null(arguments.Value("profile"));
    }
}
