"""Textual full-screen SQL editor for sqlshell v2."""

from __future__ import annotations

from pathlib import Path
from time import perf_counter

from rich.text import Text
from textual import on, work
from textual.app import App, ComposeResult
from textual.binding import Binding
from textual.containers import Horizontal, Vertical
from textual.screen import ModalScreen
from textual.widgets import (
    Button,
    DataTable,
    DirectoryTree,
    Footer,
    Input,
    OptionList,
    RichLog,
    Select,
    Static,
    TabbedContent,
    TabPane,
    TextArea,
)
from textual.widgets.option_list import Option
from textual.widgets.text_area import Selection

from .connection import ConnectionManager, ResultSet
from .export import export_results
from .profiles import ProfileStore
from .scripts import current_batch


class TextPrompt(ModalScreen[str | None]):
    """Small keyboard-friendly prompt used for paths and profile names."""

    CSS = """
    TextPrompt { align: center middle; background: $background 65%; }
    TextPrompt > Vertical { width: 72; height: auto; border: double #00ffff; background: #0000aa; padding: 1 2; }
    TextPrompt Input { margin: 1 0; }
    TextPrompt Horizontal { height: 3; align-horizontal: right; }
    TextPrompt Button { margin-left: 1; }
    """

    def __init__(self, title: str, initial: str = "", placeholder: str = "") -> None:
        super().__init__()
        self.prompt_title = title
        self.initial = initial
        self.placeholder = placeholder

    def compose(self) -> ComposeResult:
        with Vertical():
            yield Static(self.prompt_title, classes="dialog-title")
            yield Input(value=self.initial, placeholder=self.placeholder, id="prompt-value")
            with Horizontal():
                yield Button("Cancel", id="cancel")
                yield Button("OK", variant="primary", id="ok")

    def on_mount(self) -> None:
        self.query_one(Input).focus()

    @on(Input.Submitted)
    def submit(self) -> None:
        self.dismiss(self.query_one(Input).value.strip() or None)

    @on(Button.Pressed)
    def button(self, event: Button.Pressed) -> None:
        self.dismiss(self.query_one(Input).value.strip() or None if event.button.id == "ok" else None)


class ConfirmPrompt(ModalScreen[bool]):
    CSS = """
    ConfirmPrompt { align: center middle; background: $background 65%; }
    ConfirmPrompt > Vertical { width: 64; height: auto; border: double #ffff00; background: #0000aa; padding: 1 2; }
    ConfirmPrompt Horizontal { height: 3; margin-top: 1; align-horizontal: right; }
    ConfirmPrompt Button { margin-left: 1; }
    """

    def __init__(self, message: str, confirm_label: str = "Discard") -> None:
        super().__init__()
        self.message = message
        self.confirm_label = confirm_label

    def compose(self) -> ComposeResult:
        with Vertical():
            yield Static(self.message)
            with Horizontal():
                yield Button("Cancel", id="no")
                yield Button(self.confirm_label, variant="warning", id="yes")

    @on(Button.Pressed)
    def answer(self, event: Button.Pressed) -> None:
        self.dismiss(event.button.id == "yes")


class FilteredDirectoryTree(DirectoryTree):
    """Directory tree that retains folders while filtering visible files."""

    def __init__(self, path: Path, extensions: set[str]) -> None:
        self.extensions = {suffix.lower() for suffix in extensions}
        super().__init__(path, id="file-tree")

    def filter_paths(self, paths):
        return filter_file_paths(paths, self.extensions)


def filter_file_paths(paths, extensions: set[str]):
    """Return directories and files matching a case-insensitive suffix set."""
    extensions = {suffix.lower() for suffix in extensions}
    return [path for path in paths if path.is_dir() or path.suffix.lower() in extensions]


class FileBrowser(ModalScreen[Path | None]):
    """Keyboard and mouse file browser for opening and saving files."""

    CSS = """
    FileBrowser { align: center middle; background: $background 70%; }
    FileBrowser > Vertical { width: 90%; height: 85%; border: double #00ffff; background: #0000aa; padding: 1; }
    FileBrowser #browser-path { height: 1; color: #ffff00; }
    FileBrowser DirectoryTree { height: 1fr; margin: 1 0; background: #000080; }
    FileBrowser Input { height: 3; }
    FileBrowser Horizontal { height: 3; align-horizontal: right; }
    FileBrowser Button { margin-left: 1; }
    """

    def __init__(
        self,
        mode: str,
        start: Path,
        *,
        extensions: set[str],
        filename: str = "",
    ) -> None:
        super().__init__()
        self.mode = mode
        self.current_dir = start.resolve()
        self.extensions = extensions
        self.filename = filename

    def compose(self) -> ComposeResult:
        verb = "Open" if self.mode == "open" else "Save"
        with Vertical():
            yield Static(f"{verb} file", classes="dialog-title")
            yield Static(str(self.current_dir), id="browser-path")
            yield FilteredDirectoryTree(self.current_dir, self.extensions)
            yield Input(value=self.filename, placeholder="Filename", id="browser-name")
            with Horizontal():
                yield Button("Up", id="up")
                yield Button("New folder", id="mkdir")
                yield Button("Cancel", id="cancel")
                yield Button(verb, id="accept", variant="primary")

    @on(DirectoryTree.DirectorySelected)
    def directory_selected(self, event: DirectoryTree.DirectorySelected) -> None:
        self.current_dir = event.path.resolve()
        self.query_one("#browser-path", Static).update(str(self.current_dir))

    @on(DirectoryTree.FileSelected)
    def file_selected(self, event: DirectoryTree.FileSelected) -> None:
        self.current_dir = event.path.parent.resolve()
        self.query_one("#browser-name", Input).value = event.path.name
        if self.mode == "open":
            self.dismiss(event.path.resolve())

    @on(Input.Submitted, "#browser-name")
    def filename_submitted(self) -> None:
        self._accept()

    @on(Button.Pressed)
    def browser_button(self, event: Button.Pressed) -> None:
        if event.button.id == "cancel":
            self.dismiss(None)
        elif event.button.id == "accept":
            self._accept()
        elif event.button.id == "up":
            self.current_dir = self.current_dir.parent
            self.query_one("#browser-path", Static).update(str(self.current_dir))
            self.query_one("#file-tree", DirectoryTree).path = self.current_dir
        elif event.button.id == "mkdir":
            self.app.push_screen(TextPrompt("New folder name"), self._create_folder)

    def _accept(self) -> None:
        value = self.query_one("#browser-name", Input).value.strip()
        if not value:
            self.notify("Enter or select a filename", severity="warning")
            return
        candidate = Path(value).expanduser()
        if not candidate.is_absolute():
            candidate = self.current_dir / candidate
        if self.mode == "open" and not candidate.is_file():
            self.notify(f"File not found: {candidate}", severity="error")
            return
        if candidate.suffix.lower() not in self.extensions:
            expected = " or ".join(sorted(self.extensions))
            self.notify(f"Filename must end in {expected}", severity="warning")
            return
        self.dismiss(candidate.resolve())

    def _create_folder(self, value: str | None) -> None:
        if not value:
            return
        if value in (".", "..") or Path(value).name != value:
            self.notify("Enter a single folder name", severity="warning")
            return
        folder = self.current_dir / value
        try:
            folder.mkdir()
        except OSError as exc:
            self.notify(str(exc), severity="error")
            return
        self.current_dir = folder.resolve()
        self.query_one("#browser-path", Static).update(str(self.current_dir))
        self.query_one("#file-tree", DirectoryTree).path = self.current_dir


class MenuPopup(ModalScreen[str | None]):
    """Simple navigable application menu."""

    BINDINGS = [Binding("escape", "cancel", "Close")]
    CSS = """
    MenuPopup { align: center top; background: $background 40%; padding-top: 3; }
    MenuPopup > Vertical { width: 42; height: auto; max-height: 24; border: double #00ffff; background: #0000aa; padding: 1; }
    MenuPopup OptionList { height: auto; max-height: 18; background: #0000aa; }
    """

    def __init__(self, title: str, items: list[tuple[str, str]]) -> None:
        super().__init__()
        self.menu_title = title
        self.items = items

    def compose(self) -> ComposeResult:
        with Vertical():
            yield Static(self.menu_title, classes="dialog-title")
            yield OptionList(*(Option(label, id=action) for label, action in self.items))

    @on(OptionList.OptionSelected)
    def selected(self, event: OptionList.OptionSelected) -> None:
        self.dismiss(event.option.id)

    def action_cancel(self) -> None:
        self.dismiss(None)


class HelpScreen(ModalScreen[None]):
    BINDINGS = [Binding("escape", "dismiss", "Close")]
    CSS = """
    HelpScreen { align: center middle; background: $background 70%; }
    HelpScreen > Vertical { width: 76; height: 25; border: double #00ffff; background: #0000aa; padding: 1 2; }
    HelpScreen .help { height: 1fr; }
    HelpScreen Button { width: 16; align-horizontal: center; }
    """

    def compose(self) -> ComposeResult:
        with Vertical():
            yield Static("SQLSHELL 2.0 — KEYBOARD REFERENCE", classes="dialog-title")
            yield Static(
                "F1             Help\n"
                "F2             Save\n"
                "F3             Open SQL file\n"
                "F4             Switch profile\n"
                "F5 / Ctrl+Enter Execute selection or full buffer\n"
                "Shift+F5       Execute current GO batch\n"
                "F6             Toggle editor/results focus\n"
                "F8             Export active result to CSV/TSV\n"
                "F9             Toggle Turbo/modern theme\n"
                "F10            Open File menu\n"
                "Ctrl+Shift+S   Save as\n"
                "Ctrl+F         Find text\n"
                "Alt+F/E/S/Q/C/H Open top menus\n"
                "Ctrl+P         Command palette\n"
                "Ctrl+Q         Quit safely",
                classes="help",
            )
            yield Button("Close", id="close", variant="primary")

    def action_dismiss(self) -> None:
        self.dismiss(None)

    @on(Button.Pressed, "#close")
    def close(self) -> None:
        self.dismiss(None)


class SqlIdeApp(App[None]):
    """A DOS-inspired SQL editor sharing the v1 connection manager."""

    TITLE = "sqlshell IDE"
    ENABLE_COMMAND_PALETTE = True
    BINDINGS = [
        Binding("f1", "help", "Help", priority=True),
        Binding("f2", "save", "Save", priority=True),
        Binding("f3", "open", "Open", priority=True),
        Binding("f4", "profile", "Profile", priority=True),
        Binding("f5,ctrl+enter", "run_query", "Run", priority=True),
        Binding("shift+f5", "run_batch", "Batch", show=False, priority=True),
        Binding("f6", "toggle_results", "Results", priority=True),
        Binding("f8", "export", "Export", priority=True),
        Binding("f9", "toggle_theme", "Theme", priority=True),
        Binding("f10", "menu_file", "Menu", show=False, priority=True),
        Binding("ctrl+shift+s", "save_as", "Save as", show=False, priority=True),
        Binding("ctrl+n", "new", "New", show=False, priority=True),
        Binding("ctrl+f", "find", "Find", show=False, priority=True),
        Binding("ctrl+q", "request_quit", "Quit", priority=True),
        Binding("alt+f", "menu_file", "File menu", show=False, priority=True),
        Binding("alt+e", "menu_edit", "Edit menu", show=False, priority=True),
        Binding("alt+s", "menu_search", "Search menu", show=False, priority=True),
        Binding("alt+q", "menu_query", "Query menu", show=False, priority=True),
        Binding("alt+c", "menu_connection", "Connection menu", show=False, priority=True),
        Binding("alt+h", "menu_help", "Help menu", show=False, priority=True),
    ]

    CSS = """
    Screen { background: #0000aa; color: #ffffff; }
    #menu { height: 1; background: #00aaaa; layout: horizontal; }
    #menu Button { height: 1; min-width: 9; width: auto; border: none; padding: 0 1; background: #00aaaa; color: #000000; text-style: bold; }
    #menu Button:hover, #menu Button:focus { background: #000000; color: #ffffff; }
    #document-title { height: 1; background: #0000aa; color: #ffff00; padding: 0 1; }
    #editor { height: 1fr; border: solid #00ffff; background: #0000aa; color: #ffffff; }
    #output { height: 40%; min-height: 8; border: solid #00ffff; }
    #result-set { height: 3; display: none; }
    #result-set.visible { display: block; }
    DataTable { height: 1fr; background: #000080; }
    RichLog { background: #000080; color: #ffffff; }
    #status { height: 1; background: #00aaaa; color: #000000; padding: 0 1; }
    Footer { background: #000000; color: #ffffff; }
    .dialog-title { text-style: bold; color: #ffff00; }
    Screen.modern { background: #161b22; color: #e6edf3; }
    Screen.modern #menu, Screen.modern #menu Button { background: #30363d; color: #e6edf3; }
    Screen.modern #document-title { background: #161b22; color: #58a6ff; }
    Screen.modern #editor { background: #0d1117; border: solid #30363d; }
    Screen.modern #output { border: solid #30363d; }
    Screen.modern DataTable, Screen.modern RichLog { background: #0d1117; }
    Screen.modern #status { background: #30363d; color: #e6edf3; }
    """

    def __init__(
        self,
        manager: ConnectionManager,
        store: ProfileStore,
        *,
        path: Path | None = None,
        text: str = "",
        max_rows: int = 5000,
    ) -> None:
        super().__init__()
        self.manager = manager
        self.store = store
        self.path = path
        self.initial_text = text
        self.saved_text = text
        self.max_rows = max_rows
        self.result_sets: list[ResultSet] = []
        self.active_result = 0
        self.query_running = False
        self.modern_theme = False
        self.find_text = ""
        self.pending_save_path: Path | None = None
        self.last_directory = self._load_last_directory(path)

    def compose(self) -> ComposeResult:
        with Horizontal(id="menu"):
            yield Button("File", id="menu-file")
            yield Button("Edit", id="menu-edit")
            yield Button("Search", id="menu-search")
            yield Button("Query", id="menu-query")
            yield Button("Connection", id="menu-connection")
            yield Button("Help", id="menu-help")
        yield Static("", id="document-title")
        yield TextArea.code_editor(
            self.initial_text,
            language="sql",
            theme="css",
            soft_wrap=False,
            id="editor",
        )
        with TabbedContent(id="output", initial="results-pane"):
            with TabPane("Results", id="results-pane"):
                yield Select([], prompt="Result set", allow_blank=True, id="result-set")
                yield DataTable(id="results-grid", zebra_stripes=True, cursor_type="cell")
            with TabPane("Messages", id="messages-pane"):
                yield RichLog(id="messages", wrap=True, markup=False)
        yield Static("", id="status")
        yield Footer()

    def on_mount(self) -> None:
        self.manager.event = self._connection_event
        self._update_title()
        self._update_status()
        profile = self.manager.profile
        if profile:
            self._log("connected", f"Connected: {profile.name} — {profile.server}/{profile.database}")
        self.query_one("#editor", TextArea).focus()

    @property
    def dirty(self) -> bool:
        return self.query_one("#editor", TextArea).text != self.saved_text

    @on(TextArea.Changed, "#editor")
    def changed(self) -> None:
        self._update_title()

    @on(TextArea.SelectionChanged, "#editor")
    def selection_changed(self) -> None:
        self._update_status()

    @on(Select.Changed, "#result-set")
    def result_changed(self, event: Select.Changed) -> None:
        if isinstance(event.value, int):
            self.active_result = event.value
            self._populate_grid(self.result_sets[event.value])

    @on(Button.Pressed, "#menu Button")
    def menu_clicked(self, event: Button.Pressed) -> None:
        menu = (event.button.id or "").removeprefix("menu-").replace("-", "_")
        action = getattr(self, f"action_menu_{menu}", None)
        if action:
            action()

    def action_run_query(self) -> None:
        editor = self.query_one("#editor", TextArea)
        sql = editor.selected_text.strip() or editor.text.strip()
        self._start_query(sql, "selection" if editor.selected_text.strip() else "buffer")

    def action_run_batch(self) -> None:
        editor = self.query_one("#editor", TextArea)
        sql = current_batch(editor.text, editor.cursor_location[0])
        self._start_query(sql, "current GO batch")

    def _start_query(self, sql: str, label: str) -> None:
        if self.query_running:
            self.notify("A query is already running", severity="warning")
            return
        if not sql:
            self.notify("There is no SQL to execute", severity="warning")
            return
        self.query_running = True
        self._update_status("Running...")
        self._log("info", f"Executing {label}...")
        self._execute_worker(sql)

    @work(thread=True, exclusive=True, group="sql-query")
    def _execute_worker(self, sql: str) -> None:
        started = perf_counter()
        try:
            results = self.manager.execute(sql, max_rows=self.max_rows)
        except Exception as exc:
            self.call_from_thread(self._query_failed, exc, perf_counter() - started)
        else:
            self.call_from_thread(self._query_complete, results, perf_counter() - started)

    def _query_complete(self, results: list[ResultSet], elapsed: float) -> None:
        self.query_running = False
        self.result_sets = [result for result in results if result.columns]
        affected = sum(max(0, result.row_count) for result in results if not result.columns)
        if self.result_sets:
            picker = self.query_one("#result-set", Select)
            picker.set_options([(f"Result {i + 1} — {len(result.rows):,} rows", i) for i, result in enumerate(self.result_sets)])
            picker.value = 0
            picker.set_class(len(self.result_sets) > 1, "visible")
            self.active_result = 0
            self._populate_grid(self.result_sets[0])
            total = sum(len(result.rows) for result in self.result_sets)
            self._log("ok", f"Completed in {elapsed:.2f}s; {total:,} row(s) displayed")
            if any(result.truncated for result in self.result_sets):
                self._log("warning", f"Result limited to {self.max_rows:,} rows per result set")
            self.query_one("#output", TabbedContent).active = "results-pane"
        else:
            self.query_one("#results-grid", DataTable).clear(columns=True)
            self._log("ok", f"Completed in {elapsed:.2f}s; {affected:,} row(s) affected")
            self.query_one("#output", TabbedContent).active = "messages-pane"
        self._update_status("Ready")

    def _query_failed(self, error: BaseException, elapsed: float) -> None:
        self.query_running = False
        self._log("error", f"Error after {elapsed:.2f}s: {error}")
        self.query_one("#output", TabbedContent).active = "messages-pane"
        self._update_status("Query failed")

    def _populate_grid(self, result: ResultSet) -> None:
        table = self.query_one("#results-grid", DataTable)
        table.clear(columns=True)
        table.add_columns(*result.columns)
        table.add_rows([[self._cell(value) for value in row] for row in result.rows])

    @staticmethod
    def _cell(value):
        if value is None:
            return Text("NULL", style="dim italic")
        if isinstance(value, bytes):
            return "0x" + value.hex()
        return str(value)

    def action_save(self) -> None:
        if self.path is None:
            self.action_save_as()
        else:
            self._save_to(self.path)

    def action_save_as(self) -> None:
        start = self.path.parent if self.path else self.last_directory
        filename = self.path.name if self.path else "query.sql"
        self.push_screen(
            FileBrowser("save", start, extensions={".sql"}, filename=filename),
            self._save_prompted,
        )

    def _save_prompted(self, value: Path | None) -> None:
        if not value:
            return
        self._remember_directory(value.parent)
        if value.exists() and value != self.path:
            self.pending_save_path = value
            self.push_screen(
                ConfirmPrompt(f"Replace existing file?\n{value}", "Overwrite"),
                self._overwrite_confirmed,
            )
        else:
            self._save_to(value)

    def _overwrite_confirmed(self, confirmed: bool) -> None:
        path = self.pending_save_path
        self.pending_save_path = None
        if confirmed and path:
            self._save_to(path)

    def _save_to(self, path: Path) -> None:
        try:
            text = self.query_one("#editor", TextArea).text
            path.write_text(text, encoding="utf-8")
        except OSError as exc:
            self.notify(str(exc), severity="error")
            return
        self.path = path
        self.saved_text = text
        self._update_title()
        self._log("ok", f"Saved {path.resolve()}")

    def action_open(self) -> None:
        if self.dirty:
            self.push_screen(ConfirmPrompt("Discard unsaved changes and open another file?"), self._open_confirmed)
        else:
            self._prompt_open()

    def _open_confirmed(self, confirmed: bool) -> None:
        if confirmed:
            self._prompt_open()

    def _prompt_open(self) -> None:
        self.push_screen(
            FileBrowser("open", self.last_directory, extensions={".sql"}),
            self._open_prompted,
        )

    def _open_prompted(self, value: Path | None) -> None:
        if not value:
            return
        path = value
        try:
            text = path.read_text(encoding="utf-8-sig")
        except OSError as exc:
            self.notify(str(exc), severity="error")
            return
        self.path = path
        self._remember_directory(path.parent)
        self.saved_text = text
        self.query_one("#editor", TextArea).load_text(text)
        self._update_title()
        self._log("ok", f"Opened {path.resolve()}")

    def action_export(self) -> None:
        if not self.result_sets:
            self.notify("Run a query with results before exporting", severity="warning")
            return
        self.push_screen(
            FileBrowser("save", self.last_directory, extensions={".csv", ".tsv"}, filename="results.csv"),
            self._export_prompted,
        )

    def _export_prompted(self, value: Path | None) -> None:
        if not value:
            return
        path = value
        self._remember_directory(path.parent)
        if path.exists():
            self.pending_save_path = path
            self.push_screen(
                ConfirmPrompt(f"Replace existing export?\n{path}", "Overwrite"),
                self._export_overwrite_confirmed,
            )
            return
        self._export_to(path)

    def _export_overwrite_confirmed(self, confirmed: bool) -> None:
        path = self.pending_save_path
        self.pending_save_path = None
        if confirmed and path:
            self._export_to(path)

    def _export_to(self, path: Path) -> None:
        try:
            count = export_results([self.result_sets[self.active_result]], path)
        except (OSError, ValueError) as exc:
            self.notify(str(exc), severity="error")
            return
        self._log("ok", f"Exported {count:,} row(s) to {path.resolve()}")

    def action_new(self) -> None:
        if self.dirty:
            self.push_screen(ConfirmPrompt("Discard unsaved changes and create a new file?"), self._new_confirmed)
        else:
            self._new_document()

    def _new_confirmed(self, confirmed: bool) -> None:
        if confirmed:
            self._new_document()

    def _new_document(self) -> None:
        self.path = None
        self.saved_text = ""
        self.query_one("#editor", TextArea).load_text("")
        self._update_title()

    def action_undo(self) -> None:
        self.query_one("#editor", TextArea).undo()

    def action_redo(self) -> None:
        self.query_one("#editor", TextArea).redo()

    def action_select_all(self) -> None:
        editor = self.query_one("#editor", TextArea)
        editor.select_all()
        editor.focus()

    def action_find(self) -> None:
        self.push_screen(TextPrompt("Find text", self.find_text), self._find_prompted)

    def _find_prompted(self, value: str | None) -> None:
        if value:
            self.find_text = value
            self.action_find_next()

    def action_find_next(self) -> None:
        if not self.find_text:
            self.action_find()
            return
        editor = self.query_one("#editor", TextArea)
        text = editor.text
        start = editor.document.get_index_from_location(editor.selection.end)
        index = text.casefold().find(self.find_text.casefold(), start)
        if index < 0 and start:
            index = text.casefold().find(self.find_text.casefold(), 0, start)
        if index < 0:
            self.notify(f"Not found: {self.find_text}", severity="warning")
            return
        begin = editor.document.get_location_from_index(index)
        end = editor.document.get_location_from_index(index + len(self.find_text))
        editor.selection = Selection(begin, end)
        editor.focus()

    def action_menu_file(self) -> None:
        self._open_menu("File", [
            ("New                 Ctrl+N", "new"),
            ("Open...             F3", "open"),
            ("Save                F2", "save"),
            ("Save as...          Ctrl+Shift+S", "save_as"),
            ("Export results...   F8", "export"),
            ("Quit                Ctrl+Q", "request_quit"),
        ])

    def action_menu_edit(self) -> None:
        self._open_menu("Edit", [
            ("Undo", "undo"),
            ("Redo", "redo"),
            ("Select all", "select_all"),
        ])

    def action_menu_search(self) -> None:
        self._open_menu("Search", [("Find...", "find"), ("Find next", "find_next")])

    def action_menu_query(self) -> None:
        self._open_menu("Query", [
            ("Execute selection/buffer    F5", "run_query"),
            ("Execute current GO batch    Shift+F5", "run_batch"),
            ("Focus results               F6", "toggle_results"),
        ])

    def action_menu_connection(self) -> None:
        self._open_menu("Connection", [("Switch profile...    F4", "profile")])

    def action_menu_help(self) -> None:
        self._open_menu("Help", [("Keyboard reference    F1", "help"), ("Toggle theme          F9", "toggle_theme")])

    def _open_menu(self, title: str, items: list[tuple[str, str]]) -> None:
        self.push_screen(MenuPopup(title, items), self._menu_selected)

    def _menu_selected(self, action: str | None) -> None:
        if action:
            handler = getattr(self, f"action_{action}", None)
            if handler:
                handler()

    def action_profile(self) -> None:
        if self.query_running:
            self.notify("Wait for the current query to finish", severity="warning")
            return
        names = ", ".join(profile.name for profile in self.store.list())
        current = self.manager.profile.name if self.manager.profile else ""
        self.push_screen(TextPrompt(f"Switch profile (available: {names})", current), self._profile_prompted)

    def _profile_prompted(self, value: str | None) -> None:
        if value and (not self.manager.profile or value != self.manager.profile.name):
            self.query_running = True
            self._update_status("Connecting...")
            self._connect_worker(value)

    @work(thread=True, exclusive=True, group="connection")
    def _connect_worker(self, name: str) -> None:
        try:
            self.manager.connect(self.store.get(name))
        except Exception as exc:
            self.call_from_thread(self._connection_failed, exc)
        else:
            self.call_from_thread(self._connection_complete)

    def _connection_complete(self) -> None:
        self.query_running = False
        self._update_status("Ready")
        self._update_title()

    def _connection_failed(self, error: BaseException) -> None:
        self.query_running = False
        self._log("error", f"Connection failed: {error}")
        self._update_status("Connection failed")

    def action_toggle_results(self) -> None:
        editor = self.query_one("#editor", TextArea)
        table = self.query_one("#results-grid", DataTable)
        if editor.has_focus:
            table.focus()
        else:
            editor.focus()

    def action_toggle_theme(self) -> None:
        self.modern_theme = not self.modern_theme
        self.screen.set_class(self.modern_theme, "modern")
        self.notify("Modern dark theme" if self.modern_theme else "Turbo blue theme")

    def action_help(self) -> None:
        self.push_screen(HelpScreen())

    def action_request_quit(self) -> None:
        if self.query_running:
            self.notify("Wait for the current database operation to finish", severity="warning")
            return
        if self.dirty:
            self.push_screen(ConfirmPrompt("Discard unsaved changes and quit?"), self._quit_confirmed)
        else:
            self.exit()

    def _quit_confirmed(self, confirmed: bool) -> None:
        if confirmed:
            self.exit()

    def _connection_event(self, kind: str, message: str) -> None:
        self.call_from_thread(self._log, kind, message)

    def _log(self, kind: str, message: str) -> None:
        prefixes = {"error": "ERROR", "warning": "WARNING", "reconnect": "RECONNECT", "ok": "OK"}
        self.query_one("#messages", RichLog).write(f"[{prefixes.get(kind, kind.upper())}] {message}")

    def _update_title(self) -> None:
        if not self.is_mounted:
            return
        filename = str(self.path) if self.path else "Untitled.sql"
        marker = " *" if self.dirty else ""
        profile = self.manager.profile
        connection = f"{profile.name} / {profile.database}" if profile else "Disconnected"
        self.query_one("#document-title", Static).update(f" {filename}{marker}    |    {connection}")

    def _update_status(self, state: str = "Ready") -> None:
        if not self.is_mounted:
            return
        editor = self.query_one("#editor", TextArea)
        line, column = editor.cursor_location
        profile = self.manager.profile.name if self.manager.profile else "none"
        self.query_one("#status", Static).update(
            f" Ln {line + 1} Col {column + 1}  |  {state}  |  Profile: {profile}  |  Limit: {self.max_rows:,} rows"
        )

    def _load_last_directory(self, path: Path | None) -> Path:
        if path:
            return path.resolve().parent
        config_dir = getattr(self.store, "config_dir", None)
        self._last_directory_file = Path(config_dir) / "ide-last-directory.txt" if config_dir else None
        if self._last_directory_file:
            try:
                remembered = Path(self._last_directory_file.read_text(encoding="utf-8").strip())
                if remembered.is_dir():
                    return remembered.resolve()
            except OSError:
                pass
        return Path.cwd().resolve()

    def _remember_directory(self, path: Path) -> None:
        self.last_directory = path.resolve()
        settings_file = getattr(self, "_last_directory_file", None)
        if settings_file:
            try:
                settings_file.parent.mkdir(parents=True, exist_ok=True)
                settings_file.write_text(str(self.last_directory), encoding="utf-8")
            except OSError:
                pass


def launch_editor(
    manager: ConnectionManager,
    store: ProfileStore,
    filename: str | None = None,
    *,
    max_rows: int = 5000,
) -> None:
    path = Path(filename).expanduser() if filename else None
    text = ""
    if path:
        text = path.read_text(encoding="utf-8-sig")
    SqlIdeApp(manager, store, path=path, text=text, max_rows=max_rows).run()
