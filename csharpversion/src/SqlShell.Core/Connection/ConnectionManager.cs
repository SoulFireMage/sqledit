using SqlShell.Core.Profiles;

namespace SqlShell.Core.Connection;

/// <summary>Persistent, retrying connection lifecycle.</summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly IProfilePasswordSource _store;
    private readonly IConnectionFactory _factory;

    public ConnectionManager(
        IProfilePasswordSource store,
        Action<string, string>? @event = null,
        IConnectionFactory? factory = null)
    {
        _store = store;
        Event = @event ?? ((_, _) => { });
        _factory = factory ?? new SqlClientConnectionFactory();
    }

    /// <summary>Receives connection lifecycle messages. Reassignable so a UI can redirect them.</summary>
    public Action<string, string> Event { get; set; }

    public Profile? Profile { get; private set; }

    public IDbConnectionHandle? Connection { get; private set; }

    public void Connect(Profile profile)
    {
        Close();
        var connectionString = ConnectionStringFactory.Build(profile, _store.Password(profile));
        try
        {
            Connection = _factory.Open(connectionString);
        }
        catch (Exception exception)
        {
            if (ConnectionErrors.IsCertificateValidationError(exception))
            {
                throw new CertificateValidationException(
                    "SQL Server presented a certificate that Windows does not trust. "
                    + "Prefer installing its issuing CA certificate. For a server you trust, keep TLS encryption "
                    + "and bypass certificate validation with: "
                    + $"sqlshell profile configure {profile.Name} --trust-server-certificate");
            }

            if (ConnectionErrors.IsAuthenticationError(exception))
            {
                throw new AuthenticationException(
                    $"SQL Server rejected the login for profile '{profile.Name}'. "
                    + "Usernames are literal: do not include decorative square brackets. "
                    + $"Review the profile with 'sqlshell profile show {profile.Name}', "
                    + $"correct it with 'sqlshell profile edit {profile.Name}', or replace only "
                    + $"the password with 'sqlshell profile edit {profile.Name} --password'.");
            }

            throw;
        }

        Connection.SetQueryTimeout(profile.QueryTimeout);
        Profile = profile;
        Event("connected", $"Connected to {profile.Server}/{profile.Database} as profile '{profile.Name}'");
    }

    public void Reconnect()
    {
        if (Profile is null)
        {
            throw new InvalidOperationException("No profile is connected");
        }

        var profile = Profile;
        Event("reconnect", "Connection was lost; reconnecting and retrying once...");
        Connect(profile);
        Event("reconnect", "Reconnected successfully");
    }

    public IReadOnlyList<ResultSet> Execute(string sql, int? maxRows = null)
    {
        if (Connection is null)
        {
            throw new InvalidOperationException("Not connected");
        }

        try
        {
            return ExecuteOnce(sql, maxRows);
        }
        catch (Exception exception) when (ConnectionErrors.IsConnectionError(exception))
        {
            Reconnect();
            return ExecuteOnce(sql, maxRows);
        }
    }

    private IReadOnlyList<ResultSet> ExecuteOnce(string sql, int? maxRows)
    {
        var cursor = Connection!.CreateCursor();
        var results = new List<ResultSet>();
        try
        {
            cursor.Execute(sql);
            while (true)
            {
                if (cursor.Description is { } columns)
                {
                    IReadOnlyList<object?[]> rows;
                    bool truncated;
                    if (maxRows is null)
                    {
                        rows = cursor.FetchAll();
                        truncated = false;
                    }
                    else
                    {
                        var fetched = cursor.FetchMany(maxRows.Value + 1);
                        truncated = fetched.Count > maxRows.Value;
                        rows = fetched.Take(maxRows.Value).ToList();
                    }

                    results.Add(new ResultSet(columns, rows, cursor.RowCount, truncated));
                }
                else
                {
                    results.Add(new ResultSet([], [], cursor.RowCount));
                }

                if (!cursor.NextResult())
                {
                    break;
                }
            }

            return results;
        }
        finally
        {
            cursor.Dispose();
        }
    }

    public void Close()
    {
        if (Connection is not null)
        {
            try
            {
                Connection.Dispose();
            }
            finally
            {
                Connection = null;
            }
        }
    }

    public void Dispose() => Close();
}
