namespace SqlShell.Ide;

/// <summary>Case-insensitive incremental search with wrap-around, mirroring the Python find-next behaviour.</summary>
public sealed class FindState
{
    public string Text { get; set; } = string.Empty;

    public (int Index, int Length)? FindNext(string content, int startIndex)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return null;
        }

        var haystack = content.ToLowerInvariant();
        var needle = Text.ToLowerInvariant();
        var from = Math.Clamp(startIndex, 0, haystack.Length);

        var index = haystack.IndexOf(needle, from, StringComparison.Ordinal);
        if (index < 0 && from > 0)
        {
            index = haystack.IndexOf(needle, 0, from, StringComparison.Ordinal);
        }

        return index < 0 ? null : (index, Text.Length);
    }
}
