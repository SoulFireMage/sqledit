"""SQL script batch parsing for SQL Server (GO) and PostgreSQL (semicolons)."""

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


_IDENTIFIER_START = re.compile(r"[^\W\d]")
_IDENTIFIER_BODY = re.compile(r"\w")


def _dollar_tag(script: str, index: int) -> str | None:
    """Return the dollar-quote opener at index, for example '$$' or '$body$'."""
    end = index + 1
    if end < len(script) and _IDENTIFIER_START.match(script[end]):
        end += 1
        while end < len(script) and _IDENTIFIER_BODY.match(script[end]):
            end += 1
    if end < len(script) and script[end] == "$":
        return script[index : end + 1]
    return None


def _has_code(statement: str) -> bool:
    """True when a statement holds SQL rather than only comments."""
    remainder = re.sub(r"/\*.*?\*/", " ", re.sub(r"--[^\n]*", " ", statement), flags=re.DOTALL)
    return bool(remainder.strip())


def statement_spans(script: str) -> list[tuple[str, int, int]]:
    """Split PostgreSQL text on top-level semicolons.

    Quoted strings and identifiers, dollar-quoted bodies, line comments and nested
    block comments all hide a semicolon from the splitter, so function bodies and
    DO blocks survive intact. Each item is a statement with its zero-based first
    and last line in the script.
    """
    spans: list[tuple[str, int, int]] = []
    start = 0
    start_line = 0
    line = 0
    index = 0
    length = len(script)
    while index < length:
        char = script[index]
        if char == "\n":
            line += 1
            index += 1
            continue
        if script.startswith("--", index):
            newline = script.find("\n", index)
            index = length if newline == -1 else newline
            continue
        if script.startswith("/*", index):
            depth = 1
            index += 2
            while index < length and depth:
                if script.startswith("/*", index):
                    depth += 1
                    index += 2
                elif script.startswith("*/", index):
                    depth -= 1
                    index += 2
                else:
                    line += script[index] == "\n"
                    index += 1
            continue
        if char in "'\"":
            backslash_escapes = char == "'" and index > 0 and script[index - 1] in "Ee"
            index += 1
            while index < length:
                if backslash_escapes and script[index] == "\\":
                    index += 2
                    continue
                if script[index] == char:
                    if script.startswith(char + char, index):
                        index += 2
                        continue
                    index += 1
                    break
                line += script[index] == "\n"
                index += 1
            continue
        if char == "$":
            tag = _dollar_tag(script, index)
            if tag:
                closing = script.find(tag, index + len(tag))
                end = length if closing == -1 else closing + len(tag)
                line += script.count("\n", index, end)
                index = end
                continue
        if char == ";":
            statement = script[start:index].strip()
            if _has_code(statement):
                spans.append((statement, start_line, line))
            index += 1
            start = index
            start_line = line
            continue
        index += 1
    statement = script[start:].strip()
    if _has_code(statement):
        spans.append((statement, start_line, line))
    return spans


def split_statements(script: str) -> list[str]:
    """Return the PostgreSQL statements in a script, without their terminators."""
    return [statement for statement, _start, _end in statement_spans(script)]


def statement_at_line(script: str, cursor_line: int) -> str:
    """Return the PostgreSQL statement containing a zero-based cursor line."""
    spans = statement_spans(script)
    if not spans:
        return ""
    for statement, start, end in spans:
        if start <= cursor_line <= end:
            return statement
    return spans[-1][0] if cursor_line > spans[-1][2] else spans[0][0]
