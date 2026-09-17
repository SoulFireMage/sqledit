using Microsoft.Data.SqlClient;

namespace SqlShell.Core.Connection;

/// <summary>Classifies SQL Server exceptions as transient, certificate, or login failures.</summary>
public static class ConnectionErrors
{
    // ODBC SQLSTATEs treated as transient by the Python implementation. Retained as a
    // message-token fallback so wrapped or non-SqlClient errors classify consistently.
    private static readonly string[] ConnectionStates =
        ["08S01", "08001", "08003", "08004", "08007", "08S02", "HYT00", "HYT01"];

    private static readonly int[] TransientNumbers =
        [0, 20, 64, 233, 10053, 10054, 10060, 10928, 10929, 40197, 40501, 40613];

    public static bool IsConnectionError(Exception error)
    {
        if (error is SqlException sql)
        {
            if (sql.IsTransient)
            {
                return true;
            }

            foreach (SqlError item in sql.Errors)
            {
                if (TransientNumbers.Contains(item.Number))
                {
                    return true;
                }
            }
        }

        var text = Messages(error).ToUpperInvariant();
        return ConnectionStates.Any(state => text.Contains(state, StringComparison.Ordinal));
    }

    public static bool IsCertificateValidationError(Exception error)
        => Messages(error).Contains(
            "certificate chain was issued by an authority that is not trusted",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsAuthenticationError(Exception error)
    {
        if (error is SqlException sql && sql.Number == 18456)
        {
            return true;
        }

        var text = Messages(error).ToLowerInvariant();
        return text.Contains("login failed for user") || text.Contains("18456");
    }

    private static string Messages(Exception error)
    {
        var parts = new List<string>();
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrEmpty(current.Message))
            {
                parts.Add(current.Message);
            }
        }

        return string.Join(" ", parts);
    }
}

/// <summary>Raised with actionable guidance for an untrusted SQL Server certificate.</summary>
public sealed class CertificateValidationException(string message) : Exception(message);

/// <summary>Raised with profile inspection guidance after SQL authentication fails.</summary>
public sealed class AuthenticationException(string message) : Exception(message);
