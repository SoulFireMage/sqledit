using System.Diagnostics;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using SqlShell.Core.Connection;
using SqlShell.Core.Export;
using SqlShell.Core.Profiles;
using SqlShell.Core.Scripts;
using EditorControl = Terminal.Gui.Editor.Editor;

namespace SqlShell.Ide;

/// <summary>Turbo-Pascal-inspired full-screen SQL editor.</summary>
public sealed class IdeWindow : Window
{
    private const string HelpText =
        "F1             Help\n"
        + "F2             Save\n"
        + "F3             Open SQL file\n"
        + "F4             Switch profile\n"
        + "F5 / Ctrl+Enter Execute selection or full buffer\n"
        + "Shift+F5       Execute current GO batch\n"
        + "F6             Toggle editor/results focus\n"
        + "F8             Export active result to CSV/TSV\n"
        + "F9             Toggle Turbo/modern theme\n"
        + "Ctrl+N         New file\n"
        + "Ctrl+S         Save\n"
        + "Ctrl+Shift+S   Save as\n"
        + "Ctrl+F         Find text\n"
        + "Ctrl+Q         Quit safely";

    private readonly IApplication _app;
    private readonly ConnectionManager _manager;
    private readonly ProfileStore _store;
    private readonly int _maxRows;
    private readonly IdeDocument _document = new();
    private readonly FindState _find = new();

    private MenuBar _menuBar = null!;
    private Label _title = null!;
    private EditorControl _editor = null!;
    private Tabs _output = null!;
    private TableView _results = null!;
    private EditorControl _messages = null!;
    private StatusBar _status = null!;

    private IReadOnlyList<ResultSet> _resultSets = [];
    private int _activeResult;
    private bool _running;
    private bool _modernTheme;

    public IdeWindow(IApplication app, ConnectionManager manager, ProfileStore store, string? file, int maxRows)
    {
        _app = app;
        _manager = manager;
        _store = store;
        _maxRows = maxRows;

        Title = "sqlshell IDE";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        BuildViews();

        if (file is not null)
        {
            OpenFile(file);
        }

        RefreshTitle();
        _editor.SetFocus();
    }

    /// <summary>Append a connection lifecycle message from any thread.</summary>
    public void LogEvent(string kind, string message)
    {
        var prefix = kind switch
        {
            "error" => "ERROR",
            "warning" => "WARNING",
            "reconnect" => "RECONNECT",
            "ok" => "OK",
            "connected" => "CONNECTED",
            _ => kind.ToUpperInvariant(),
        };
        var document = _messages.Document!;
        document.Insert(document.TextLength, $"[{prefix}] {message}\n");
    }

    private void BuildViews()
    {
        _menuBar = new MenuBar(
        [
            new MenuBarItem("_File",
            [
                new MenuItem("_New", "Ctrl+N", () => NewDocument(), Key.N.WithCtrl),
                new MenuItem("_Open...", "F3", () => PromptOpen(), Key.F3),
                new MenuItem("_Save", "F2", () => Save(), Key.F2),
                new MenuItem("Save _as...", "Ctrl+Shift+S", () => SaveAs(), Key.S.WithCtrl.WithShift),
                new MenuItem("_Export results...", "F8", () => Export(), Key.F8),
                new MenuItem("_Quit", "Ctrl+Q", () => Quit(), Key.Q.WithCtrl),
            ]),
            new MenuBarItem("_Edit",
            [
                new MenuItem("_Undo", "Ctrl+Z", () => _editor.Document!.UndoStack.Undo(), Key.Z.WithCtrl),
                new MenuItem("_Redo", "Ctrl+Y", () => _editor.Document!.UndoStack.Redo(), Key.Y.WithCtrl),
                new MenuItem("Select _all", "Ctrl+A", () => _editor.SelectAll(), Key.A.WithCtrl),
            ]),
            new MenuBarItem("_Search",
            [
                new MenuItem("_Find...", "Ctrl+F", () => Find(), Key.F.WithCtrl),
                new MenuItem("Find _next", "Ctrl+G", () => FindNext(), Key.G.WithCtrl),
                new MenuItem("_Replace...", "Ctrl+H", () => ShowFindReplace(), Key.H.WithCtrl),
            ]),
            new MenuBarItem("_Query",
            [
                new MenuItem("_Execute", "F5", () => RunQuery(), Key.F5),
                new MenuItem("Execute _batch", "Shift+F5", () => RunBatch(), Key.F5.WithShift),
                new MenuItem("_Focus results", "F6", () => ToggleFocus(), Key.F6),
            ]),
            new MenuBarItem("_Connection",
            [
                new MenuItem("_Switch profile...", "F4", () => SwitchProfile(), Key.F4),
            ]),
            new MenuBarItem("_Help",
            [
                new MenuItem("_Keyboard reference", "F1", () => Help(), Key.F1),
                new MenuItem("Toggle _theme", "F9", () => ToggleTheme(), Key.F9),
            ]),
        ])
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
        };

        _title = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = 1 };

        _editor = new EditorControl
        {
            Id = "editor",
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(15),
            Multiline = true,
            WordWrap = false,
            ReadOnly = false,
            GutterOptions = GutterOptions.LineNumbers,
            IndentationSize = 4,
            ConvertTabsToSpaces = true,
            HighlightingDefinition = HighlightingManager.Instance.GetDefinition("TSQL"),
        };
        _editor.ContentChanged += (_, _) => RefreshTitle();
        _editor.CaretChanged += (_, _) => RefreshTitle();
        _editor.SelectionChanged += (_, _) => RefreshTitle();
        _editor.KeyDown += (_, key) => OnKey(key);

        _results = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            Title = "Results",
        };
        _results.Style = new TableStyle
        {
            ShowHeaders = true,
            AlwaysShowHeaders = true,
            ExpandLastColumn = true,
            ShowVerticalCellLines = true,
        };
        _results.NullSymbol = "NULL";
        _results.KeyDown += (_, key) => OnKey(key);

        _messages = new EditorControl
        {
            Id = "messages",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Multiline = true,
            WordWrap = true,
            GutterOptions = GutterOptions.None,
            Title = "Messages",
        };
        _messages.KeyDown += (_, key) => OnKey(key);

        _output = new Tabs
        {
            Id = "output",
            X = 0,
            Y = Pos.AnchorEnd(13),
            Width = Dim.Fill(),
            Height = 12,
        };
        _output.Add(_results, _messages);

        _status = new StatusBar(
        [
            new Shortcut(Key.F1, "Help", () => Help(), "Keyboard reference"),
            new Shortcut(Key.F2, "Save", () => Save(), "Save the buffer"),
            new Shortcut(Key.F3, "Open", () => PromptOpen(), "Open a SQL file"),
            new Shortcut(Key.F5, "Run", () => RunQuery(), "Execute"),
            new Shortcut(Key.F6, "Focus", () => ToggleFocus(), "Toggle editor/results focus"),
            new Shortcut(Key.F8, "Export", () => Export(), "Export active result"),
            new Shortcut(Key.F9, "Theme", () => ToggleTheme(), "Toggle theme"),
            new Shortcut(Key.Q.WithCtrl, "Quit", () => Quit(), "Quit safely"),
        ])
        {
            Id = "status",
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
        };
        foreach (var shortcut in _status.SubViews.OfType<Shortcut>())
        {
            shortcut.BindKeyToApplication = false;
        }

        Add(_menuBar);
        Add(_title);
        Add(_editor);
        Add(_output);
        Add(_status);

        // Keys bubble from the focused view to the top-level window, so a single
        // handler on the window covers editor, results, and messages focus.
        KeyDown += (_, key) => OnKey(key);
    }

    private void OnKey(Key key)
    {
        if (key.IsShift && key.NoShift == Key.F5)
        {
            RunBatch();
            key.Handled = true;
        }
        else if (key == Key.F1)
        {
            Help();
            key.Handled = true;
        }
        else if (key == Key.F2)
        {
            Save();
            key.Handled = true;
        }
        else if (key == Key.F3)
        {
            PromptOpen();
            key.Handled = true;
        }
        else if (key == Key.F4)
        {
            SwitchProfile();
            key.Handled = true;
        }
        else if (key == Key.F5 || key == Key.Enter.WithCtrl)
        {
            RunQuery();
            key.Handled = true;
        }
        else if (key == Key.F6)
        {
            ToggleFocus();
            key.Handled = true;
        }
        else if (key == Key.F8)
        {
            Export();
            key.Handled = true;
        }
        else if (key == Key.F9)
        {
            ToggleTheme();
            key.Handled = true;
        }
        else if (key == Key.N.WithCtrl)
        {
            NewDocument();
            key.Handled = true;
        }
        else if (key == Key.S.WithCtrl.WithShift)
        {
            SaveAs();
            key.Handled = true;
        }
        else if (key == Key.S.WithCtrl)
        {
            Save();
            key.Handled = true;
        }
        else if (key == Key.F.WithCtrl)
        {
            Find();
            key.Handled = true;
        }
        else if (key == Key.G.WithCtrl)
        {
            FindNext();
            key.Handled = true;
        }
        else if (key == Key.H.WithCtrl)
        {
            ShowFindReplace();
            key.Handled = true;
        }
        else if (key == Key.Q.WithCtrl)
        {
            Quit();
            key.Handled = true;
        }
    }

    private void RefreshTitle()
    {
        if (!IsInitialized)
        {
            return;
        }

        var marker = _editor.Text != _document.SavedText ? " *" : string.Empty;
        var profile = _manager.Profile;
        var connection = profile is null ? "Disconnected" : $"{profile.Name} / {profile.Database}";
        var state = _running ? "Running..." : "Ready";
        var location = _editor.Document!.GetLocation(_editor.CaretOffset);
        _title.Text =
            $" {_document.DisplayName}{marker}    |    {connection}    |    "
            + $"Ln {location.Line} Col {location.Column}    |    {state}    |    Limit: {_maxRows:N0} rows";
    }

    private bool Confirm(string message)
    {
        var answer = MessageBox.Query(_app, "sqlshell IDE", message, ["Cancel", "Discard"]);
        return answer == 1;
    }

    private void NewDocument()
    {
        if (_document.IsDirty(_editor.Text ?? string.Empty)
            && !Confirm("Discard unsaved changes and create a new file?"))
        {
            return;
        }

        _document.Reset();
        _editor.Text = string.Empty;
        RefreshTitle();
    }

    private void PromptOpen()
    {
        if (_document.IsDirty(_editor.Text ?? string.Empty)
            && !Confirm("Discard unsaved changes and open another file?"))
        {
            return;
        }

        var start = _document.Path ?? _store.ConfigDir;
        var dialog = new OpenDialog { Path = start, OpenMode = OpenMode.File };
        _app.Run(dialog, ReportDialogError);
        if (!dialog.Canceled && dialog.FilePaths.Count > 0)
        {
            OpenFile(dialog.FilePaths[0]);
        }
    }

    private void OpenFile(string path)
    {
        try
        {
            _document.Load(path);
            _editor.Text = _document.SavedText;
            RefreshTitle();
            LogEvent("ok", $"Opened {System.IO.Path.GetFullPath(path)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogEvent("error", exception.Message);
        }
    }

    private void Save()
    {
        if (_document.Path is null)
        {
            SaveAs();
            return;
        }

        SaveTo(_document.Path);
    }

    private void SaveAs()
    {
        var dialog = new SaveDialog { Path = _document.Path ?? _store.ConfigDir };
        _app.Run(dialog, ReportDialogError);
        if (!dialog.Canceled && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            SaveTo(dialog.FileName);
        }
    }

    private void SaveTo(string path)
    {
        try
        {
            _document.Save(_editor.Text ?? string.Empty, path);
            RefreshTitle();
            LogEvent("ok", $"Saved {System.IO.Path.GetFullPath(path)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogEvent("error", exception.Message);
        }
    }

    private void RunQuery()
    {
        var selection = _editor.SelectedText;
        var sql = string.IsNullOrWhiteSpace(selection) ? _editor.Text ?? string.Empty : selection;
        StartQuery(sql, string.IsNullOrWhiteSpace(selection) ? "buffer" : "selection");
    }

    private void RunBatch()
    {
        var cursorLine = _editor.Document!.GetLocation(_editor.CaretOffset).Line - 1;
        var sql = BatchParser.CurrentBatch(_editor.Text ?? string.Empty, cursorLine);
        StartQuery(sql, "current GO batch");
    }

    private void StartQuery(string sql, string label)
    {
        if (_running)
        {
            LogEvent("warning", "A query is already running");
            return;
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            LogEvent("warning", "There is no SQL to execute");
            return;
        }

        _running = true;
        RefreshTitle();
        LogEvent("info", $"Executing {label}...");

        Task.Run(() =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var results = _manager.Execute(sql, _maxRows);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                _app.Invoke(() => CompleteQuery(results, elapsed));
            }
            catch (Exception exception)
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                _app.Invoke(() => FailQuery(exception, elapsed));
            }
        });
    }

    private void CompleteQuery(IReadOnlyList<ResultSet> results, double elapsed)
    {
        _running = false;
        _resultSets = results.Where(result => result.Columns.Count > 0).ToList();
        var affected = results.Where(result => result.Columns.Count == 0).Sum(result => Math.Max(0, result.RowCount));
        if (_resultSets.Count > 0)
        {
            _activeResult = 0;
            _results.Table = new ResultTableSource(_resultSets[0]);
            _results.SetNeedsDraw();
            _output.Value = _results;
            var total = _resultSets.Sum(result => result.Rows.Count);
            LogEvent("ok", $"Completed in {elapsed:0.00}s; {total:N0} row(s) displayed");
            if (_resultSets.Any(result => result.Truncated))
            {
                LogEvent("warning", $"Result limited to {_maxRows:N0} rows per result set");
            }
        }
        else
        {
            _output.Value = _messages;
            LogEvent("ok", $"Completed in {elapsed:0.00}s; {affected:N0} row(s) affected");
        }

        RefreshTitle();
    }

    private void FailQuery(Exception exception, double elapsed)
    {
        _running = false;
        _output.Value = _messages;
        LogEvent("error", $"Error after {elapsed:0.00}s: {exception.Message}");
        RefreshTitle();
    }

    private void Export()
    {
        if (_resultSets.Count == 0)
        {
            LogEvent("warning", "Run a query with results before exporting");
            return;
        }

        var dialog = new SaveDialog { Path = _store.ConfigDir };
        _app.Run(dialog, ReportDialogError);
        if (dialog.Canceled || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        try
        {
            var count = ResultExporter.Export([_resultSets[_activeResult]], dialog.FileName);
            LogEvent("ok", $"Exported {count:N0} row(s) to {System.IO.Path.GetFullPath(dialog.FileName)}");
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            LogEvent("error", exception.Message);
        }
    }

    private void Find()
    {
        var field = new TextField { Text = _find.Text, Width = Dim.Fill() };
        var input = PromptExtensions.Prompt(_app, field);
        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        _find.Text = input;
        FindNext();
    }

    private void FindNext()
    {
        if (string.IsNullOrEmpty(_find.Text))
        {
            Find();
            return;
        }

        if (!_editor.FindNext(_find.Text, matchCase: false, wrapAround: true))
        {
            LogEvent("warning", $"Not found: {_find.Text}");
        }
    }

    private void ShowFindReplace() => _app.Run(new FindReplaceDialog(_editor, selectReplaceTab: true), ReportDialogError);

    private void SwitchProfile()
    {
        if (_running)
        {
            LogEvent("warning", "Wait for the current query to finish");
            return;
        }

        var field = new TextField { Text = _manager.Profile?.Name ?? string.Empty, Width = Dim.Fill() };
        var input = PromptExtensions.Prompt(_app, field);
        if (string.IsNullOrWhiteSpace(input) || input == _manager.Profile?.Name)
        {
            return;
        }

        _running = true;
        RefreshTitle();
        Task.Run(() =>
        {
            try
            {
                _manager.Connect(_store.Get(input));
                _app.Invoke(() =>
                {
                    _running = false;
                    RefreshTitle();
                });
            }
            catch (Exception exception)
            {
                _app.Invoke(() =>
                {
                    _running = false;
                    LogEvent("error", exception.Message);
                    RefreshTitle();
                });
            }
        });
    }

    private void ToggleFocus()
    {
        if (_editor.HasFocus)
        {
            _results.SetFocus();
        }
        else
        {
            _editor.SetFocus();
        }
    }

    private void ToggleTheme()
    {
        _modernTheme = !_modernTheme;
        SchemeName = _modernTheme ? "Accent" : "Base";
        SetNeedsDraw();
        LogEvent("info", _modernTheme ? "Modern dark theme" : "Turbo blue theme");
    }

    private void Help() => MessageBox.Query(_app, "Keyboard reference", HelpText, ["Close"]);

    private void Quit()
    {
        if (_running)
        {
            LogEvent("warning", "Wait for the current database operation to finish");
            return;
        }

        if (_document.IsDirty(_editor.Text ?? string.Empty) && !Confirm("Discard unsaved changes and quit?"))
        {
            return;
        }

        _app.RequestStop(this);
    }

    private bool ReportDialogError(Exception exception)
    {
        LogEvent("error", exception.Message);
        return false;
    }
}