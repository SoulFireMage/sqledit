using System.Text.RegularExpressions;

namespace SqlShell.Core.Export;

/// <summary>A parsed <c>SELECT ... &gt; file.csv</c> redirection.</summary>
public sealed record Redirection(string Sql, string FilePath, bool Append);

/// <summary>Delimited result export and shell-style redirection parsing.</summary>
public static partial class RedirectionParser
{
    [GeneratedRegex(@"^(?<sql>.*\S)\s+(?<operator>>>?)\s+(?<path>""[^""]+""|\S+)\s*$", RegexOptions.Singleline)]
    private static partial Regex Pattern();

    public static readonly IReadOnlyDictionary<string, char> SupportedSuffixes =
        new Dictionary<string, char> { [".csv"] = ',', [".tsv"] = '\t' };

    /// <summary>Recognize a final &gt; file.csv/tsv without consuming SQL &gt; comparisons.</summary>
    public static Redirection? Parse(string line)
    {
        var match = Pattern().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var rawPath = match.Groups["path"].Value;
        if (rawPath.Length >= 2 && rawPath[0] == '"' && rawPath[^1] == '"')
        {
            rawPath = rawPath[1..^1];
        }

        var path = Normalize(ExpandUser(rawPath));
        if (!SupportedSuffixes.ContainsKey(Path.GetExtension(path).ToLowerInvariant()))
        {
            return null;
        }

        return new Redirection(
            match.Groups["sql"].Value.TrimEnd(),
            path,
            match.Groups["operator"].Value == ">>");
    }

    private static string ExpandUser(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        if (path.Length == 1 || path[1] is '/' or '\\')
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home + path[1..];
        }

        return path;
    }

    // pathlib normalizes forward slashes to the platform separator on Windows.
    private static string Normalize(string path)
        => Path.DirectorySeparatorChar == '/'
            ? path
            : path.Replace('/', Path.DirectorySeparatorChar);
}
