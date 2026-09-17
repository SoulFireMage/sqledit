namespace SqlShell.Core.Connection;

/// <summary>A forward cursor over one or more result sets, abstracted for testability.</summary>
public interface IDbCursor : IDisposable
{
    void Execute(string sql);

    /// <summary>Column names for the current result set, or null when it has no columns.</summary>
    IReadOnlyList<string>? Description { get; }

    IReadOnlyList<object?[]> FetchAll();

    IReadOnlyList<object?[]> FetchMany(int size);

    int RowCount { get; }

    bool NextResult();
}

/// <summary>An open connection to a database, abstracted for testability.</summary>
public interface IDbConnectionHandle : IDisposable
{
    IDbCursor CreateCursor();

    void SetQueryTimeout(int seconds);
}

/// <summary>Creates connection handles from a finished connection string.</summary>
public interface IConnectionFactory
{
    IDbConnectionHandle Open(string connectionString);
}
