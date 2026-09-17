import pyodbc

from sqlshell.connection import (
    AuthenticationError,
    CertificateValidationError,
    ConnectionManager,
    is_connection_error,
)
from sqlshell.profiles import Profile


class Store:
    def password(self, _profile):
        return None


class Cursor:
    __slots__ = ()
    description = [("answer",)]
    rowcount = 1

    def execute(self, _sql):
        return self

    def fetchall(self):
        return [(42,)]

    def fetchmany(self, size):
        return [(number,) for number in range(min(size, 3))]

    def nextset(self):
        return False

    def close(self):
        pass


class Connection:
    def __init__(self, fail=False):
        self.fail = fail
        self.closed = False
        self.timeout = None

    def cursor(self):
        if self.fail:
            raise pyodbc.OperationalError("08S01", "link failure")
        return Cursor()

    def close(self):
        self.closed = True


def test_connection_error_detection():
    assert is_connection_error(pyodbc.OperationalError("08S01", "link failure"))
    assert not is_connection_error(pyodbc.ProgrammingError("42000", "syntax"))


def test_reconnects_and_retries_once():
    connections = [Connection(fail=True), Connection()]
    events = []

    def connector(*_args, **_kwargs):
        return connections.pop(0)

    manager = ConnectionManager(Store(), lambda kind, message: events.append((kind, message)), connector)
    manager.connect(Profile("work", "server"))
    result = manager.execute("SELECT 42")
    assert result[0].rows == [(42,)]
    assert sum(kind == "reconnect" for kind, _ in events) == 2


def test_query_timeout_is_set_on_connection_not_cursor():
    connection = Connection()
    manager = ConnectionManager(Store(), connector=lambda *_args, **_kwargs: connection)
    manager.connect(Profile("work", "server", query_timeout=37))
    assert connection.timeout == 37
    assert manager.execute("SELECT 42")[0].rows == [(42,)]


def test_editor_result_limit_marks_truncation():
    manager = ConnectionManager(Store(), connector=lambda *_args, **_kwargs: Connection())
    manager.connect(Profile("work", "server"))
    result = manager.execute("SELECT lots", max_rows=2)[0]
    assert result.rows == [(0,), (1,)]
    assert result.truncated is True


def test_does_not_retry_sql_errors():
    class BadCursorConnection(Connection):
        def cursor(self):
            raise pyodbc.ProgrammingError("42000", "syntax error")

    calls = []

    def connector(*_args, **_kwargs):
        calls.append(1)
        return BadCursorConnection()

    manager = ConnectionManager(Store(), connector=connector)
    manager.connect(Profile("work", "server"))
    try:
        manager.execute("BAD SQL")
        assert False, "expected an error"
    except pyodbc.ProgrammingError:
        pass
    assert len(calls) == 1


def test_certificate_error_has_profile_specific_fix():
    def connector(*_args, **_kwargs):
        raise pyodbc.OperationalError(
            "08001", "SSL Provider: The certificate chain was issued by an authority that is not trusted."
        )

    manager = ConnectionManager(Store(), connector=connector)
    try:
        manager.connect(Profile("work", "server"))
        assert False, "expected an error"
    except CertificateValidationError as exc:
        assert "profile configure work --trust-server-certificate" in str(exc)


def test_login_error_has_profile_inspection_guidance():
    def connector(*_args, **_kwargs):
        raise pyodbc.OperationalError("28000", "Login failed for user '[someone]'. (18456)")

    manager = ConnectionManager(Store(), connector=connector)
    try:
        manager.connect(Profile("work", "server"))
        assert False, "expected an error"
    except AuthenticationError as exc:
        assert "profile show work" in str(exc)
        assert "square brackets" in str(exc)
