import pytest

from sqlshell.connection import AuthenticationError, CertificateValidationError, ConnectionManager
from sqlshell.engines import get_engine
from sqlshell.profiles import Profile, ProfileError
from sqlshell.scripts import split_statements, statement_at_line

POSTGRES = get_engine("postgres")
# Whichever of psycopg 3 or psycopg2 is installed, exactly as the engine picks it.
driver = POSTGRES.module()


class Store:
    def __init__(self, password="secret"):
        self._password = password

    def password(self, profile):
        return self._password if profile.auth == "sql" else None


class Cursor:
    def __init__(self, connection):
        self.connection = connection
        self.description = None
        self.rowcount = -1

    def execute(self, sql):
        self.connection.executed.append(sql)
        if sql.lower().startswith("select"):
            self.description = [("answer",)]
            self.rowcount = 1
        return self

    def fetchall(self):
        return [(42,)]

    def fetchmany(self, size):
        return [(number,) for number in range(min(size, 3))]

    def nextset(self):
        raise driver.NotSupportedError("psycopg2 has no multiple result sets")

    def close(self):
        pass


class Connection:
    def __init__(self, **kwargs):
        self.kwargs = kwargs
        self.executed = []
        self.autocommit = False
        self.closed = False

    def cursor(self):
        return Cursor(self)

    def close(self):
        self.closed = True


def postgres_profile(**changes) -> Profile:
    values = {
        "name": "pg",
        "server": "192.168.0.94",
        "database": "mydb",
        "engine": "postgres",
        "auth": "sql",
        "username": "postgres",
        "encrypt": False,
    }
    values.update(changes)
    return Profile(**values)


def test_postgres_connect_arguments_use_keywords_not_odbc():
    arguments = POSTGRES.connect_arguments(postgres_profile(port=5433), "secret")
    assert arguments == {
        "host": "192.168.0.94",
        "port": 5433,
        "dbname": "mydb",
        "connect_timeout": 8,
        "sslmode": "disable",
        "application_name": "sqlshell",
        "user": "postgres",
        "password": "secret",
    }


def test_postgres_defaults_to_5432_and_reads_a_port_in_the_server():
    assert POSTGRES.connect_arguments(postgres_profile(), "secret")["port"] == 5432
    arguments = POSTGRES.connect_arguments(postgres_profile(server="db.example:6543"), "secret")
    assert (arguments["host"], arguments["port"]) == ("db.example", 6543)


@pytest.mark.parametrize(
    ("encrypt", "trust", "expected"),
    [(False, False, "disable"), (True, True, "require"), (True, False, "verify-full")],
)
def test_sslmode_follows_the_profile_tls_settings(encrypt, trust, expected):
    profile = postgres_profile(encrypt=encrypt, trust_server_certificate=trust)
    assert POSTGRES.sslmode(profile) == expected


def test_postgres_connection_sets_autocommit_and_statement_timeout():
    connection = Connection()
    manager = ConnectionManager(Store(), connector=lambda **kwargs: connection)
    manager.connect(postgres_profile(query_timeout=37))
    assert connection.autocommit is True
    assert connection.executed == ["SET statement_timeout = 37000"]


def test_postgres_runs_each_statement_and_collects_every_result():
    connection = Connection()
    manager = ConnectionManager(Store(), connector=lambda **kwargs: connection)
    manager.connect(postgres_profile())
    results = manager.execute("select 1; update t set a = 1; select 2")
    assert [result.rows for result in results] == [[(42,)], [], [(42,)]]
    assert connection.executed[1:] == ["select 1", "update t set a = 1", "select 2"]


class DroppedConnection(Connection):
    """Serves the connect-time statement_timeout, then loses the server."""

    def cursor(self):
        if not self.executed:
            return Cursor(self)
        raise driver.OperationalError("server closed the connection unexpectedly")


class SyntaxError42601(driver.ProgrammingError):
    pgcode = "42601"


class LoginFailed28P01(driver.OperationalError):
    pgcode = "28P01"


def test_postgres_connection_loss_is_retried_once():
    connections = [DroppedConnection(), Connection()]
    events = []
    manager = ConnectionManager(
        Store(), lambda kind, message: events.append(kind), lambda **kwargs: connections.pop(0)
    )
    manager.connect(postgres_profile())
    assert manager.execute("select 1")[0].rows == [(42,)]
    assert events.count("reconnect") == 2


def test_postgres_syntax_errors_are_not_retried():
    class Broken(Connection):
        def cursor(self):
            if not self.executed:
                return Cursor(self)
            raise SyntaxError42601('syntax error at or near "slect"')

    connections = []

    def connector(**kwargs):
        connections.append(kwargs)
        return Broken(**kwargs)

    manager = ConnectionManager(Store(), connector=connector)
    manager.connect(postgres_profile())
    with pytest.raises(driver.ProgrammingError):
        manager.execute("slect 1")
    assert len(connections) == 1


def test_postgres_login_failure_explains_how_to_fix_the_profile():
    def connector(**_kwargs):
        raise LoginFailed28P01('password authentication failed for user "postgres"')

    manager = ConnectionManager(Store(), connector=connector)
    with pytest.raises(AuthenticationError) as caught:
        manager.connect(postgres_profile())
    assert "profile show pg" in str(caught.value)


def test_postgres_without_tls_points_at_the_encryption_switch():
    def connector(**_kwargs):
        raise driver.OperationalError("server does not support SSL, but SSL was required")

    manager = ConnectionManager(Store(), connector=connector)
    with pytest.raises(CertificateValidationError) as caught:
        manager.connect(postgres_profile(encrypt=True))
    assert "profile edit pg --no-encrypt" in str(caught.value)


def test_postgres_profiles_reject_windows_authentication():
    with pytest.raises(ProfileError):
        postgres_profile(auth="windows", username=None).validate()


def test_unknown_engine_is_rejected():
    with pytest.raises(ProfileError):
        Profile("p", "server", engine="oracle").validate()


def test_statements_survive_semicolons_in_strings_and_dollar_quotes():
    script = (
        "select 'a;b' as x;\n"
        "do $$ begin raise notice 'hi;'; end $$;\n"
        "-- a trailing; comment\n"
        "select 2\n"
    )
    statements = split_statements(script)
    assert statements[0] == "select 'a;b' as x"
    assert statements[1].startswith("do $$") and statements[1].endswith("$$")
    assert statements[2].endswith("select 2")


def test_comment_only_text_produces_no_statements():
    assert split_statements("-- nothing here\n/* nor here */\n") == []


def test_statement_at_line_finds_the_statement_under_the_cursor():
    script = "select 1;\nselect\n  2;\nselect 3\n"
    assert statement_at_line(script, 0) == "select 1"
    assert statement_at_line(script, 2) == "select\n  2"
    assert statement_at_line(script, 3) == "select 3"


def test_engine_batch_splitting_differs_between_servers():
    script = "select 1;\nselect 2\nGO\nselect 3\n"
    assert get_engine("mssql").split_batches(script) == ["select 1;\nselect 2", "select 3"]
    assert get_engine("postgres").split_batches(script) == ["select 1", "select 2\nGO\nselect 3"]


def test_profile_add_prompts_offer_postgres_defaults():
    from sqlshell.profiles import prompt_for_profile

    answers = iter(["postgres", "192.168.0.94", "", "mydb", "", "postgres", "n", ""])
    profile, password = prompt_for_profile(
        "pg", input_fn=lambda _prompt: next(answers), password_fn=lambda _prompt: "postgres123"
    )
    assert (profile.engine, profile.auth, profile.database) == ("postgres", "sql", "mydb")
    assert (profile.port, profile.encrypt, profile.trust_server_certificate) == (None, False, False)
    assert password == "postgres123"


def test_profile_add_still_defaults_to_sql_server():
    from sqlshell.profiles import prompt_for_profile

    answers = iter(["", "sql01", "", "", "", "n"])
    profile, password = prompt_for_profile("work", input_fn=lambda _prompt: next(answers))
    assert (profile.engine, profile.auth, profile.database) == ("mssql", "windows", "master")
    assert profile.encrypt is True and password is None
