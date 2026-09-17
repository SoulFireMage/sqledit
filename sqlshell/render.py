"""Rich terminal rendering for query output."""

from __future__ import annotations

import json
from contextlib import contextmanager
from typing import Iterable

from rich.console import Console
from rich.table import Table
from rich.text import Text

from .connection import ResultSet


class Renderer:
    def __init__(self, full_width: bool = False, console: Console | None = None) -> None:
        self.full_width = full_width
        self.console = console or Console()

    def event(self, kind: str, message: str) -> None:
        styles = {"connected": "green", "reconnect": "bold yellow", "error": "bold red"}
        self.console.print(message, style=styles.get(kind, "cyan"))

    @contextmanager
    def working(self, message: str = "Running query..."):
        with self.console.status(message, spinner="dots"):
            yield

    def results(self, result_sets: Iterable[ResultSet]) -> None:
        for result in result_sets:
            if result.columns:
                table = Table(show_header=True, header_style="bold cyan", expand=False)
                for column in result.columns:
                    table.add_column(column, overflow="fold" if self.full_width else "ellipsis", no_wrap=not self.full_width)
                for row in result.rows:
                    table.add_row(*(self._format(value) for value in row))
                self.console.print(table)
                self.console.print(f"{len(result.rows):,} row(s)", style="green")
            elif result.row_count >= 0:
                self.console.print(f"Query OK, {result.row_count:,} row(s) affected", style="green")
            else:
                self.console.print("Query OK", style="green")

    @staticmethod
    def _format(value) -> str | Text:
        if value is None:
            return Text("NULL", style="dim italic")
        if isinstance(value, (bytes, bytearray, memoryview)):
            # psycopg returns bytea as memoryview; pyodbc returns bytes.
            return "0x" + bytes(value).hex()
        if isinstance(value, (dict, list)):
            # psycopg decodes json and jsonb into Python objects; show them as JSON
            # rather than as a repr with single quotes.
            return json.dumps(value, default=str)
        return str(value)

    def error(self, error: BaseException) -> None:
        self.console.print(f"Error: {error}", style="bold red")

