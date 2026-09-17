using System.Text;

namespace SqlShell.Ide.Tests;

public class IdeDocumentTests
{
    [Fact]
    public void Load_reads_file_and_tracks_dirty_state()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "query.sql");
        File.WriteAllText(path, "SELECT 1", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var document = new IdeDocument();
        document.Load(path);

        Assert.Equal("SELECT 1", document.SavedText);
        Assert.Equal(path, document.Path);
        Assert.False(document.IsDirty("SELECT 1"));
        Assert.True(document.IsDirty("SELECT 2"));
    }

    [Fact]
    public void Save_writes_utf8_without_bom_and_updates_state()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "out.sql");

        var document = new IdeDocument();
        document.Save("SELECT 42", path);

        Assert.Equal(path, document.Path);
        Assert.Equal("SELECT 42", document.SavedText);
        Assert.False(document.IsDirty("SELECT 42"));

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void Reset_clears_document()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "out.sql");
        var document = new IdeDocument();
        document.Save("SELECT 1", path);

        document.Reset();

        Assert.Null(document.Path);
        Assert.Equal(string.Empty, document.SavedText);
        Assert.Equal("Untitled.sql", document.DisplayName);
    }
}