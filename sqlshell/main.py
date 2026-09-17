"""Command-line entry point."""

from __future__ import annotations

import argparse
import getpass
import sys

import pyodbc
from rich.console import Console
from rich.table import Table

from . import __version__
from .connection import ConnectionManager
from .profiles import AUTH_TYPES, Profile, ProfileError, ProfileStore, prompt_for_profile
from .render import Renderer
from .shell import SqlShell


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(prog="sqlshell", description="Resilient SQL Server terminal client")
    result.add_argument("--version", action="version", version=f"sqlshell {__version__}")
    result.add_argument("-p", "--profile", help="connection profile name")
    result.add_argument("-f", "--file", help="execute a SQL file and exit")
    result.add_argument("-x", "--full-width", action="store_true", help="wrap full values instead of truncating cells")
    sub = result.add_subparsers(dest="command")
    edit_app = sub.add_parser("edit", help="launch the full-screen SQL editor")
    edit_app.add_argument("file", nargs="?", help="SQL file to open")
    edit_app.add_argument("-p", "--profile", dest="edit_profile", help="connection profile name")
    edit_app.add_argument("--max-rows", type=int, default=5000, help="maximum rows displayed per result set")
    profiles = sub.add_parser("profile", help="manage connection profiles")
    profile_sub = profiles.add_subparsers(dest="profile_command", required=True)
    add = profile_sub.add_parser("add")
    add.add_argument("name")
    profile_sub.add_parser("list")
    show = profile_sub.add_parser("show", help="show all non-secret profile details")
    show.add_argument("name")
    use = profile_sub.add_parser("use")
    use.add_argument("name")
    remove = profile_sub.add_parser("remove")
    remove.add_argument("name")
    edit = profile_sub.add_parser("edit", help="edit a profile or replace its stored password")
    edit.add_argument("name")
    edit.add_argument("--password", action="store_true", help="securely prompt for a replacement password")
    edit.add_argument("--server", help="new server name or address")
    edit.add_argument("--database", help="new default database")
    edit.add_argument("--username", help="new SQL-authentication username")
    edit.add_argument("--auth", choices=AUTH_TYPES, help="new authentication mode")
    edit.add_argument("--driver", help="new ODBC driver name")
    edit.add_argument("--login-timeout", type=int, help="new login timeout in seconds")
    edit.add_argument("--query-timeout", type=int, help="new query timeout in seconds; 0 means unlimited")
    edit.add_argument("--encrypt", action=argparse.BooleanOptionalAction, default=None, help="enable or disable TLS encryption")
    edit_trust = edit.add_mutually_exclusive_group()
    edit_trust.add_argument("--trust-server-certificate", dest="trust_server_certificate", action="store_true")
    edit_trust.add_argument("--validate-server-certificate", dest="trust_server_certificate", action="store_false")
    edit.set_defaults(trust_server_certificate=None)
    configure = profile_sub.add_parser("configure", help="change TLS settings without re-entering credentials")
    configure.add_argument("name")
    trust = configure.add_mutually_exclusive_group(required=True)
    trust.add_argument(
        "--trust-server-certificate",
        dest="trust_server_certificate",
        action="store_true",
        help="keep encryption but skip server certificate validation",
    )
    trust.add_argument(
        "--validate-server-certificate",
        dest="trust_server_certificate",
        action="store_false",
        help="require a certificate chaining to a trusted CA (default)",
    )
    sub.add_parser("doctor", help="check Python and installed ODBC drivers")
    return result


def profile_command(args, store: ProfileStore, console: Console) -> int:
    if args.profile_command == "add":
        profile, password = prompt_for_profile(args.name)
        store.save(profile, password)
        console.print(f"Saved profile '{profile.name}'", style="green")
    elif args.profile_command == "list":
        try:
            active = store.get().name
        except ProfileError:
            active = None
        table = Table("", "Name", "Server", "Database", "Auth")
        for item in store.list():
            table.add_row("*" if item.name == active else "", item.name, item.server, item.database, item.auth)
        console.print(table)
    elif args.profile_command == "show":
        profile = store.get(args.name)
        _show_profile(profile, store, console)
    elif args.profile_command == "use":
        store.use(args.name)
        console.print(f"Active profile is now '{args.name}'", style="green")
    elif args.profile_command == "remove":
        store.remove(args.name)
        console.print(f"Removed profile '{args.name}'", style="green")
    elif args.profile_command == "edit":
        changes = {
            "server": args.server,
            "database": args.database,
            "username": args.username,
            "auth": args.auth,
            "driver": args.driver,
            "login_timeout": args.login_timeout,
            "query_timeout": args.query_timeout,
            "encrypt": args.encrypt,
            "trust_server_certificate": args.trust_server_certificate,
        }
        if not args.password and all(value is None for value in changes.values()):
            changes = _interactive_profile_changes(store.get(args.name))
        password = None
        if args.password:
            password = getpass.getpass("New password: ")
            confirmation = getpass.getpass("Confirm new password: ")
            if password != confirmation:
                raise ProfileError("Passwords do not match; the profile was not changed")
        profile = store.edit(args.name, password=password, **changes)
        console.print(f"Updated profile '{profile.name}'", style="green")
        _show_profile(profile, store, console)
    elif args.profile_command == "configure":
        profile = store.configure_security(
            args.name, trust_server_certificate=args.trust_server_certificate
        )
        state = "trusted without validation" if profile.trust_server_certificate else "validated against the OS trust store"
        console.print(
            f"Updated '{profile.name}': TLS remains enabled; the server certificate is now {state}.",
            style="yellow" if profile.trust_server_certificate else "green",
        )
    return 0


def _show_profile(profile: Profile, store: ProfileStore, console: Console) -> None:
    password_status = "not used"
    if profile.auth == "sql":
        password_status = "stored in credential manager" if store.password(profile) is not None else "MISSING"
    rows = [
        ("Name", profile.name),
        ("Server", profile.server),
        ("Database", profile.database),
        ("Authentication", profile.auth),
        ("Username", profile.username or "—"),
        ("Password", password_status),
        ("ODBC driver", profile.driver),
        ("Encrypt", "yes" if profile.encrypt else "no"),
        ("Trust server certificate", "yes" if profile.trust_server_certificate else "no"),
        ("Login timeout", f"{profile.login_timeout} seconds"),
        ("Query timeout", "unlimited" if profile.query_timeout == 0 else f"{profile.query_timeout} seconds"),
    ]
    table = Table("Setting", "Value", title=f"Profile: {profile.name}")
    for field, value in rows:
        table.add_row(field, str(value))
    console.print(table)
    console.print("Values are shown literally; square brackets are part of a server, database, or username value.", style="dim")


def _interactive_profile_changes(profile: Profile) -> dict:
    print("Press Enter to keep the value shown in brackets. Password is not changed here.")

    def text_value(label: str, current: str) -> str:
        return input(f"{label} [{current}]: ").strip() or current

    def bool_value(label: str, current: bool) -> bool:
        answer = input(f"{label} [{'Y/n' if current else 'y/N'}]: ").strip().lower()
        if not answer:
            return current
        if answer in ("y", "yes"):
            return True
        if answer in ("n", "no"):
            return False
        raise ProfileError(f"Please answer yes or no for {label.lower()}")

    server = text_value("Server", profile.server)
    database = text_value("Database", profile.database)
    auth = text_value("Authentication (sql/windows/entra)", profile.auth).lower()
    if auth not in AUTH_TYPES:
        raise ProfileError(f"Unknown authentication type: {auth}")
    username = profile.username
    if auth == "sql":
        username = text_value("Username", profile.username or "")
    driver = text_value("ODBC driver", profile.driver)
    encrypt = bool_value("Encrypt connection", profile.encrypt)
    trust = bool_value("Trust server certificate without validation", profile.trust_server_certificate)
    try:
        login_timeout = int(text_value("Login timeout seconds", str(profile.login_timeout)))
        query_timeout = int(text_value("Query timeout seconds (0 = unlimited)", str(profile.query_timeout)))
    except ValueError as exc:
        raise ProfileError("Timeouts must be whole numbers") from exc
    return {
        "server": server,
        "database": database,
        "auth": auth,
        "username": username,
        "driver": driver,
        "encrypt": encrypt,
        "trust_server_certificate": trust,
        "login_timeout": login_timeout,
        "query_timeout": query_timeout,
    }


def doctor(console: Console) -> int:
    drivers = pyodbc.drivers()
    sql_drivers = [item for item in drivers if "ODBC Driver" in item and "SQL Server" in item]
    console.print(f"Python: {sys.version.split()[0]}")
    console.print("SQL Server ODBC drivers: " + (", ".join(sql_drivers) or "none"))
    try:
        import textual

        console.print(f"Terminal IDE: Textual {textual.__version__}")
    except ImportError:
        console.print('Terminal IDE: not installed (install with "sqlshell[ide]")')
    if not sql_drivers:
        console.print("Install Microsoft ODBC Driver 18 for SQL Server.", style="bold red")
        return 1
    console.print("Environment is ready.", style="green")
    return 0


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    console = Console()
    store = ProfileStore()
    try:
        if args.command == "profile":
            return profile_command(args, store, console)
        if args.command == "doctor":
            return doctor(console)
        if args.command == "edit":
            if args.max_rows < 1:
                raise ProfileError("--max-rows must be at least 1")
            try:
                from .editor import launch_editor
            except ImportError as exc:
                raise RuntimeError(
                    'The editor dependencies are not installed; run: python -m pip install "sqlshell[ide]"'
                ) from exc
            manager = ConnectionManager(store, lambda kind, message: console.print(message))
            manager.connect(store.get(args.edit_profile or args.profile))
            try:
                launch_editor(manager, store, args.file, max_rows=args.max_rows)
                return 0
            finally:
                manager.close()
        renderer = Renderer(args.full_width, console)
        manager = ConnectionManager(store, renderer.event)
        manager.connect(store.get(args.profile))
        try:
            shell = SqlShell(manager, store, renderer)
            if args.file:
                return 0 if shell.run_file(args.file) else 1
            shell.run()
            return 0
        finally:
            manager.close()
    except (OSError, ProfileError, RuntimeError, pyodbc.Error) as exc:
        console.print(f"Error: {exc}", style="bold red")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
