using SqlShell.Core.Connection;
using Terminal.Gui.Views;

namespace SqlShell.Ide;

/// <summary>Adapts a <see cref="ResultSet"/> to the Terminal.Gui <see cref="ITableSource"/> contract.</summary>
public sealed class ResultTableSource : ITableSource
{
    private readonly ResultSet _result;

    public ResultTableSource(ResultSet result) => _result = result;

    public int Rows => _result.Rows.Count;

    public int Columns => _result.Columns.Count;

    public string[] ColumnNames => _result.Columns.ToArray();

    public object this[int row, int col]
    {
        get
        {
            var value = _result.Rows[row][col];
            return value is byte[] bytes
                ? "0x" + Convert.ToHexString(bytes).ToLowerInvariant()
                : value!;
        }
    }
}
