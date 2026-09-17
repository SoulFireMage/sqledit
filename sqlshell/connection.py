"""Persistent, retrying pyodbc connection lifecycle."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Callable, Sequence

import pyodbc

from .profiles import Profile, ProfileStore

CONNECTION_STATES = {"08S01", "08001", "08003", "08004", "08007", "08S02", "HYT00", "HYT01"}


@dataclass
class ResultSet:
    columns: list[str]
    rows: list[tuple]
    row_count: int = -1
    truncated: bool = False


class CertificateValidationError(RuntimeError):
    """Raised with actionable guidance for an untrusted SQL Server certificate."""


class AuthenticationError(RuntimeError):
    """Raised with profile inspection guidance after SQL authentication fails."""


def is_certificate_validation_error(error: BaseException) -> bool:
    text = " ".join(str(arg) for arg in getattr(error, "args", (error,))).lower()
    return "certificate chain was issued by an authority that is not trusted" in text


def is_connection_error(error: BaseException) -> bool:
    text = " ".join(str(arg) for arg in getattr(error, "args", (error,))).upper()
    return any(state in text for state in CONNECTION_STATES)


def _escape(value: str) -> str:
    return "{" + value.replace("}", "}}") + "}"


def build_connection_string(profile: Profile, password: str | None) -> str:
    parts = [
        f"DRIVER={_escape(profile.driver)}",
        f"SERVER={_escape(profile.server)}",
        f"DATABASE={_escape(profile.database)}",
        f"Encrypt={'yes' if profile.encrypt else 'no'}",
        f"TrustServerCertificate={'yes' if profile.trust_server_certificate else 'no'}",
        "APP=sqlshell",
    ]
    if profile.auth == "windows":
        parts.append("Trusted_Connection=yes")
    elif profile.auth == "entra":
        parts.append("Authentication=ActiveDirectoryInteractive")
        if profile.username:
            parts.append(f"UID={_escape(profile.username)}")
    else:
        if password is None:
            raise RuntimeError(f"No stored password found for SQL profile '{profile.name}'")
        parts.extend((f"UID={_escape(profile.username or '')}", f"PWD={_escape(password)}"))
    return ";".join(parts)


class ConnectionManager:
    def __init__(self, store: ProfileStore, event: Callable[[str, str], None] | None = None, connector=None) -> None:
        self.store = store
        self.event = event or (lambda _kind, _message: None)
        self.connector = connector or pyodbc.connect
        self.profile: Profile | None = None
        self.connection = None

    def connect(self, profile: Profile) -> None:
        self.close()
        connection_string = build_connection_string(profile, self.store.password(profile))
        try:
            self.connection = self.connector(connection_string, timeout=profile.login_timeout, autocommit=True)
        except pyodbc.Error as exc:
            if is_certificate_validation_error(exc):
                raise CertificateValidationError(
                    "SQL Server presented a certificate that Windows does not trust. "
                    "Prefer installing its issuing CA certificate. For a server you trust, keep TLS encryption "
                    "and bypass certificate validation with: "
                    f"python -m sqlshell profile configure {profile.name} --trust-server-certificate"
                ) from exc
            if "login failed for user" in str(exc).lower() or "18456" in str(exc):
                raise AuthenticationError(
                    f"SQL Server rejected the login for profile '{profile.name}'. "
                    "Usernames are literal: do not include decorative square brackets. "
                    f"Review the profile with 'python -m sqlshell profile show {profile.name}', "
                    f"correct it with 'python -m sqlshell profile edit {profile.name}', or replace only "
                    f"the password with 'python -m sqlshell profile edit {profile.name} --password'."
                ) from exc
            raise
        # pyodbc copies Connection.timeout to each new statement as
        # SQL_ATTR_QUERY_TIMEOUT. Cursor objects do not expose a timeout property.
        self.connection.timeout = profile.query_timeout
        self.profile = profile
        self.event("connected", f"Connected to {profile.server}/{profile.database} as profile '{profile.name}'")

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
        except pyodbc.Error as exc:
            if not is_connection_error(exc):
                raise
            self.reconnect()
            return self._execute_once(sql, max_rows=max_rows)

    def _execute_once(self, sql: str, *, max_rows: int | None = None) -> list[ResultSet]:
        cursor = self.connection.cursor()
        results: list[ResultSet] = []
        try:
            cursor.execute(sql)
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
                if not cursor.nextset():
                    break
            return results
        finally:
            cursor.close()

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
