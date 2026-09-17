using System.Text;
using Spectre.Console;
using SqlShell.Cli.Rendering;
using SqlShell.Core;
using SqlShell.Core.Connection;
using SqlShell.Core.Export;
using SqlShell.Core.Profiles;
using SqlShell.Core.Scripts;

namespace SqlShell.Cli.Shell;

/// <summary>Interactive SQL REPL over a live, resilient connection.</summary>
public sealed class SqlShellRepl
{
    public static readonly string[] Commands =
        [".connect", ".run", ".save", ".edit", ".history", ".help", ".exit"];

    private readonly ConnectionManager _manager;
    private readonly ProfileStore _store;
    private readonly ResultRenderer _renderer;
    private readonly LineEditor _editor;
    private string? _lastSql;

    public SqlShellRepl(ConnectionManager manager, ProfileStore store, ResultRenderer renderer)
    {
        _manager = manager;
        _store = store;
        _renderer = renderer;
        _editor = new LineEditor(Path.Combine(store.ConfigDir, "history"));
    }

    public void Run()
    {
        _renderer.Console.MarkupLine(
            $"[bold cyan]{SqlShellInfo.Name} {SqlShellInfo.Version} — .help for commands[/]");

        while (true)
        {
            var line = _editor.Read("sql> ", Commands);
            if (line is null)
            {
                break;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('.'))
            {
                if (!Command(line))
                {
                    break;
                }
            }
            else
            {
                var redirection = RedirectionParser.Parse(line);
                if (redirection is not null)
                {
                    ExecuteSql(redirection.Sql, redirection.FilePath, redirection.Append);
                }
                else
                {
                    ExecuteSql(line);
                }
            }
        }
    }

    public bool ExecuteSql(string sql, string? outputPath = null, bool append = false)
    {
        _lastSql = sql;
        try
        {
            var results = _renderer.WithStatus("Running query...", () => _manager.Execute(sql));
            if (outputPath is null)
            {
                _renderer.Results(results);
            }
            else
            {
                var rowCount = ResultExporter.Export(results, outputPath, append);
                var action = append ? "Appended" : "Wrote";
                _renderer.Event("connected", $"{action} {rowCount:N0} row(s) to {Path.GetFullPath(outputPath)}");
            }

            return true;
        }
        catch (Exception exception)
        {
            _renderer.Error(exception);
            return false;
        }
    }

    public bool RunFile(string filename)
    {
        var path = UserPath.Expand(filename);
        string script;
        try
        {
            script = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _renderer.Error(exception);
            return false;
        }

        var batches = BatchParser.SplitBatches(script);
        if (batches.Count == 0)
        {
            _renderer.Event("error", $"No SQL found in {path}");
            return false;
        }

        var ok = true;
        for (var index = 0; index < batches.Count; index++)
        {
            _renderer.Console.Write(new Rule($"Batch {index + 1}/{batches.Count}"));
            ok = ExecuteSql(batches[index]) && ok;
        }

        return ok;
    }

    private bool Command(string line)
    {
        IReadOnlyList<string> parts;
        try
        {
            parts = CommandLineSplitter.Split(line);
        }
        catch (FormatException exception)
        {
            _renderer.Error(exception);
            return true;
        }

        var command = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).Select(argument => argument.Trim('"')).ToArray();
        try
        {
            switch (command)
            {
                case ".exit":
                    return false;
                case ".help":
                    PrintHelp();
                    break;
                case ".connect":
                    _manager.Connect(_store.Get(OneArg(command, args)));
                    break;
                case ".run":
                    RunFile(OneArg(command, args));
                    break;
                case ".save":
                    if (_lastSql is null)
                    {
                        throw new InvalidOperationException("There is no previous query to save");
                    }

                    File.WriteAllText(
                        UserPath.Expand(OneArg(command, args)),
                        _lastSql + "\n",
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    _renderer.Event("connected", "Query saved");
                    break;
                case ".edit":
                    if (args.Length > 1)
                    {
                        throw new InvalidOperationException("Usage: .edit [FILE.sql]");
                    }

                    _renderer.Event(
                        "error", "The full-screen IDE is not implemented yet in the C# port");
                    break;
                case ".history":
                    var history = _editor.History;
                    for (var index = 0; index < history.Count; index++)
                    {
                        _renderer.Console.WriteLine($"{index + 1,4}  {history[index]}");
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unknown command: {command} (use .help)");
            }
        }
        catch (Exception exception) when (exception
            is IOException
            or ProfileException
            or InvalidOperationException
            or FormatException
            or AuthenticationException
            or CertificateValidationException)
        {
            _renderer.Error(exception);
        }

        return true;
    }

    private void PrintHelp()
    {
        const string body =
            ".connect PROFILE\n"
            + ".run FILE.sql\n"
            + ".save FILE.sql\n"
            + ".history\n"
            + ".exit\n\n"
            + ".edit [FILE.sql] — launch the full-screen editor\n"
            + "Export: SELECT ... > results.csv\n"
            + "Append: SELECT ... >> results.csv\n"
            + "Use a quoted filename when it contains spaces. CSV and TSV are supported.";
        _renderer.Console.MarkupLine($"[cyan]{Markup.Escape(body)}[/]");
    }

    private static string OneArg(string command, string[] args)
    {
        if (args.Length != 1)
        {
            throw new InvalidOperationException($"Usage: {command} VALUE");
        }

        return args[0];
    }
}
