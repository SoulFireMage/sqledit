namespace SqlShell.Core.Connection;

/// <summary>A single result set returned by a query.</summary>
public sealed class ResultSet(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    int RowCount = -1,
    bool Truncated = false)
{
    public IReadOnlyList<string> Columns { get; } = Columns;

    public IReadOnlyList<object?[]> Rows { get; } = Rows;

    public int RowCount { get; } = RowCount;

    public bool Truncated { get; } = Truncated;
}
