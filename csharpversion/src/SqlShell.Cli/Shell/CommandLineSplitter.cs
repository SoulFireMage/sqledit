namespace SqlShell.Cli.Shell;

/// <summary>Expands a leading <c>~</c> in a user-supplied path.</summary>
internal static class UserPath
{
    public static string Expand(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        if (path.Length == 1 || path[1] is '/' or '\\')
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        }

        return path;
    }
}

/// <summary>Whitespace tokenizer for dot-commands, matching shlex non-POSIX behaviour.</summary>
internal static class CommandLineSplitter
{
    public static IReadOnlyList<string> Split(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inToken = false;
        var quote = '\0';

        foreach (var character in line)
        {
            if (quote != '\0')
            {
                current.Append(character);
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
                current.Append(character);
                inToken = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(character);
                inToken = true;
            }
        }

        if (quote != '\0')
        {
            throw new FormatException("No closing quotation");
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
