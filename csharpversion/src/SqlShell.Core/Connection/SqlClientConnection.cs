using Microsoft.Data.SqlClient;

namespace SqlShell.Core.Connection;

/// <summary>Default <see cref="IConnectionFactory"/> backed by Microsoft.Data.SqlClient.</summary>
public sealed class SqlClientConnectionFactory : IConnectionFactory
{
    public IDbConnectionHandle Open(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            connection.Open();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new SqlConnectionHandle(connection);
    }
}

internal sealed class SqlConnectionHandle(SqlConnection connection) : IDbConnectionHandle
{
    private int _queryTimeout;

    public IDbCursor CreateCursor() => new SqlCursor(connection, _queryTimeout);

    public void SetQueryTimeout(int seconds) => _queryTimeout = seconds;

    public void Dispose() => connection.Dispose();
}

internal sealed class SqlCursor(SqlConnection connection, int queryTimeout) : IDbCursor
{
    private SqlDataReader? _reader;

    public IReadOnlyList<string>? Description
    {
        get
        {
            if (_reader is null || _reader.FieldCount == 0)
            {
                return null;
            }

            var names = new string[_reader.FieldCount];
            for (var index = 0; index < names.Length; index++)
            {
                names[index] = _reader.GetName(index);
            }

            return names;
        }
    }

    public int RowCount => _reader?.RecordsAffected ?? -1;

    public void Execute(string sql)
    {
        _reader?.Dispose();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = queryTimeout;
        _reader = command.ExecuteReader();
    }

    public IReadOnlyList<object?[]> FetchAll()
    {
        var rows = new List<object?[]>();
        while (ReadRow(out var row))
        {
            rows.Add(row);
        }

        return rows;
    }

    public IReadOnlyList<object?[]> FetchMany(int size)
    {
        var rows = new List<object?[]>(size);
        while (rows.Count < size && ReadRow(out var row))
        {
            rows.Add(row);
        }

        return rows;
    }

    public bool NextResult() => _reader?.NextResult() ?? false;

    private bool ReadRow(out object?[] row)
    {
        if (_reader is null || !_reader.Read())
        {
            row = [];
            return false;
        }

        row = new object?[_reader.FieldCount];
        for (var index = 0; index < row.Length; index++)
        {
            var value = _reader.GetValue(index);
            row[index] = value is DBNull ? null : value;
        }

        return true;
    }

    public void Dispose() => _reader?.Dispose();
}
