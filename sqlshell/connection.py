"""Persistent, retrying connection lifecycle shared by every engine."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Callable

from .engines import (
    MSSQL_CONNECTION_STATES as CONNECTION_STATES,  # pre-engine name kept for callers
    AuthenticationError,
    CertificateValidationError,
    Engine,
    engine_for,
    get_engine,
)
from .profiles import Profile, ProfileStore

__all__ = [
    "AuthenticationError",
    "CertificateValidationError",
    "CONNECTION_STATES",
    "ConnectionManager",
    "ResultSet",
    "build_connection_string",
    "is_certificate_validation_error",
    "is_connection_error",
]


@dataclass
class ResultSet:
    columns: list[str]
    rows: list[tuple]
    row_count: int = -1
    truncated: bool = False


def is_certificate_validation_error(error: BaseException) -> bool:
    text = " ".join(str(arg) for arg in getattr(error, "args", (error,))).lower()
    return "certificate chain was issued by an authority that is not trusted" in text


def is_connection_error(error: BaseException, engine: Engine | str = "mssql") -> bool:
    resolved = engine if isinstance(engine, Engine) else get_engine(engine)
    return resolved.is_connection_error(error)


def build_connection_string(profile: Profile, password: str | None) -> str:
    """SQL Server ODBC connection string; PostgreSQL profiles use keyword arguments."""
    return get_engine("mssql").connection_string(profile, password)


class ConnectionManager:
    def __init__(self, store: ProfileStore, event: Callable[[str, str], None] | None = None, connector=None) -> None:
        self.store = store
        self.event = event or (lambda _kind, _message: None)
        self.connector = connector
        self.profile: Profile | None = None
        self.engine: Engine = get_engine("mssql")
        self.connection = None

    def connect(self, profile: Profile) -> None:
        self.close()
        engine = engine_for(profile)
        try:
            self.connection = engine.connect(profile, self.store.password(profile), self.connector)
            engine.apply_query_timeout(self.connection, profile.query_timeout)
        except engine.errors() as exc:
            translated = engine.translate_error(profile, exc)
            if translated is not None:
                raise translated from exc
            raise
        self.engine = engine
        self.profile = profile
        self.event(
            "connected",
            f"Connected to {engine.describe_target(profile)} as profile '{profile.name}' ({engine.label})",
        )

    def reconnect(self) -> None:
        if self.profile is None:
            raise RuntimeError("No profile is connected")
        profile = self.profile
        self.event("reconnect", "Connection was lost; reconnecting and retrying once...")
        self.connect(profile)
        self.event("reconnect", "Reconnected successfully")

    def execute(self, sql: str, *, max_rows: int | None = None) -> list[ResultSet]:
        if self.connection is None:
            raise RuntimeError("Not connected")
        try:
            return self._execute_once(sql, max_rows=max_rows)
        except self.engine.errors() as exc:
            if not self.engine.is_connection_error(exc):
                raise
            self.reconnect()
            return self._execute_once(sql, max_rows=max_rows)

    def _execute_once(self, sql: str, *, max_rows: int | None = None) -> list[ResultSet]:
        results: list[ResultSet] = []
        for statement in self.engine.statements(sql):
            cursor = self.connection.cursor()
            try:
                cursor.execute(statement)
                self._collect(cursor, results, max_rows, self.engine.multiple_result_sets)
            finally:
                cursor.close()
        return results

    @staticmethod
    def _collect(cursor, results: list[ResultSet], max_rows: int | None, follow_nextset: bool) -> None:
        while True:
            if cursor.description:
                columns = [str(item[0]) for item in cursor.description]
                if max_rows is None:
                    rows = [tuple(row) for row in cursor.fetchall()]
                    truncated = False
                else:
                    fetched = cursor.fetchmany(max_rows + 1)
                    truncated = len(fetched) > max_rows
                    rows = [tuple(row) for row in fetched[:max_rows]]
                results.append(ResultSet(columns, rows, cursor.rowcount, truncated))
            else:
                results.append(ResultSet([], [], cursor.rowcount))
            # pyodbc walks every result set of a batch; psycopg produces one per
            # statement, and psycopg2 raises rather than reporting there are no more.
            if not follow_nextset or not cursor.nextset():
                break

    def close(self) -> None:
        if self.connection is not None:
            try:
                self.connection.close()
            finally:
                self.connection = None

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        self.close()
