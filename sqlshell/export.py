"""Delimited result export and shell-style redirection parsing."""

from __future__ import annotations

import csv
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

from .connection import ResultSet

SUPPORTED_SUFFIXES = {".csv": ",", ".tsv": "\t"}
REDIRECTION = re.compile(
    r'^(?P<sql>.*\S)\s+(?P<operator>>>?)\s+(?P<path>"[^"]+"|\S+)\s*$',
    re.DOTALL,
)


@dataclass(frozen=True)
class Redirection:
    sql: str
    path: Path
    append: bool


def parse_redirection(line: str) -> Redirection | None:
    """Recognize a final > file.csv/tsv without consuming SQL > comparisons."""
    match = REDIRECTION.match(line)
    if not match:
        return None
    raw_path = match.group("path")
    if len(raw_path) >= 2 and raw_path[0] == raw_path[-1] == '"':
        raw_path = raw_path[1:-1]
    path = Path(raw_path).expanduser()
    if path.suffix.lower() not in SUPPORTED_SUFFIXES:
        return None
    return Redirection(match.group("sql").rstrip(), path, match.group("operator") == ">>")


def export_results(results: Iterable[ResultSet], path: Path, append: bool = False) -> int:
    """Write the single tabular result set to CSV/TSV and return its row count."""
    tabular = [result for result in results if result.columns]
    if not tabular:
        raise ValueError("The query did not return a tabular result set to export")
    if len(tabular) > 1:
        raise ValueError("The query returned multiple result sets; export one SELECT at a time")
    delimiter = SUPPORTED_SUFFIXES.get(path.suffix.lower())
    if delimiter is None:
        raise ValueError("Output filename must end in .csv or .tsv")
    path.parent.mkdir(parents=True, exist_ok=True)
    write_header = not append or not path.exists() or path.stat().st_size == 0
    mode = "a" if append else "w"
    # utf-8-sig makes newly-created exports open cleanly in Excel. When appending,
    # utf-8 avoids inserting another BOM into an existing file.
    encoding = "utf-8" if append and path.exists() and path.stat().st_size else "utf-8-sig"
    result = tabular[0]
    with path.open(mode, encoding=encoding, newline="") as handle:
        writer = csv.writer(handle, delimiter=delimiter)
        if write_header:
            writer.writerow(result.columns)
        writer.writerows(_export_row(row) for row in result.rows)
    return len(result.rows)


def _export_row(row: tuple) -> list[str]:
    values = []
    for value in row:
        if value is None:
            values.append("")
        elif isinstance(value, bytes):
            values.append("0x" + value.hex())
        else:
            values.append(str(value))
    return values
