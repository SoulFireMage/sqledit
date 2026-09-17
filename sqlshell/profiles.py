"""Connection profile persistence and credential handling."""

from __future__ import annotations

import os
import tempfile
import tomllib
from dataclasses import asdict, dataclass, replace
from pathlib import Path
from typing import Callable

import keyring

SERVICE_NAME = "sqlshell"
AUTH_TYPES = ("sql", "windows", "entra")


class ProfileError(ValueError):
    pass


@dataclass(frozen=True)
class Profile:
    name: str
    server: str
    database: str = "master"
    auth: str = "windows"
    username: str | None = None
    driver: str = "ODBC Driver 18 for SQL Server"
    encrypt: bool = True
    trust_server_certificate: bool = False
    login_timeout: int = 8
    query_timeout: int = 0

    def validate(self) -> None:
        if not self.name or any(c in self.name for c in "[]\r\n"):
            raise ProfileError("Profile name must be non-empty and cannot contain brackets or newlines")
        if not self.server:
            raise ProfileError("Server is required")
        if self.auth not in AUTH_TYPES:
            raise ProfileError(f"Unknown authentication type: {self.auth}")
        if self.auth == "sql" and not self.username:
            raise ProfileError("SQL authentication requires a username")
        if self.login_timeout < 1 or self.query_timeout < 0:
            raise ProfileError("Timeouts cannot be negative (login timeout must be at least 1)")


def default_config_dir() -> Path:
    if os.name == "nt" and os.environ.get("APPDATA"):
        return Path(os.environ["APPDATA"]) / "sqlshell"
    return Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "sqlshell"


def _toml_string(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n") + '"'


class ProfileStore:
    def __init__(self, config_dir: Path | None = None, keyring_backend=keyring) -> None:
        self.config_dir = config_dir or default_config_dir()
        self.path = self.config_dir / "profiles.toml"
        self.keyring = keyring_backend

    def _read(self) -> dict:
        if not self.path.exists():
            return {"profiles": {}}
        try:
            with self.path.open("rb") as handle:
                data = tomllib.load(handle)
        except (OSError, tomllib.TOMLDecodeError) as exc:
            raise ProfileError(f"Cannot read {self.path}: {exc}") from exc
        data.setdefault("profiles", {})
        return data

    def _write(self, data: dict) -> None:
        self.config_dir.mkdir(parents=True, exist_ok=True)
        lines = []
        active = data.get("active")
        if active:
            lines.append(f"active = {_toml_string(active)}\n")
        for name in sorted(data.get("profiles", {})):
            values = data["profiles"][name]
            lines.append(f"\n[profiles.{_toml_string(name)}]\n")
            for key, value in values.items():
                if value is None:
                    continue
                encoded = str(value).lower() if isinstance(value, bool) else str(value) if isinstance(value, int) else _toml_string(str(value))
                lines.append(f"{key} = {encoded}\n")
        fd, tmp_name = tempfile.mkstemp(prefix="profiles-", suffix=".toml", dir=self.config_dir)
        try:
            with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
                handle.writelines(lines)
            os.replace(tmp_name, self.path)
        finally:
            if os.path.exists(tmp_name):
                os.unlink(tmp_name)

    def list(self) -> list[Profile]:
        return [self._from_data(name, values) for name, values in self._read()["profiles"].items()]

    def get(self, name: str | None = None) -> Profile:
        data = self._read()
        selected = name or data.get("active")
        if not selected:
            raise ProfileError("No profile selected; use -p NAME or 'sqlshell profile use NAME'")
        try:
            return self._from_data(selected, data["profiles"][selected])
        except KeyError as exc:
            raise ProfileError(f"Profile not found: {selected}") from exc

    @staticmethod
    def _from_data(name: str, values: dict) -> Profile:
        try:
            profile = Profile(name=name, **values)
            profile.validate()
            return profile
        except TypeError as exc:
            raise ProfileError(f"Invalid profile {name}: {exc}") from exc

    def save(self, profile: Profile, password: str | None = None) -> None:
        profile.validate()
        data = self._read()
        values = asdict(profile)
        values.pop("name")
        data["profiles"][profile.name] = values
        self._write(data)
        if profile.auth == "sql" and password is not None:
            self.keyring.set_password(SERVICE_NAME, profile.name, password)

    def use(self, name: str) -> None:
        data = self._read()
        if name not in data["profiles"]:
            raise ProfileError(f"Profile not found: {name}")
        data["active"] = name
        self._write(data)

    def remove(self, name: str) -> None:
        data = self._read()
        if name not in data["profiles"]:
            raise ProfileError(f"Profile not found: {name}")
        was_sql = data["profiles"][name].get("auth") == "sql"
        del data["profiles"][name]
        if data.get("active") == name:
            data.pop("active", None)
        self._write(data)
        if was_sql:
            try:
                self.keyring.delete_password(SERVICE_NAME, name)
            except keyring.errors.PasswordDeleteError:
                pass

    def configure_security(
        self,
        name: str,
        *,
        trust_server_certificate: bool | None = None,
        encrypt: bool | None = None,
    ) -> Profile:
        """Update TLS options without reading or rewriting stored credentials."""
        profile = self.get(name)
        changes = {}
        if trust_server_certificate is not None:
            changes["trust_server_certificate"] = trust_server_certificate
        if encrypt is not None:
            changes["encrypt"] = encrypt
        if not changes:
            raise ProfileError("No security setting was supplied")
        updated = replace(profile, **changes)
        self.save(updated)
        return updated

    def edit(
        self,
        name: str,
        *,
        password: str | None = None,
        **changes,
    ) -> Profile:
        """Edit profile metadata and optionally replace its stored password."""
        profile = self.get(name)
        if password is not None:
            if profile.auth != "sql":
                raise ProfileError("Only SQL-authentication profiles have a stored password")
            if not password:
                raise ProfileError("Password cannot be empty")
        supplied = {key: value for key, value in changes.items() if value is not None}
        updated = replace(profile, **supplied)
        updated.validate()
        self.save(updated, password)
        return updated

    def password(self, profile: Profile) -> str | None:
        return self.keyring.get_password(SERVICE_NAME, profile.name) if profile.auth == "sql" else None


def prompt_for_profile(name: str, input_fn: Callable[[str], str] = input, password_fn=None) -> tuple[Profile, str | None]:
    import getpass

    password_fn = password_fn or getpass.getpass
    server = input_fn("Server: ").strip()
    database = input_fn("Database [master]: ").strip() or "master"
    auth = input_fn("Authentication (sql/windows/entra) [windows]: ").strip().lower() or "windows"
    username = input_fn("Username: ").strip() if auth == "sql" else None
    password = password_fn("Password: ") if auth == "sql" else None
    trust_answer = input_fn("Trust server certificate without validation? [y/N]: ").strip().lower()
    if trust_answer not in ("", "n", "no", "y", "yes"):
        raise ProfileError("Please answer yes or no for certificate trust")
    profile = Profile(
        name=name,
        server=server,
        database=database,
        auth=auth,
        username=username,
        trust_server_certificate=trust_answer in ("y", "yes"),
    )
    profile.validate()
    return profile, password
