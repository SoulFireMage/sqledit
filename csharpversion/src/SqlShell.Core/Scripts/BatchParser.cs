using System.Text.RegularExpressions;

namespace SqlShell.Core.Scripts;

/// <summary>SQL Server script batch parsing.</summary>
public static partial class BatchParser
{
    [GeneratedRegex(@"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    /// <summary>Split on sqlcmd-style GO lines, retaining semicolons inside batches.</summary>
    public static IReadOnlyList<string> SplitBatches(string script)
    {
        var batches = new List<string>();
        var current = new List<string>();
        foreach (var line in SplitLinesKeepEnds(script))
        {
            var match = GoLine().Match(line.TrimEnd('\r', '\n'));
            if (!match.Success)
            {
                current.Add(line);
                continue;
            }

            var batch = string.Concat(current).Trim();
            if (batch.Length > 0)
            {
                var repeat = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 1;
                for (var index = 0; index < repeat; index++)
                {
                    batches.Add(batch);
                }
            }

            current.Clear();
        }

        var trailing = string.Concat(current).Trim();
        if (trailing.Length > 0)
        {
            batches.Add(trailing);
        }

        return batches;
    }

    /// <summary>Return the GO-delimited batch containing a zero-based cursor line.</summary>
    public static string CurrentBatch(string script, int cursorLine)
    {
        var lines = SplitLines(script);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        cursorLine = Math.Max(0, Math.Min(cursorLine, lines.Count - 1));
        var start = 0;
        var end = lines.Count;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!GoLine().IsMatch(lines[index]))
            {
                continue;
            }

            if (index < cursorLine)
            {
                start = index + 1;
            }
            else
            {
                end = index;
                break;
            }
        }

        return string.Join("\n", lines.Skip(start).Take(end - start)).Trim();
    }

    private static List<string> SplitLines(string text)
        => SplitLinesKeepEnds(text).Select(line => line.TrimEnd('\r', '\n')).ToList();

    private static List<string> SplitLinesKeepEnds(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                lines.Add(text[start..(index + 1)]);
                start = index + 1;
            }
            else if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                lines.Add(text[start..(index + 1)]);
                start = index + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }
}
