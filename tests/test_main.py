from sqlshell.main import parser


def test_edit_accepts_profile_after_subcommand():
    args = parser().parse_args(["edit", "query.sql", "-p", "work", "--max-rows", "250"])
    assert args.command == "edit"
    assert args.file == "query.sql"
    assert args.edit_profile == "work"
    assert args.max_rows == 250
