using Spectre.Console;
using SqlShell.Cli.Commands;
using SqlShell.Core;

return args.Length == 0
    ? ShellCli.Run([])
    : args[0] switch
    {
        "-v" or "--version" => PrintVersion(),
        "-h" or "--help" or "help" => PrintRootHelp(),
        "doctor" => DoctorCli.Run(),
        "edit" => EditCli.Run(args[1..]),
        "profile" => ProfileCli.Run(args[1..]),
        _ => ShellCli.Run(args),
    };

static int PrintVersion()
{
    AnsiConsole.WriteLine($"{SqlShellInfo.Name} {SqlShellInfo.Version}");
    return 0;
}

static int PrintRootHelp()
{
    const string body =
        "sqlshell — resilient SQL Server terminal client\n\n"
        + "Usage: sqlshell [-p PROFILE] [-f FILE] [-x]\n"
        + "       sqlshell edit [FILE.sql] [-p PROFILE] [--max-rows N]\n"
        + "       sqlshell profile <command> [name] [options]\n"
        + "       sqlshell doctor\n"
        + "       sqlshell --version";
    AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(body)}[/]");
    return 0;
}
