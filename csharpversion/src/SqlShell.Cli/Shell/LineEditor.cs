using System.Text;

namespace SqlShell.Cli.Shell;

/// <summary>
/// A small interactive line editor with persistent history, cursor editing,
/// inline history suggestions, and Tab completion. Falls back to
/// <see cref="Console.ReadLine"/> when input is redirected.
/// </summary>
public sealed class LineEditor
{
    private const string ClearToEnd = "\u001b[K";
    private const string Dim = "\u001b[90m";
    private const string Reset = "\u001b[0m";

    private readonly string _historyPath;
    private readonly List<string> _history = [];

    public LineEditor(string historyPath)
    {
        _historyPath = historyPath;
        Load();
    }

    public IReadOnlyList<string> History => _history;

    public string? Read(string prompt, IReadOnlyList<string> completions)
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var buffer = string.Empty;
        var cursor = 0;
        var historyIndex = _history.Count;
        Console.Write(prompt);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    if (buffer.Trim().Length > 0)
                    {
                        Remember(buffer);
                    }

                    return buffer;

                case ConsoleKey.Backspace:
                    if (cursor > 0)
                    {
                        buffer = buffer.Remove(cursor - 1, 1);
                        cursor--;
                    }

                    break;

                case ConsoleKey.Delete:
                    if (cursor < buffer.Length)
                    {
                        buffer = buffer.Remove(cursor, 1);
                    }

                    break;

                case ConsoleKey.LeftArrow:
                    cursor = Math.Max(0, cursor - 1);
                    break;

                case ConsoleKey.RightArrow:
                    cursor = Math.Min(buffer.Length, cursor + 1);
                    break;

                case ConsoleKey.Home:
                    cursor = 0;
                    break;

                case ConsoleKey.End:
                    cursor = buffer.Length;
                    break;

                case ConsoleKey.UpArrow:
                    if (historyIndex > 0)
                    {
                        historyIndex--;
                        buffer = _history[historyIndex];
                        cursor = buffer.Length;
                    }

                    break;

                case ConsoleKey.DownArrow:
                    if (historyIndex < _history.Count - 1)
                    {
                        historyIndex++;
                        buffer = _history[historyIndex];
                    }
                    else
                    {
                        historyIndex = _history.Count;
                        buffer = string.Empty;
                    }

                    cursor = buffer.Length;
                    break;

                case ConsoleKey.Tab:
                    buffer = Complete(buffer, completions);
                    cursor = buffer.Length;
                    break;

                case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control) && buffer.Length == 0:
                    Console.WriteLine();
                    return null;

                default:
                    if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0')
                    {
                        buffer = buffer.Insert(cursor, key.KeyChar.ToString());
                        cursor++;
                    }

                    break;
            }

            Redraw(prompt, buffer, cursor);
        }
    }

    private void Redraw(string prompt, string buffer, int cursor)
    {
        var suggestion = cursor == buffer.Length ? Suggest(buffer) : null;
        Console.Write('\r');
        Console.Write(prompt);
        Console.Write(buffer);
        var trailing = 0;
        if (suggestion is not null)
        {
            var remainder = suggestion[buffer.Length..];
            Console.Write(Dim);
            Console.Write(remainder);
            Console.Write(Reset);
            trailing = remainder.Length;
        }

        Console.Write(ClearToEnd);
        var back = trailing + (buffer.Length - cursor);
        if (back > 0)
        {
            Console.Write($"\u001b[{back}D");
        }
    }

    private string? Suggest(string buffer)
    {
        if (buffer.Length == 0)
        {
            return null;
        }

        for (var index = _history.Count - 1; index >= 0; index--)
        {
            var candidate = _history[index];
            if (candidate.Length > buffer.Length
                && candidate.StartsWith(buffer, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Complete(string buffer, IReadOnlyList<string> completions)
    {
        if (!buffer.StartsWith('.') || buffer.Contains(' '))
        {
            return buffer;
        }

        var matches = completions
            .Where(command => command.StartsWith(buffer, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            return buffer;
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        var prefix = matches[0];
        foreach (var match in matches)
        {
            while (!match.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                prefix = prefix[..^1];
            }
        }

        return prefix;
    }

    private void Remember(string line)
    {
        if (_history.Count > 0 && _history[^1] == line)
        {
            return;
        }

        _history.Add(line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_historyPath))!);
            File.AppendAllText(_historyPath, line + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (IOException)
        {
            // History persistence is best-effort.
        }
    }

    private void Load()
    {
        if (!File.Exists(_historyPath))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadAllLines(_historyPath))
            {
                if (line.Length > 0)
                {
                    _history.Add(line);
                }
            }
        }
        catch (IOException)
        {
            // History persistence is best-effort.
        }
    }
}
