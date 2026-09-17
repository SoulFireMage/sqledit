import csv

import pytest

from sqlshell.connection import ResultSet
from sqlshell.export import export_results, parse_redirection


def test_parse_csv_redirection_and_quoted_path():
    parsed = parse_redirection('select * from equipment > "output files/results.csv"')
    assert parsed is not None
    assert parsed.sql == "select * from equipment"
    assert str(parsed.path) == "output files\\results.csv"
    assert parsed.append is False


def test_parse_append_redirection():
    parsed = parse_redirection("select 1 >> results.tsv")
    assert parsed is not None
    assert parsed.append is True


def test_sql_comparison_is_not_redirection():
    assert parse_redirection("select * from equipment where quantity > 10") is None
    assert parse_redirection("select * from equipment where filename > 'results.csv'") is None


def test_export_csv_quotes_values_and_formats_null_and_bytes(tmp_path):
    path = tmp_path / "results.csv"
    result = ResultSet(["name", "note", "payload"], [("one", "a,b", b"\x01"), ("two", None, b"")])
    assert export_results([result], path) == 2
    with path.open(encoding="utf-8-sig", newline="") as handle:
        assert list(csv.reader(handle)) == [
            ["name", "note", "payload"],
            ["one", "a,b", "0x01"],
            ["two", "", "0x"],
        ]


def test_append_does_not_repeat_header(tmp_path):
    path = tmp_path / "results.csv"
    result = ResultSet(["value"], [(1,)])
    export_results([result], path)
    export_results([result], path, append=True)
    with path.open(encoding="utf-8-sig", newline="") as handle:
        assert list(csv.reader(handle)) == [["value"], ["1"], ["1"]]


def test_export_requires_one_tabular_result(tmp_path):
    with pytest.raises(ValueError, match="did not return"):
        export_results([ResultSet([], [], 3)], tmp_path / "results.csv")
    result = ResultSet(["value"], [(1,)])
    with pytest.raises(ValueError, match="multiple"):
        export_results([result, result], tmp_path / "results.csv")
