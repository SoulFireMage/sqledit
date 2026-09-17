using System.Globalization;

namespace SqlShell.Cli.Commands;

/// <summary>Minimal option parser supporting <c>--name value</c>, <c>--name=value</c>, and aliases.</summary>
internal sealed class Arguments
{
    private readonly List<string> _positionals = [];
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases;
    private readonly HashSet<string> _flags;
    private readonly HashSet<string> _valued;

    private Arguments(Dictionary<string, string> aliases, IEnumerable<string> flags, IEnumerable<string> valued)
    {
        _aliases = aliases;
        _flags = new HashSet<string>(flags, StringComparer.Ordinal);
        _valued = new HashSet<string>(valued, StringComparer.Ordinal);
    }

    public static Arguments Parse(
        string[] args,
        Dictionary<string, string>? aliases = null,
        string[]? flags = null,
        string[]? valued = null)
    {
        var result = new Arguments(aliases ?? [], flags ?? [], valued ?? []);
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (token == "--")
            {
                result._positionals.AddRange(args[(index + 1)..]);
                break;
            }

            if (token.Length > 1 && token[0] == '-')
            {
                var name = token;
                string? inline = null;
                var equals = token.IndexOf('=');
                if (equals >= 0)
                {
                    name = token[..equals];
                    inline = token[(equals + 1)..];
                }

                var canonical = result._aliases.TryGetValue(name, out var alias) ? alias : name.TrimStart('-');
                if (result._flags.Contains(canonical))
                {
                    result._options[canonical] = "true";
                }
                else if (result._valued.Contains(canonical))
                {
                    var value = inline;
                    if (value is null)
                    {
                        if (index + 1 >= args.Length)
                        {
                            throw new ArgumentException($"Missing value for {name}");
                        }

                        value = args[++index];
                    }

                    result._options[canonical] = value;
                }
                else
                {
                    throw new ArgumentException($"Unrecognized option: {name}");
                }
            }
            else
            {
                result._positionals.Add(token);
            }
        }

        return result;
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public bool Flag(string name) => _options.ContainsKey(name);

    public string? Value(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public int? IntValue(string name)
        => Value(name) is { } text
            ? int.Parse(text, CultureInfo.InvariantCulture)
            : null;

    public string? Positional(int index) => index < _positionals.Count ? _positionals[index] : null;
}
