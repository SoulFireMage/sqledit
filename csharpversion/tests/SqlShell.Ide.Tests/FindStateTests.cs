namespace SqlShell.Ide.Tests;

public class FindStateTests
{
    [Fact]
    public void Finds_case_insensitively_from_the_start_index()
    {
        var find = new FindState { Text = "select" };
        var match = find.FindNext("abc SELECT def", 0);
        Assert.NotNull(match);
        Assert.Equal(4, match!.Value.Index);
        Assert.Equal(6, match.Value.Length);
    }

    [Fact]
    public void Wraps_around_to_the_start()
    {
        var find = new FindState { Text = "go" };
        var match = find.FindNext("go xx", 4);
        Assert.NotNull(match);
        Assert.Equal(0, match!.Value.Index);
    }

    [Fact]
    public void Returns_null_for_missing_text_or_empty_needle()
    {
        Assert.Null(new FindState { Text = "zzz" }.FindNext("abc", 0));
        Assert.Null(new FindState().FindNext("abc", 0));
    }
}