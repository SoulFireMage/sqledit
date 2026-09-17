"""Database engine backends: SQL Server over pyodbc, PostgreSQL over psycopg."""

from __future__ import annotations

from typing import Any

from .profiles import Profile
from .scripts import current_batch, split_batches, split_statements, statement_at_line

ENGINES = ("mssql", "postgres")

MSSQL_CONNECTION_STATES = {"08S01", "08001", "08003", "08004", "08007", "08S02", "HYT00", "HYT01"}
POSTGRES_CONNECTION_STATES = {
    "08000", "08001", "08003", "08004", "08006", "08007", "08P01",
    "57P01", "57P02", "57P03",
}


class CertificateValidationError(RuntimeError):
    """Raised with actionable guidance for an untrusted or unavailable server certificate."""


class AuthenticationError(RuntimeError):
    """Raised with profile inspection guidance after authentication fails."""


class Engine:
    """Everything that differs between the supported database servers."""

    name = ""
    label = ""
    default_database = ""
    default_port: int | None = None
    auth_types: tuple[str, ...] = ()
    multiple_result_sets = True

    def module(self):
        raise NotImplementedError

    def errors(self) -> tuple[type[BaseException], ...]:
        """Driver exception types that ConnectionManager should inspect and retry."""
        try:
            return (self.module().Error,)
        except RuntimeError:
            # The driver is missing; connect() reports that with installation help.
            return ()

    def connect(self, profile: Profile, password: str | None, connector=None):
        raise NotImplementedError

    def apply_query_timeout(self, connection, seconds: int) -> None:
        raise NotImplementedError

    def is_connection_error(self, error: BaseException) -> bool:
        raise NotImplementedError

    def translate_error(self, profile: Profile, error: BaseException) -> BaseException | None:
        """Return a friendlier error for a connect failure, or None to re-raise as is."""
        return None

    def statements(self, sql: str) -> list[str]:
        """Split one submitted buffer into the statements to send to the server."""
        return [sql]

    def split_batches(self, script: str) -> list[str]:
        raise NotImplementedError

    def current_batch(self, script: str, cursor_line: int) -> str:
        raise NotImplementedError

    def batch_label(self) -> str:
        return "batch"

    def describe_target(self, profile: Profile) -> str:
        return f"{profile.server}/{profile.database}"


class MssqlEngine(Engine):
    name = "mssql"
    label = "Microsoft SQL Server"
    default_database = "master"
    default_port = None
    auth_types = ("sql", "windows", "entra")

    def module(self):
        import pyodbc

        return pyodbc

    @staticmethod
    def _escape(value: str) -> str:
        return "{" + value.replace("}", "}}") + "}"

    def connection_string(self, profile: Profile, password: str | None) -> str:
        escape = self._escape
        server = profile.server if profile.port is None else f"{profile.server},{profile.port}"
        parts = [
            f"DRIVER={escape(profile.driver)}",
            f"SERVER={escape(server)}",
            f"DATABASE={escape(profile.database)}",
            f"Encrypt={'yes' if profile.encrypt else 'no'}",
            f"TrustServerCertificate={'yes' if profile.trust_server_certificate else 'no'}",
            "APP=sqlshell",
        ]
        if profile.auth == "windows":
            parts.append("Trusted_Connection=yes")
        elif profile.auth == "entra":
            parts.append("Authentication=ActiveDirectoryInteractive")
            if profile.username:
                parts.append(f"UID={escape(profile.username)}")
        else:
            if password is None:
                raise RuntimeError(f"No stored password found for SQL profile '{profile.name}'")
            parts.extend((f"UID={escape(profile.username or '')}", f"PWD={escape(password)}"))
        return ";".join(parts)

    def connect(self, profile: Profile, password: str | None, connector=None):
        connector = connector or self.module().connect
        return connector(
            self.connection_string(profile, password),
            timeout=profile.login_timeout,
            autocommit=True,
        )

    def apply_query_timeout(self, connection, seconds: int) -> None:
        # pyodbc copies Connection.timeout to each new statement as
        # SQL_ATTR_QUERY_TIMEOUT. Cursor objects do not expose a timeout property.
        connection.timeout = seconds

    def is_connection_error(self, error: BaseException) -> bool:
        text = " ".join(str(arg) for arg in getattr(error, "args", (error,))).upper()
        return any(state in text for state in MSSQL_CONNECTION_STATES)

    def translate_error(self, profile: Profile, error: BaseException) -> BaseException | None:
        text = " ".join(str(arg) for arg in getattr(error, "args", (error,))).lower()
        if "certificate chain was issued by an authority that is not trusted" in text:
            return CertificateValidationError(
                "SQL Server presented a certificate that Windows does not trust. "
                "Prefer installing its issuing CA certificate. For a server you trust, keep TLS encryption "
                "and bypass certificate validation with: "
                f"python -m sqlshell profile configure {profile.name} --trust-server-certificate"
            )
        if "login failed for user" in text or "18456" in str(error):
            return AuthenticationError(
                f"SQL Server rejected the login for profile '{profile.name}'. "
                "Usernames are literal: do not include decorative square brackets. "
                f"Review the profile with 'python -m sqlshell profile show {profile.name}', "
                f"correct it with 'python -m sqlshell profile edit {profile.name}', or replace only "
                f"the password with 'python -m sqlshell profile edit {profile.name} --password'."
            )
        return None

    def split_batches(self, script: str) -> list[str]:
        return split_batches(script)

    def current_batch(self, script: str, cursor_line: int) -> str:
        return current_batch(script, cursor_line)

    def batch_label(self) -> str:
        return "GO batch"


class PostgresEngine(Engine):
    name = "postgres"
    label = "PostgreSQL"
    default_database = "postgres"
    default_port = 5432
    auth_types = ("sql", "none")
    multiple_result_sets = False

    def module(self):
        try:
            import psycopg  # psycopg 3

            return psycopg
        except ImportError:
            pass
        try:
            import psycopg2

            return psycopg2
        except ImportError as exc:
            raise RuntimeError(
                "PostgreSQL support needs the psycopg driver; run: "
                'python -m pip install "sqlshell[postgres]"'
            ) from exc

    def sslmode(self, profile: Profile) -> str:
        if not profile.encrypt:
            return "disable"
        return "require" if profile.trust_server_certificate else "verify-full"

    def connect_arguments(self, profile: Profile, password: str | None) -> dict[str, Any]:
        host, port = profile.server, profile.port
        if port is None and host.count(":") == 1:
            host, _, text = host.partition(":")
            port = int(text) if text.isdigit() else None
        arguments: dict[str, Any] = {
            "host": host,
            "port": port or self.default_port,
            "dbname": profile.database,
            "connect_timeout": profile.login_timeout,
            "sslmode": self.sslmode(profile),
            "application_name": "sqlshell",
        }
        if profile.username:
            arguments["user"] = profile.username
        if profile.auth == "sql":
            if password is None:
                raise RuntimeError(
                    f"No stored password found for PostgreSQL profile '{profile.name}'; "
                    f"set one with 'python -m sqlshell profile edit {profile.name} --password'"
                )
            arguments["password"] = password
        return arguments

    def connect(self, profile: Profile, password: str | None, connector=None):
        connector = connector or self.module().connect
        connection = connector(**self.connect_arguments(profile, password))
        connection.autocommit = True
        return connection

    def apply_query_timeout(self, connection, seconds: int) -> None:
        # PostgreSQL has no client-side query timeout; statement_timeout is enforced
        # by the server and 0 means unlimited, which matches the profile default.
        cursor = connection.cursor()
        try:
            cursor.execute(f"SET statement_timeout = {int(seconds) * 1000}")
        finally:
            cursor.close()

    def is_connection_error(self, error: BaseException) -> bool:
        code = getattr(error, "sqlstate", None) or getattr(error, "pgcode", None)
        if code:
            return str(code) in POSTGRES_CONNECTION_STATES
        module = self.module()
        if isinstance(error, module.InterfaceError):
            return True
        # A lost socket surfaces as OperationalError with no SQLSTATE, because the
        # server never got far enough to send one.
        return isinstance(error, module.OperationalError)

    def translate_error(self, profile: Profile, error: BaseException) -> BaseException | None:
        code = str(getattr(error, "sqlstate", None) or getattr(error, "pgcode", None) or "")
        text = str(error).lower()
        if code in ("28P01", "28000") or "password authentication failed" in text:
            return AuthenticationError(
                f"PostgreSQL rejected the login for profile '{profile.name}'. "
                f"Review the profile with 'python -m sqlshell profile show {profile.name}', "
                f"correct it with 'python -m sqlshell profile edit {profile.name}', or replace only "
                f"the password with 'python -m sqlshell profile edit {profile.name} --password'."
            )
        if "server does not support ssl" in text or "ssl connection has been closed" in text:
            return CertificateValidationError(
                f"The PostgreSQL server at {profile.server} does not offer TLS. "
                "Connect without encryption only on a network you trust: "
                f"python -m sqlshell profile edit {profile.name} --no-encrypt"
            )
        if "certificate" in text and ("verify" in text or "not trusted" in text):
            return CertificateValidationError(
                "The PostgreSQL server certificate could not be verified against the OS trust store. "
                "Prefer installing its issuing CA certificate. For a server you trust, keep TLS encryption "
                "and bypass certificate validation with: "
                f"python -m sqlshell profile configure {profile.name} --trust-server-certificate"
            )
        return None

    def statements(self, sql: str) -> list[str]:
        # psycopg exposes only the final result set of a multi-statement execute, so
        # each statement is sent separately and its results collected in turn.
        return split_statements(sql)

    def split_batches(self, script: str) -> list[str]:
        return split_statements(script)

    def current_batch(self, script: str, cursor_line: int) -> str:
        return statement_at_line(script, cursor_line)

    def batch_label(self) -> str:
        return "statement"

    def describe_target(self, profile: Profile) -> str:
        host = profile.server if profile.port is None else f"{profile.server}:{profile.port}"
        return f"{host}/{profile.database}"


_ENGINES = {engine.name: engine for engine in (MssqlEngine(), PostgresEngine())}


def get_engine(name: str) -> Engine:
    try:
        return _ENGINES[name]
    except KeyError as exc:
        raise ValueError(f"Unknown database engine: {name}") from exc


def engine_for(profile: Profile) -> Engine:
    return get_engine(profile.engine)
