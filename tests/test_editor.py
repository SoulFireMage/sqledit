from pathlib import Path

import pytest
from textual.widgets import DataTable, TextArea

from sqlshell.connection import ResultSet
from sqlshell.editor import FileBrowser, MenuPopup, SqlIdeApp, filter_file_paths
from sqlshell.engines import engine_for
from sqlshell.profiles import Profile


class FakeStore:
    def __init__(self):
        self.profile = Profile("test", "server", database="database")

    def list(self):
        return [self.profile]

    def get(self, name=None):
        return self.profile


class FakeManager:
    def __init__(self, store):
        self.profile = store.profile
        self.engine = engine_for(store.profile)
        self.event = lambda *_args: None
        self.executed = []

    def execute(self, sql, *, max_rows=None):
        self.executed.append((sql, max_rows))
        return [ResultSet(["answer", "empty"], [(42, None)])]

    def connect(self, profile):
        self.profile = profile
        self.engine = engine_for(profile)
        self.event("connected", f"Connected to {profile.name}")


@pytest.mark.asyncio
async def test_editor_runs_buffer_and_populates_grid():
    store = FakeStore()
    manager = FakeManager(store)
    app = SqlIdeApp(manager, store, text="SELECT 42", max_rows=123)
    async with app.run_test(size=(120, 40)) as pilot:
        await pilot.press("f5")
        await app.workers.wait_for_complete()
        await pilot.pause()
        assert manager.executed == [("SELECT 42", 123)]
        grid = app.query_one("#results-grid", DataTable)
        assert grid.row_count == 1
        assert len(grid.columns) == 2


@pytest.mark.asyncio
async def test_editor_saves_buffer(tmp_path: Path):
    store = FakeStore()
    manager = FakeManager(store)
    app = SqlIdeApp(manager, store, text="SELECT 1")
    output = tmp_path / "saved.sql"
    async with app.run_test(size=(100, 35)):
        editor = app.query_one("#editor", TextArea)
        editor.load_text("SELECT 2")
        assert app.dirty
        app._save_to(output)
        assert output.read_text(encoding="utf-8") == "SELECT 2"
        assert not app.dirty


@pytest.mark.asyncio
async def test_top_menu_is_mouse_and_keyboard_accessible():
    store = FakeStore()
    app = SqlIdeApp(FakeManager(store), store, text="SELECT 1")
    async with app.run_test(size=(120, 40)) as pilot:
        await pilot.click("#menu-file")
        await pilot.pause()
        assert isinstance(app.screen, MenuPopup)
        await pilot.press("escape")
        await pilot.press("alt+s")
        await pilot.pause()
        assert isinstance(app.screen, MenuPopup)


@pytest.mark.asyncio
async def test_open_uses_file_browser():
    store = FakeStore()
    app = SqlIdeApp(FakeManager(store), store)
    async with app.run_test(size=(120, 40)) as pilot:
        await pilot.press("f3")
        await pilot.pause()
        assert isinstance(app.screen, FileBrowser)


@pytest.mark.asyncio
async def test_file_menu_open_item_launches_browser():
    store = FakeStore()
    app = SqlIdeApp(FakeManager(store), store)
    async with app.run_test(size=(120, 40)) as pilot:
        await pilot.click("#menu-file")
        await pilot.press("down", "enter")
        await pilot.pause()
        assert isinstance(app.screen, FileBrowser)


def test_file_browser_filter_keeps_directories_and_sql(tmp_path: Path):
    folder = tmp_path / "queries"
    folder.mkdir()
    sql = tmp_path / "query.sql"
    sql.write_text("SELECT 1", encoding="utf-8")
    text = tmp_path / "notes.txt"
    text.write_text("notes", encoding="utf-8")
    assert filter_file_paths([folder, sql, text], {".sql"}) == [folder, sql]
