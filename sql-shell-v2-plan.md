# sqlshell v2 — terminal SQL IDE plan

## Product and versioning decision

The IDE is version 2 of the existing `sqlshell` package, not a separate product.
The v1 REPL remains supported and continues to use the same profiles, connection
manager, retry logic, script parser, and export code.

- `python -m sqlshell -p work` launches the classic REPL.
- `python -m sqlshell edit -p work` launches the terminal IDE.
- `python -m sqlshell edit query.sql -p work` opens a file in the IDE.
- `.edit [query.sql]` launches the IDE from the REPL with its live connection.
- Textual is an optional `ide` dependency so shell-only installs remain small.

The first usable editor was `2.0.0b1`. The `2.0.0b2` milestone adds functional
menus, filesystem browsing, overwrite protection, and find/find-next. Stable `2.0.0` requires daily-use testing,
large-result paging, and reliable cancellation behavior.

## Interface

```text
┌ File  Edit  Search  Query  Connection  Help ────────────────────────┐
│ query.sql                                      work / JobBook       │
├─────────────────────────────────────────────────────────────────────┤
│  1  SELECT TOP 100 *                                               │
│  2  FROM hkp.viewsandprocslist                                     │
│  3  WHERE object_type = 'VIEW';                                    │
│                                                                    │
│                         SQL editor                                 │
├ Results ────────────────┬ Messages ─────────────────────────────────┤
│ schema │ name │ type    │ Query completed in 0.18s                 │
│ hkp    │ ...  │ VIEW    │ 42 rows returned                         │
├─────────────────────────────────────────────────────────────────────┤
│ Ln 3 Col 28 │ Connected │ F2 Save │ F5 Run │ F6 Results │ F10 Menu │
└─────────────────────────────────────────────────────────────────────┘
```

## Milestone 1 — shared engine hardening

- Keep database behavior UI-independent.
- Add a bounded-row execution option for the IDE while retaining unbounded REPL
  behavior and explicit exports.
- Add current-`GO`-batch selection as a tested script primitive.
- Preserve visible reconnect events in both interfaces.

## Milestone 2 — usable editor vertical slice (`2.0.0b1`)

- Full-screen Textual application with multiline editor and line numbers.
- Turbo-blue and modern-dark themes.
- Open, save, save-as, dirty indicator, and unsaved-change confirmation.
- F5 executes selected SQL, or the whole buffer when there is no selection.
- Shift+F5 executes the current `GO` batch.
- Results grid, multiple-result-set selector, and messages pane.
- Background query execution so the UI remains responsive.
- Result cap with a conspicuous truncation warning.
- CSV/TSV export of the active result set.
- Profile switching without leaving the editor.
- Keyboard-first help and function-key footer.

## Milestone 3 — daily-use editor

- Completed in `2.0.0b2`: clickable and Alt-key menus, SQL-filtered open/save
  browser, remembered location, new-folder support, overwrite confirmation, and
  find/find-next.
- Multiple file tabs and recent-file list.
- Find and replace.
- Query cancellation after driver behavior is verified.
- Configurable result limits and layout persistence.
- T-SQL completion using cached schema metadata.
- Improved T-SQL syntax highlighting if the installed Textual/tree-sitter
  language set does not provide it natively.

## Stable `2.0.0` gate

- Replace eager `fetchall()` for IDE queries with genuinely paged/streamed result
  access suitable for very large result sets.
- Exercise VPN drop/reconnect, resize, Unicode, multiple results, long-running
  queries, and unsaved files in Windows Terminal.
- Run headless Textual interaction tests plus the existing engine tests.
- Confirm the v1 REPL and one-shot script modes remain backward compatible.

## Default keyboard map

| Key | Action |
|---|---|
| F1 | Help |
| F2 | Save |
| F3 | Open |
| F4 | Switch connection profile |
| F5 / Ctrl+Enter | Execute selection or full buffer |
| Shift+F5 | Execute current `GO` batch |
| F6 | Toggle editor/results focus |
| F8 | Export active result set |
| F9 | Toggle Turbo/modern theme |
| F10 | Open File menu |
| Ctrl+Shift+S | Save as |
| Ctrl+Q | Quit safely |

## Non-goals for the first editor release

- Object browser or graphical query builder.
- Multiple simultaneous queries or servers.
- Schema migration/ORM features.
- Pretending a fixed in-memory row cap is full large-result paging.
