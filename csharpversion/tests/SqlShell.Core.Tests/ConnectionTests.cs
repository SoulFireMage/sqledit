using SqlShell.Core.Connection;
using SqlShell.Core.Profiles;

namespace SqlShell.Core.Tests;

public class ConnectionTests
{
    private sealed class PasswordStore : IProfilePasswordSource
    {
        public string? Password(Profile profile) => null;
    }

    private sealed class FakeCursor : IDbCursor
    {
        public IReadOnlyList<string>? Description => new[] { "answer" };

        public int RowCount => 1;

        public void Execute(string sql)
        {
        }

        public IReadOnlyList<object?[]> FetchAll() => new List<object?[]> { new object?[] { 42 } };

        public IReadOnlyList<object?[]> FetchMany(int size)
        {
            var rows = new List<object?[]>();
            for (var number = 0; number < Math.Min(size, 3); number++)
            {
                rows.Add(new object?[] { number });
            }

            return rows;
        }

        public bool NextResult() => false;

        public void Dispose()
        {
        }
    }

    private class FakeConnection : IDbConnectionHandle
    {
        private readonly bool _failOnCursor;

        public FakeConnection(bool failOnCursor = false) => _failOnCursor = failOnCursor;

        public bool Closed { get; private set; }

        public int? Timeout { get; private set; }

        public virtual IDbCursor CreateCursor()
            => _failOnCursor ? throw new InvalidOperationException("08S01 link failure") : new FakeCursor();

        public void SetQueryTimeout(int seconds) => Timeout = seconds;

        public void Dispose() => Closed = true;
    }

    private sealed class BadCursorConnection : FakeConnection
    {
        public override IDbCursor CreateCursor()
            => throw new InvalidOperationException("42000 syntax error");
    }

    private sealed class FakeFactory(params FakeConnection[] connections) : IConnectionFactory
    {
        private readonly Queue<FakeConnection> _connections = new(connections);

        public List<string> ConnectionStrings { get; } = [];

        public IDbConnectionHandle Open(string connectionString)
        {
            ConnectionStrings.Add(connectionString);
            return _connections.Dequeue();
        }
    }

    private sealed class DelegateFactory(Func<string, IDbConnectionHandle> open) : IConnectionFactory
    {
        public IDbConnectionHandle Open(string connectionString) => open(connectionString);
    }

    [Fact]
    public void Connection_error_detection()
    {
        Assert.True(ConnectionErrors.IsConnectionError(new InvalidOperationException("08S01 link failure")));
        Assert.False(ConnectionErrors.IsConnectionError(new InvalidOperationException("42000 syntax")));
    }

    [Fact]
    public void Reconnects_and_retries_once()
    {
        var factory = new FakeFactory(new FakeConnection(failOnCursor: true), new FakeConnection());
        var events = new List<(string Kind, string Message)>();
        var manager = new ConnectionManager(new PasswordStore(), (kind, message) => events.Add((kind, message)), factory);

        manager.Connect(new Profile("work", "server"));
        var result = manager.Execute("SELECT 42");

        Assert.Equal(new object?[] { 42 }, result[0].Rows[0]);
        Assert.Equal(2, events.Count(item => item.Kind == "reconnect"));
    }

    [Fact]
    public void Query_timeout_is_set_on_connection_not_cursor()
    {
        var connection = new FakeConnection();
        var manager = new ConnectionManager(new PasswordStore(), null, new FakeFactory(connection));

        manager.Connect(new Profile("work", "server", QueryTimeout: 37));

        Assert.Equal(37, connection.Timeout);
        Assert.Equal(new object?[] { 42 }, manager.Execute("SELECT 42")[0].Rows[0]);
    }

    [Fact]
    public void Editor_result_limit_marks_truncation()
    {
        var manager = new ConnectionManager(new PasswordStore(), null, new FakeFactory(new FakeConnection()));
        manager.Connect(new Profile("work", "server"));

        var result = manager.Execute("SELECT lots", maxRows: 2)[0];

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { 0 }, result.Rows[0]);
        Assert.Equal(new object?[] { 1 }, result.Rows[1]);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Does_not_retry_sql_errors()
    {
        var factory = new FakeFactory(new BadCursorConnection());
        var manager = new ConnectionManager(new PasswordStore(), null, factory);
        manager.Connect(new Profile("work", "server"));

        Assert.Throws<InvalidOperationException>(() => manager.Execute("BAD SQL"));
        Assert.Single(factory.ConnectionStrings);
    }

    [Fact]
    public void Certificate_error_has_profile_specific_fix()
    {
        var factory = new DelegateFactory(_ => throw new InvalidOperationException(
            "08001 SSL Provider: The certificate chain was issued by an authority that is not trusted."));
        var manager = new ConnectionManager(new PasswordStore(), null, factory);

        var exception = Assert.Throws<CertificateValidationException>(
            () => manager.Connect(new Profile("work", "server")));
        Assert.Contains("profile configure work --trust-server-certificate", exception.Message);
    }

    [Fact]
    public void Login_error_has_profile_inspection_guidance()
    {
        var factory = new DelegateFactory(_ => throw new InvalidOperationException(
            "28000 Login failed for user '[someone]'. (18456)"));
        var manager = new ConnectionManager(new PasswordStore(), null, factory);

        var exception = Assert.Throws<AuthenticationException>(
            () => manager.Connect(new Profile("work", "server")));
        Assert.Contains("profile show work", exception.Message);
        Assert.Contains("square brackets", exception.Message);
    }
}
