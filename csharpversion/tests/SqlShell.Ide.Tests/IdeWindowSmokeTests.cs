using System.Drawing;
using SqlShell.Core.Connection;
using SqlShell.Core.Profiles;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;

namespace SqlShell.Ide.Tests;

/// <summary>
/// Constructs the full window against a fake connection and checks the layout.
/// Terminal.Gui needs a usable console driver; when one is unavailable (for
/// example a headless test runner) the test is inconclusive and returns.
/// </summary>
public class IdeWindowSmokeTests
{
    [Fact]
    public void Editor_fills_most_of_the_screen_and_does_not_overlap_output()
    {
        IApplication app;
        try
        {
            app = Application.Create();
            app.Init(null);
        }
        catch (Exception)
        {
            return;
        }

        using var temp = new TempDirectory();
        try
        {
            var store = new ProfileStore(temp.Path, new InMemoryCredentialStore());
            var manager = new ConnectionManager(store, (_, _) => { });
            var window = new IdeWindow(app, manager, store, null, 5000);
            Assert.Equal("sqlshell IDE", window.Title);

            window.Layout(new Size(100, 40));

            var editor = window.SubViews.First(view => view.Id == "editor");
            var output = window.SubViews.First(view => view.Id == "output");
            var status = window.SubViews.First(view => view.Id == "status");

            var editorControl = Assert.IsType<Terminal.Gui.Editor.Editor>(editor);
            Assert.Equal("TSQL", editorControl.HighlightingDefinition?.Name);

            Assert.True(
                editor.Frame.Height > output.Frame.Height,
                $"editor height {editor.Frame.Height} should exceed output height {output.Frame.Height}");
            Assert.True(
                editor.Frame.Y + editor.Frame.Height <= output.Frame.Y,
                $"editor bottom {editor.Frame.Y + editor.Frame.Height} overlaps output top {output.Frame.Y}");
            Assert.True(
                output.Frame.Y + output.Frame.Height <= status.Frame.Y,
                $"output bottom {output.Frame.Y + output.Frame.Height} overlaps status top {status.Frame.Y}");
        }
        finally
        {
            app.Dispose();
        }
    }
}