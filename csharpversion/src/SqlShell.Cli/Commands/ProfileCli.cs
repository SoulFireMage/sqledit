using Spectre.Console;
using SqlShell.Core.Profiles;

namespace SqlShell.Cli.Commands;

/// <summary>The <c>profile</c> command group.</summary>
internal static class ProfileCli
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return 1;
            }

            var rest = args[1..];
            return args[0] switch
            {
                "add" => Add(rest),
                "list" => List(),
                "show" => Show(rest),
                "use" => Use(rest),
                "remove" => Remove(rest),
                "edit" => Edit(rest),
                "configure" => Configure(rest),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception exception) when (exception
            is ProfileException
            or IOException
            or ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return CommandErrors.Report(exception);
        }
    }

    private static int Add(string[] args)
    {
        var name = RequireName(Arguments.Parse(args));
        var store = new ProfileStore();
        var (profile, password) = ProfilePrompts.Add(AnsiConsole.Console, name);
        store.Save(profile, password);
        AnsiConsole.MarkupLine($"[green]Saved profile '{Markup.Escape(profile.Name)}'[/]");
        return 0;
    }

    private static int List()
    {
        var store = new ProfileStore();
        string? active = null;
        try
        {
            active = store.Get().Name;
        }
        catch (ProfileException)
        {
        }

        var table = new Table().AddColumns(string.Empty, "Name", "Server", "Database", "Auth");
        foreach (var item in store.List())
        {
            table.AddRow(
                item.Name == active ? "*" : string.Empty,
                Markup.Escape(item.Name),
                Markup.Escape(item.Server),
                Markup.Escape(item.Database),
                Markup.Escape(item.Auth));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static int Show(string[] args)
    {
        var name = RequireName(Arguments.Parse(args));
        var store = new ProfileStore();
        ProfilePrompts.Show(AnsiConsole.Console, store.Get(name), store);
        return 0;
    }

    private static int Use(string[] args)
    {
        var name = RequireName(Arguments.Parse(args));
        new ProfileStore().Use(name);
        AnsiConsole.MarkupLine($"[green]Active profile is now '{Markup.Escape(name)}'[/]");
        return 0;
    }

    private static int Remove(string[] args)
    {
        var name = RequireName(Arguments.Parse(args));
        new ProfileStore().Remove(name);
        AnsiConsole.MarkupLine($"[green]Removed profile '{Markup.Escape(name)}'[/]");
        return 0;
    }

    private static int Edit(string[] args)
    {
        var parsed = Arguments.Parse(
            args,
            flags:
            [
                "password",
                "encrypt",
                "no-encrypt",
                "trust-server-certificate",
                "validate-server-certificate",
            ],
            valued: ["server", "database", "username", "auth", "driver", "login-timeout", "query-timeout"]);
        var name = RequireName(parsed);
        var store = new ProfileStore();

        var changes = new ProfileEdit
        {
            Server = parsed.Value("server"),
            Database = parsed.Value("database"),
            Username = parsed.Value("username"),
            Auth = parsed.Value("auth"),
            Driver = parsed.Value("driver"),
            LoginTimeout = parsed.IntValue("login-timeout"),
            QueryTimeout = parsed.IntValue("query-timeout"),
            Encrypt = ResolveEncrypt(parsed.Flag("encrypt"), parsed.Flag("no-encrypt")),
            TrustServerCertificate = ResolveTrust(
                parsed.Flag("trust-server-certificate"), parsed.Flag("validate-server-certificate")),
        };

        if (parsed.Flag("password"))
        {
            var first = AnsiConsole.Prompt(new TextPrompt<string>("New password:").Secret());
            var confirmation = AnsiConsole.Prompt(new TextPrompt<string>("Confirm new password:").Secret());
            if (first != confirmation)
            {
                throw new ProfileException("Passwords do not match; the profile was not changed");
            }

            changes = changes with { Password = first };
        }
        else if (HasNoChanges(changes))
        {
            changes = ProfilePrompts.InteractiveChanges(AnsiConsole.Console, store.Get(name));
        }

        var profile = store.Edit(name, changes);
        AnsiConsole.MarkupLine($"[green]Updated profile '{Markup.Escape(profile.Name)}'[/]");
        ProfilePrompts.Show(AnsiConsole.Console, profile, store);
        return 0;
    }

    private static int Configure(string[] args)
    {
        var parsed = Arguments.Parse(
            args, flags: ["trust-server-certificate", "validate-server-certificate"]);
        var name = RequireName(parsed);
        if (parsed.Flag("trust-server-certificate") == parsed.Flag("validate-server-certificate"))
        {
            throw new ProfileException(
                "Specify exactly one of --trust-server-certificate or --validate-server-certificate");
        }

        var store = new ProfileStore();
        var profile = store.ConfigureSecurity(name, trustServerCertificate: parsed.Flag("trust-server-certificate"));
        var state = profile.TrustServerCertificate
            ? "trusted without validation"
            : "validated against the OS trust store";
        var color = profile.TrustServerCertificate ? "yellow" : "green";
        AnsiConsole.MarkupLine(
            $"[{color}]Updated '{Markup.Escape(profile.Name)}': TLS remains enabled; "
            + $"the server certificate is now {state}.[/]");
        return 0;
    }

    private static bool? ResolveEncrypt(bool encrypt, bool noEncrypt)
    {
        if (encrypt && noEncrypt)
        {
            throw new ProfileException("Specify either --encrypt or --no-encrypt, not both");
        }

        return encrypt ? true : noEncrypt ? false : null;
    }

    private static bool? ResolveTrust(bool trust, bool validate)
    {
        if (trust && validate)
        {
            throw new ProfileException(
                "Specify either --trust-server-certificate or --validate-server-certificate, not both");
        }

        return trust ? true : validate ? false : null;
    }

    internal static bool HasNoChanges(ProfileEdit edit)
        => edit.Server is null
            && edit.Database is null
            && edit.Username is null
            && edit.Auth is null
            && edit.Driver is null
            && edit.LoginTimeout is null
            && edit.QueryTimeout is null
            && edit.Encrypt is null
            && edit.TrustServerCertificate is null;

    private static string RequireName(Arguments parsed)
    {
        var name = parsed.Positional(0);
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A profile name is required");
        }

        return name;
    }

    private static int Unknown(string subcommand)
    {
        AnsiConsole.MarkupLine($"[bold red]Unknown profile subcommand: {Markup.Escape(subcommand)}[/]");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        const string body =
            "Usage: sqlshell profile <command> [name] [options]\n"
            + "  add <name>        Create a profile interactively\n"
            + "  list              List saved profiles\n"
            + "  show <name>       Show non-secret profile details\n"
            + "  use <name>        Select the active profile\n"
            + "  remove <name>     Remove a profile\n"
            + "  edit <name>       Edit metadata or --password\n"
            + "  configure <name>  --trust-server-certificate | --validate-server-certificate";
        AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(body)}[/]");
    }
}
