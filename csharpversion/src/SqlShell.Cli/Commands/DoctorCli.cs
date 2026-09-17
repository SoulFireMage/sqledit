using Microsoft.Data.SqlClient;
using Spectre.Console;
using SqlShell.Core;

namespace SqlShell.Cli.Commands;

internal static class DoctorCli
{
    public static int Run()
    {
        var sqlClient = typeof(SqlConnection).Assembly.GetName().Version?.ToString() ?? "unknown";
        AnsiConsole.MarkupLine($"[cyan]sqlshell[/] {SqlShellInfo.Version}");
        AnsiConsole.MarkupLine($".NET runtime: {Environment.Version}");
        AnsiConsole.MarkupLine($"OS: {Environment.OSVersion.VersionString}");
        AnsiConsole.MarkupLine($"SQL driver: Microsoft.Data.SqlClient {sqlClient}");
        AnsiConsole.MarkupLine("Terminal IDE: not implemented yet");
        AnsiConsole.MarkupLine("[green]Core environment is ready.[/]");
        return 0;
    }
}
