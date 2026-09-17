using Microsoft.Data.SqlClient;
using Spectre.Console;
using SqlShell.Cli.Rendering;
using SqlShell.Cli.Shell;
using SqlShell.Core.Connection;
using SqlShell.Core.Profiles;

namespace SqlShell.Cli.Commands;

/// <summary>The default command: interactive SQL shell and one-shot script runner.</summary>
internal static class ShellCli
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-p"] = "profile",
        ["--profile"] = "profile",
        ["-f"] = "file",
        ["--file"] = "file",
        ["-x"] = "full-width",
        ["-h"] = "help",
        ["--help"] = "help",
    };

    public static int Run(string[] args)
    {
        try
        {
            var parsed = Arguments.Parse(
                args, Aliases, flags: ["full-width", "help"], valued: ["profile", "file"]);
            if (parsed.Flag("help"))
            {
                PrintHelp();
                return 0;
            }

            var store = new ProfileStore();
            var renderer = new ResultRenderer(parsed.Flag("full-width"), AnsiConsole.Console);
            var manager = new ConnectionManager(store, renderer.Event);
            try
            {
                manager.Connect(store.Get(parsed.Value("profile")));
                var repl = new SqlShellRepl(manager, store, renderer);
                var file = parsed.Value("file");
                if (file is not null)
                {
                    return repl.RunFile(file) ? 0 : 1;
                }

                repl.Run();
                return 0;
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

    private static void PrintHelp()
    {
        const string body =
            "Usage: sqlshell [-p PROFILE] [-f FILE] [-x]\n"
            + "  -p, --profile NAME   Connection profile to use\n"
            + "  -f, --file FILE      Execute a SQL file and exit\n"
            + "  -x, --full-width     Wrap full cell values instead of truncating";
        AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(body)}[/]");
    }
}
