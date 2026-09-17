using SqlShell.Core.Scripts;

namespace SqlShell.Core.Tests;

public class ScriptTests
{
    [Fact]
    public void Split_batches_and_repeat()
    {
        const string script = "SELECT 1;\nGO\nSELECT 2\nGO 2 -- repeat\n";
        Assert.Equal(new[] { "SELECT 1;", "SELECT 2", "SELECT 2" }, BatchParser.SplitBatches(script));
    }

    [Fact]
    public void Go_inside_sql_is_not_separator()
    {
        Assert.Equal(new[] { "SELECT 'GO';\n-- GO" }, BatchParser.SplitBatches("SELECT 'GO';\n-- GO\n"));
    }

    [Fact]
    public void Current_batch_uses_cursor_line()
    {
        const string script = "SELECT 1;\nGO\nSELECT 2;\nSELECT 3;\nGO\nSELECT 4;";
        Assert.Equal("SELECT 2;\nSELECT 3;", BatchParser.CurrentBatch(script, 2));
        Assert.Equal("SELECT 4;", BatchParser.CurrentBatch(script, 5));
        Assert.Equal("SELECT 1;", BatchParser.CurrentBatch(script, 1));
    }
}
