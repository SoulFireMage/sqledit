using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using SqlShell.Core.Connection;
using SqlShell.Core.Export;

namespace SqlShell.Cli.Rendering;

/// <summary>Spectre.Console rendering for query output and connection events.</summary>
public sealed class ResultRenderer
{
    private readonly bool _fullWidth;
    private readonly IAnsiConsole _console;

    public ResultRenderer(bool fullWidth, IAnsiConsole? console = null)
    {
        _fullWidth = fullWidth;
        _console = console ?? AnsiConsole.Console;
    }

    public IAnsiConsole Console => _console;

    public void Event(string kind, string message)
    {
        var color = kind switch
        {
            "connected" => "green",
            "reconnect" => "bold yellow",
            "error" => "bold red",
            _ => "cyan",
        };
        _console.MarkupLine($"[{color}]{Markup.Escape(message)}[/]");
    }

    public T WithStatus<T>(string message, Func<T> action)
        => _console.Status().Start(message, _ => action());

    public void Results(IReadOnlyList<ResultSet> resultSets)
    {
        foreach (var result in resultSets)
        {
            if (result.Columns.Count > 0)
            {
                var table = new Table();
                foreach (var column in result.Columns)
                {
                    table.AddColumn(new TableColumn(new Markup(Markup.Escape(column))) { NoWrap = !_fullWidth });
                }

                foreach (var row in result.Rows)
                {
                    table.AddRow(row.Select(FormatValue));
                }

                _console.Write(table);
                _console.MarkupLine($"[green]{result.Rows.Count:N0} row(s)[/]");
            }
            else if (result.RowCount >= 0)
            {
                _console.MarkupLine($"[green]Query OK, {result.RowCount:N0} row(s) affected[/]");
            }
            else
            {
                _console.MarkupLine("[green]Query OK[/]");
            }
        }
    }

    public void Error(Exception error)
        => _console.MarkupLine($"[bold red]Error: {Markup.Escape(error.Message)}[/]");

    public void Info(string message)
        => _console.MarkupLine($"[cyan]{Markup.Escape(message)}[/]");

    private static IRenderable FormatValue(object? value)
    {
        if (value is null)
        {
            return new Markup("[dim italic]NULL[/]");
        }

        return new Markup(Markup.Escape(ResultExporter.FormatValue(value)));
    }
}
