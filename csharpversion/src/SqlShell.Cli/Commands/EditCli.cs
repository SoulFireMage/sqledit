using Microsoft.Data.SqlClient;
using Spectre.Console;
using SqlShell.Core.Connection;
using SqlShell.Core.Profiles;
using SqlShell.Ide;

namespace SqlShell.Cli.Commands;

/// <summary>Launches the full-screen Terminal.Gui IDE over a live connection.</summary>
internal static class EditCli
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-p"] = "profile",
        ["--profile"] = "profile",
        ["-o"] = "offline",
        ["--offline"] = "offline",
        ["-h"] = "help",
        ["--help"] = "help",
    };

    public static int Run(string[] args)
    {
        try
        {
            var parsed = Arguments.Parse(
                args, Aliases, flags: ["help", "offline"], valued: ["profile", "max-rows"]);
            if (parsed.Flag("help"))
            {
                AnsiConsole.MarkupLine(
                    $"[cyan]{Markup.Escape("Usage: sqlshell edit [FILE.sql] [-p PROFILE] [--max-rows N] [--offline]")}[/]");
                return 0;
            }

            var maxRows = parsed.IntValue("max-rows") ?? 5000;
            if (maxRows < 1)
            {
                throw new ArgumentException("--max-rows must be at least 1");
            }

            var store = new ProfileStore();
            var manager = new ConnectionManager(store, (_, _) => { });
            try
            {
                if (!parsed.Flag("offline"))
                {
                    manager.Connect(store.Get(parsed.Value("profile")));
                }

                return IdeHost.Run(manager, store, parsed.Positional(0), maxRows);
            }
            finally
            {
                manager.Close();
            }
        }
        catch (Exception exception) when (exception
            is ProfileException
            or IOException
            or InvalidOperationException
            or ArgumentException
            or System.ComponentModel.Win32Exception
            or AuthenticationException
            or CertificateValidationException
            or SqlException)
        {
            return CommandErrors.Report(exception);
        }
    }
}