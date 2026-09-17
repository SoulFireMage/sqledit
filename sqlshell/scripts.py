"""SQL Server script batch parsing."""

from __future__ import annotations

import re

GO_LINE = re.compile(r"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$", re.IGNORECASE)


def split_batches(script: str) -> list[str]:
    """Split on sqlcmd-style GO lines, retaining semicolons inside batches."""
    batches: list[str] = []
    current: list[str] = []
    for line in script.splitlines(keepends=True):
        match = GO_LINE.match(line.rstrip("\r\n"))
        if not match:
            current.append(line)
            continue
        batch = "".join(current).strip()
        if batch:
            batches.extend([batch] * int(match.group(1) or 1))
        current = []
    batch = "".join(current).strip()
    if batch:
        batches.append(batch)
    return batches


def current_batch(script: str, cursor_line: int) -> str:
    """Return the GO-delimited batch containing a zero-based cursor line."""
    lines = script.splitlines()
    if not lines:
        return ""
    cursor_line = max(0, min(cursor_line, len(lines) - 1))
    start = 0
    end = len(lines)
    for index, line in enumerate(lines):
        if not GO_LINE.match(line):
            continue
        if index < cursor_line:
            start = index + 1
        else:
            end = index
            break
    return "\n".join(lines[start:end]).strip()
