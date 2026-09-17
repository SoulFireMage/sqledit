using System.Globalization;
using System.Text;
using SqlShell.Core.Connection;

namespace SqlShell.Core.Export;

/// <summary>Writes a single tabular result set to CSV or TSV.</summary>
public static class ResultExporter
{
    /// <summary>Write the single tabular result set to CSV/TSV and return its row count.</summary>
    public static int Export(IEnumerable<ResultSet> results, string path, bool append = false)
    {
        var tabular = results.Where(result => result.Columns.Count > 0).ToList();
        if (tabular.Count == 0)
        {
            throw new ArgumentException("The query did not return a tabular result set to export");
        }

        if (tabular.Count > 1)
        {
            throw new ArgumentException("The query returned multiple result sets; export one SELECT at a time");
        }

        var suffix = Path.GetExtension(path).ToLowerInvariant();
        if (!RedirectionParser.SupportedSuffixes.TryGetValue(suffix, out var delimiter))
        {
            throw new ArgumentException("Output filename must end in .csv or .tsv");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var exists = File.Exists(path);
        var length = exists ? new FileInfo(path).Length : 0;
        var writeHeader = !append || !exists || length == 0;
        // utf-8-sig makes newly-created exports open cleanly in Excel. When appending,
        // utf-8 avoids inserting another BOM into an existing file.
        var emitBom = !(append && exists && length > 0);

        var result = tabular[0];
        using var stream = new FileStream(
            path,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(emitBom)) { NewLine = "\r\n" };
        if (writeHeader)
        {
            writer.Write(FormatLine(result.Columns, delimiter));
        }

        foreach (var row in result.Rows)
        {
            writer.Write(FormatLine(row.Select(FormatValue).ToList(), delimiter));
        }

        return result.Rows.Count;
    }

    public static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        byte[] bytes => "0x" + Convert.ToHexString(bytes).ToLowerInvariant(),
        string text => text,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string FormatLine(IReadOnlyList<string> fields, char delimiter)
        => string.Join(delimiter.ToString(), fields.Select(field => EscapeField(field, delimiter))) + "\r\n";

    private static string EscapeField(string value, char delimiter)
    {
        if (value.IndexOfAny([delimiter, '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
