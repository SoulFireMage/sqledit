using Spectre.Console;
using SqlShell.Core.Profiles;
using Profile = SqlShell.Core.Profiles.Profile;

namespace SqlShell.Cli.Commands;

/// <summary>Interactive profile prompts and detail display, mirroring the Python CLI.</summary>
public static class ProfilePrompts
{
    public static (Profile Profile, string? Password) Add(IAnsiConsole console, string name)
    {
        var server = console.Prompt(new TextPrompt<string>("Server:"));
        var database = console.Prompt(new TextPrompt<string>("Database:").DefaultValue("master"));
        var auth = console
            .Prompt(new TextPrompt<string>("Authentication (sql/windows/entra):").DefaultValue("windows"))
            .ToLowerInvariant();

        string? username = null;
        string? password = null;
        if (auth == "sql")
        {
            username = console.Prompt(new TextPrompt<string>("Username:"));
            password = console.Prompt(new TextPrompt<string>("Password:").Secret());
        }

        var trust = PromptYesNo(console, "Trust server certificate without validation?", current: false);
        var profile = new Profile(
            name,
            server,
            Database: database,
            Auth: auth,
            Username: username,
            TrustServerCertificate: trust);
        profile.Validate();
        return (profile, password);
    }

    public static ProfileEdit InteractiveChanges(IAnsiConsole console, Profile profile)
    {
        console.WriteLine("Press Enter to keep the value shown as the default. Password is not changed here.");
        var auth = Text(console, "Authentication (sql/windows/entra)", profile.Auth).ToLowerInvariant();
        if (!Profile.AuthTypes.Contains(auth))
        {
            throw new ProfileException($"Unknown authentication type: {auth}");
        }

        var username = profile.Username;
        if (auth == "sql")
        {
            username = Text(console, "Username", profile.Username ?? string.Empty);
        }

        return new ProfileEdit
        {
            Server = Text(console, "Server", profile.Server),
            Database = Text(console, "Database", profile.Database),
            Auth = auth,
            Username = username,
            Driver = Text(console, "ODBC driver", profile.Driver),
            Encrypt = PromptYesNo(console, "Encrypt connection", profile.Encrypt),
            TrustServerCertificate = PromptYesNo(
                console, "Trust server certificate without validation", profile.TrustServerCertificate),
            LoginTimeout = Integer(console, "Login timeout seconds", profile.LoginTimeout),
            QueryTimeout = Integer(console, "Query timeout seconds (0 = unlimited)", profile.QueryTimeout),
        };
    }

    public static void Show(IAnsiConsole console, Profile profile, ProfileStore store)
    {
        var passwordStatus = "not used";
        if (profile.Auth == "sql")
        {
            passwordStatus = store.Password(profile) is not null ? "stored in credential manager" : "MISSING";
        }

        var rows = new (string Field, string Value)[]
        {
            ("Name", profile.Name),
            ("Server", profile.Server),
            ("Database", profile.Database),
            ("Authentication", profile.Auth),
            ("Username", profile.Username ?? "—"),
            ("Password", passwordStatus),
            ("ODBC driver", profile.Driver),
            ("Encrypt", profile.Encrypt ? "yes" : "no"),
            ("Trust server certificate", profile.TrustServerCertificate ? "yes" : "no"),
            ("Login timeout", $"{profile.LoginTimeout} seconds"),
            ("Query timeout", profile.QueryTimeout == 0 ? "unlimited" : $"{profile.QueryTimeout} seconds"),
        };

        var table = new Table().Title($"Profile: {Markup.Escape(profile.Name)}");
        table.AddColumn("Setting");
        table.AddColumn("Value");
        foreach (var (field, value) in rows)
        {
            table.AddRow(Markup.Escape(field), Markup.Escape(value));
        }

        console.Write(table);
        console.MarkupLine("[dim]Values are shown literally; square brackets are part of a server, database, or username value.[/]");
    }

    private static string Text(IAnsiConsole console, string label, string current)
        => console.Prompt(new TextPrompt<string>($"{label}:").DefaultValue(current));

    private static int Integer(IAnsiConsole console, string label, int current)
        => console.Prompt(new TextPrompt<int>($"{label}:").DefaultValue(current));

    private static bool PromptYesNo(IAnsiConsole console, string label, bool current)
    {
        var answer = console
            .Prompt(new TextPrompt<string>($"{label} ({(current ? "Y/n" : "y/N")}):").AllowEmpty())
            .Trim()
            .ToLowerInvariant();
        return answer switch
        {
            "" => current,
            "y" or "yes" => true,
            "n" or "no" => false,
            _ => throw new ProfileException($"Please answer yes or no for {label.ToLowerInvariant()}"),
        };
    }
}
