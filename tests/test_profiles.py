from dataclasses import replace

import pytest

from sqlshell.connection import build_connection_string
from sqlshell.profiles import Profile, ProfileError, ProfileStore


class FakeKeyring:
    errors = type("Errors", (), {"PasswordDeleteError": KeyError})

    def __init__(self):
        self.values = {}

    def set_password(self, service, name, password):
        self.values[(service, name)] = password

    def get_password(self, service, name):
        return self.values.get((service, name))

    def delete_password(self, service, name):
        del self.values[(service, name)]


def test_profile_roundtrip_and_password_is_not_in_toml(tmp_path):
    keys = FakeKeyring()
    store = ProfileStore(tmp_path, keys)
    profile = Profile("work", "server.example", auth="sql", username="richard")
    store.save(profile, "secret")
    assert store.get("work") == profile
    assert store.password(profile) == "secret"
    assert "secret" not in store.path.read_text(encoding="utf-8")


def test_active_and_remove(tmp_path):
    store = ProfileStore(tmp_path, FakeKeyring())
    profile = Profile("work", "server")
    store.save(profile)
    store.use("work")
    assert store.get() == profile
    store.remove("work")
    with pytest.raises(ProfileError):
        store.get()


def test_configure_security_preserves_password(tmp_path):
    keys = FakeKeyring()
    store = ProfileStore(tmp_path, keys)
    profile = Profile("work", "server", auth="sql", username="user")
    store.save(profile, "secret")
    updated = store.configure_security("work", trust_server_certificate=True)
    assert updated.trust_server_certificate is True
    assert updated.encrypt is True
    assert store.password(updated) == "secret"


def test_edit_replaces_password_without_writing_it_to_profile(tmp_path):
    keys = FakeKeyring()
    store = ProfileStore(tmp_path, keys)
    profile = Profile("work", "server", auth="sql", username="user")
    store.save(profile, "old-secret")
    updated = store.edit("work", password="new-secret")
    assert updated == profile
    assert store.password(updated) == "new-secret"
    contents = store.path.read_text(encoding="utf-8")
    assert "old-secret" not in contents
    assert "new-secret" not in contents


def test_edit_metadata_preserves_password(tmp_path):
    keys = FakeKeyring()
    store = ProfileStore(tmp_path, keys)
    profile = Profile("work", "old-server", auth="sql", username="user")
    store.save(profile, "secret")
    updated = store.edit("work", server="new-server", database="reporting")
    assert updated.server == "new-server"
    assert updated.database == "reporting"
    assert store.password(updated) == "secret"


def test_cannot_add_password_to_windows_profile(tmp_path):
    store = ProfileStore(tmp_path, FakeKeyring())
    store.save(Profile("work", "server"))
    with pytest.raises(ProfileError, match="SQL-authentication"):
        store.edit("work", password="secret")


def test_connection_strings_for_auth_modes():
    base = Profile("p", "tcp:server,1433")
    assert "Trusted_Connection=yes" in build_connection_string(base, None)
    assert "Authentication=ActiveDirectoryInteractive" in build_connection_string(replace(base, auth="entra"), None)
    sql = replace(base, auth="sql", username="u")
    value = build_connection_string(sql, "p}ass")
    assert "UID={u}" in value and "PWD={p}}ass}" in value
