"""Interactive prompt_toolkit REPL."""

from __future__ import annotations

import shlex
from pathlib import Path

from prompt_toolkit import PromptSession
from prompt_toolkit.auto_suggest import AutoSuggestFromHistory
from prompt_toolkit.completion import WordCompleter
from prompt_toolkit.history import FileHistory
from prompt_toolkit.lexers import PygmentsLexer
from pygments.lexers.sql import TransactSqlLexer

from . import __version__
from .connection import ConnectionManager
from .export import export_results, parse_redirection
from .profiles import ProfileError, ProfileStore
from .render import Renderer
from .scripts import split_batches

COMMANDS = [".connect", ".run", ".save", ".edit", ".history", ".help", ".exit"]


class SqlShell:
    def __init__(self, manager: ConnectionManager, store: ProfileStore, renderer: Renderer) -> None:
        self.manager = manager
        self.store = store
        self.renderer = renderer
        history_path = store.config_dir / "history"
        history_path.parent.mkdir(parents=True, exist_ok=True)
        self.session = PromptSession(
            history=FileHistory(str(history_path)),
            auto_suggest=AutoSuggestFromHistory(),
            completer=WordCompleter(COMMANDS, sentence=True),
            lexer=PygmentsLexer(TransactSqlLexer),
        )
        self.last_sql: str | None = None

    def run(self) -> None:
        self.renderer.console.print(f"sqlshell {__version__} — .help for commands", style="bold cyan")
        while True:
            try:
                line = self.session.prompt("sql> ").strip()
            except (EOFError, KeyboardInterrupt):
                self.renderer.console.print()
                break
            if not line:
                continue
            if line.startswith("."):
                if not self._command(line):
                    break
            else:
                redirection = parse_redirection(line)
                if redirection:
                    self.execute_sql(redirection.sql, redirection.path, redirection.append)
                else:
                    self.execute_sql(line)

    def execute_sql(self, sql: str, output_path: Path | None = None, append: bool = False) -> bool:
        self.last_sql = sql
        try:
            with self.renderer.working():
                results = self.manager.execute(sql)
            if output_path is None:
                self.renderer.results(results)
            else:
                row_count = export_results(results, output_path, append)
                action = "Appended" if append else "Wrote"
                self.renderer.event("connected", f"{action} {row_count:,} row(s) to {output_path.resolve()}")
            return True
        except Exception as exc:
            self.renderer.error(exc)
            return False

    def run_file(self, filename: str) -> bool:
        path = Path(filename).expanduser()
        try:
            script = path.read_text(encoding="utf-8-sig")
        except OSError as exc:
            self.renderer.error(exc)
            return False
        batches = split_batches(script)
        if not batches:
            self.renderer.event("error", f"No SQL found in {path}")
            return False
        ok = True
        for number, batch in enumerate(batches, 1):
            self.renderer.console.rule(f"Batch {number}/{len(batches)}")
            ok = self.execute_sql(batch) and ok
        return ok

    def _command(self, line: str) -> bool:
        try:
            parts = shlex.split(line, posix=False)
        except ValueError as exc:
            self.renderer.error(exc)
            return True
        command = parts[0].lower()
        args = [arg.strip('"') for arg in parts[1:]]
        try:
            if command == ".exit":
                return False
            if command == ".help":
                self.renderer.console.print(
                    ".connect PROFILE\n.run FILE.sql\n.save FILE.sql\n.history\n.exit\n\n"
                    ".edit [FILE.sql] — launch the full-screen editor\n"
                    "Export: SELECT ... > results.csv\nAppend: SELECT ... >> results.csv\n"
                    "Use a quoted filename when it contains spaces. CSV and TSV are supported."
                )
            elif command == ".connect":
                self.manager.connect(self.store.get(self._one_arg(command, args)))
            elif command == ".run":
                self.run_file(self._one_arg(command, args))
            elif command == ".save":
                if self.last_sql is None:
                    raise ValueError("There is no previous query to save")
                Path(self._one_arg(command, args)).expanduser().write_text(self.last_sql + "\n", encoding="utf-8")
                self.renderer.event("connected", "Query saved")
            elif command == ".edit":
                if len(args) > 1:
                    raise ValueError("Usage: .edit [FILE.sql]")
                try:
                    from .editor import launch_editor
                except ImportError as exc:
                    raise RuntimeError(
                        'Editor dependencies are missing; run: python -m pip install "sqlshell[ide]"'
                    ) from exc
                previous_event = self.manager.event
                try:
                    launch_editor(self.manager, self.store, args[0] if args else None)
                finally:
                    self.manager.event = previous_event
            elif command == ".history":
                for number, item in enumerate(self.session.history.get_strings(), 1):
                    self.renderer.console.print(f"{number:>4}  {item}")
            else:
                raise ValueError(f"Unknown command: {command} (use .help)")
        except (OSError, ProfileError, ValueError, RuntimeError) as exc:
            self.renderer.error(exc)
        return True

    @staticmethod
    def _one_arg(command: str, args: list[str]) -> str:
        if len(args) != 1:
            raise ValueError(f"Usage: {command} VALUE")
        return args[0]
