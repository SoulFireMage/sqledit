using Microsoft.Data.SqlClient;
using SqlShell.Core.Profiles;

namespace SqlShell.Core.Connection;

/// <summary>Builds the Microsoft.Data.SqlClient connection string for a profile.</summary>
public static class ConnectionStringFactory
{
    public static string Build(Profile profile, string? password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = profile.Server,
            InitialCatalog = profile.Database,
            Encrypt = profile.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = profile.TrustServerCertificate,
            ApplicationName = "sqlshell",
            ConnectTimeout = profile.LoginTimeout,
        };

        switch (profile.Auth)
        {
            case "windows":
                builder.IntegratedSecurity = true;
                break;
            case "entra":
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrEmpty(profile.Username))
                {
                    builder.UserID = profile.Username;
                }

                break;
            default:
                if (password is null)
                {
                    throw new InvalidOperationException(
                        $"No stored password found for SQL profile '{profile.Name}'");
                }

                builder.UserID = profile.Username ?? string.Empty;
                builder.Password = password;
                break;
        }

        return builder.ConnectionString;
    }
}
