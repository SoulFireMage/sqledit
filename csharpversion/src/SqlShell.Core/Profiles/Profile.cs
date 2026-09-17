namespace SqlShell.Core.Profiles;

/// <summary>Connection profile metadata. Mirrors the Python sqlshell Profile dataclass.</summary>
public sealed record Profile(
    string Name,
    string Server,
    string Database = "master",
    string Auth = "windows",
    string? Username = null,
    string Driver = "ODBC Driver 18 for SQL Server",
    bool Encrypt = true,
    bool TrustServerCertificate = false,
    int LoginTimeout = 8,
    int QueryTimeout = 0)
{
    public static readonly string[] AuthTypes = ["sql", "windows", "entra"];

    public void Validate()
    {
        if (string.IsNullOrEmpty(Name) || Name.IndexOfAny(['[', ']', '\r', '\n']) >= 0)
        {
            throw new ProfileException("Profile name must be non-empty and cannot contain brackets or newlines");
        }

        if (string.IsNullOrEmpty(Server))
        {
            throw new ProfileException("Server is required");
        }

        if (!AuthTypes.Contains(Auth))
        {
            throw new ProfileException($"Unknown authentication type: {Auth}");
        }

        if (Auth == "sql" && string.IsNullOrEmpty(Username))
        {
            throw new ProfileException("SQL authentication requires a username");
        }

        if (LoginTimeout < 1 || QueryTimeout < 0)
        {
            throw new ProfileException("Timeouts cannot be negative (login timeout must be at least 1)");
        }
    }
}

/// <summary>Raised for invalid profile data or profile storage failures.</summary>
public sealed class ProfileException(string message) : Exception(message);
