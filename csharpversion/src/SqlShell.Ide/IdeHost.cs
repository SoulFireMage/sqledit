using Terminal.Gui.App;
using SqlShell.Core.Connection;
using SqlShell.Core.Profiles;

namespace SqlShell.Ide;

/// <summary>Runs the full-screen IDE over an existing connection.</summary>
public static class IdeHost
{
    public static int Run(ConnectionManager manager, ProfileStore store, string? file, int maxRows)
    {
        var app = Application.Create();
        app.Init(null);
        try
        {
            var window = new IdeWindow(app, manager, store, file, maxRows);
            manager.Event = (kind, message) => app.Invoke(() => window.LogEvent(kind, message));
            if (manager.Profile is { } profile)
            {
                window.LogEvent("connected", $"Connected: {profile.Name} — {profile.Server}/{profile.Database}");
            }

            app.Run(window, exception =>
            {
                window.LogEvent("error", exception.Message);
                return false;
            });
            return 0;
        }
        finally
        {
            app.Dispose();
        }
    }
}