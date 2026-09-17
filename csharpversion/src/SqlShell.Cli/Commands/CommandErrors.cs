using Spectre.Console;

namespace SqlShell.Cli.Commands;

/// <summary>Consistent error reporting and exit codes for commands.</summary>
internal static class CommandErrors
{
    public static int Report(Exception exception)
    {
        AnsiConsole.MarkupLine($"[bold red]Error: {Markup.Escape(exception.Message)}[/]");
        return 1;
    }
}
