from sqlshell.scripts import current_batch, split_batches


def test_split_batches_and_repeat():
    script = "SELECT 1;\nGO\nSELECT 2\nGO 2 -- repeat\n"
    assert split_batches(script) == ["SELECT 1;", "SELECT 2", "SELECT 2"]


def test_go_inside_sql_is_not_separator():
    assert split_batches("SELECT 'GO';\n-- GO\n") == ["SELECT 'GO';\n-- GO"]


def test_current_batch_uses_cursor_line():
    script = "SELECT 1;\nGO\nSELECT 2;\nSELECT 3;\nGO\nSELECT 4;"
    assert current_batch(script, 2) == "SELECT 2;\nSELECT 3;"
    assert current_batch(script, 5) == "SELECT 4;"
    assert current_batch(script, 1) == "SELECT 1;"
