# Project: sqlshell — resilient terminal SQL client

## Problem statement
VPN drops cause connection timeouts while running SQL Server work via SSMS/sqlcmd.
Need a lightweight terminal tool that survives drops, reconnects automatically,
and is nicer to use day-to-day than raw sqlcmd — with a nostalgic nod to
Turbo Pascal / MS-DOS `edit.com`.

## Direction
Hybrid build (Option C): start as a resilient command-line SQL shell
(fast to build, immediately useful), then layer in an optional full-screen
edit mode for composing longer scripts. Ship v1 (shell only) before
starting v2 (editor mode).

## Connectivity decision
Use **pyodbc** with a persistent connection object — not shelling out to
`sqlcmd.exe`. Rationale: TDS wire chatter is equivalent either way; the
win is that owning the connection object lets the app detect a dropped/timed-out
connection and silently reconnect using saved credentials, instead of every
command re-authenticating from scratch (or dying) via a fresh sqlcmd process.

Requires the Microsoft ODBC Driver for SQL Server on the host machine
(already present if SSMS/sqlcmd work there — verify, don't assume).

## Tech stack
- Python 3.11+
- `pyodbc` — connection + query execution
- `rich` — styled console output, tables for result sets, syntax highlighting
- `prompt_toolkit` — line editing, history, auto-suggest for the v1 shell prompt
- `textual` (built on Rich) — v2 full-screen edit mode only
- `keyring` (or simple local encrypted file) — saved credential storage; do not store passwords in plaintext config
- `tomllib`/`toml` — connection profile config file

## v1 — Resilient SQL shell

### Modules
- `connection.py` — wraps pyodbc connection lifecycle
  - `connect(profile)` — opens connection, sets `login_timeout`/`timeout` tuned for flaky VPN (e.g. shorter login_timeout, longer query timeout)
  - `execute(sql)` — runs a statement; on ODBC error matching connection-loss patterns (e.g. `08S01`, `HYT00`), triggers `reconnect()` and retries once transparently
  - `reconnect(profile)` — re-establishes using saved profile creds, logs the event visibly (so Richard knows a drop happened) but doesn't require re-auth input
- `profiles.py` — saved connection profiles (server, database, auth type: SQL auth / Windows auth / Entra)
  - Passwords via `keyring` (OS credential store), never plaintext in the profile file
  - CLI commands: `sqlshell profile add|list|use|remove`
- `shell.py` — the interactive REPL
  - `prompt_toolkit` session: history, up-arrow recall, basic tab-complete on `.` commands
  - Dot-commands: `.connect <profile>`, `.run <file.sql>`, `.save <file.sql>` (last query/session), `.history`, `.exit`
  - Anything not starting with `.` is treated as SQL and sent to `execute()`
- `render.py` — result-set rendering
  - Rich `Table` for SELECT results (auto-detect column widths, truncate long text with `…`, `-x` flag for full width)
  - Rich status/spinner while a query is running
  - Clear, distinct styling for: query OK, rows returned, error, **reconnect happened** (this last one matters — Richard should always see when a silent reconnect fired)
- `main.py` — CLI entrypoint (`sqlshell`, or `sqlshell -p <profile>`, or `sqlshell -p <profile> -f script.sql` for one-shot script execution without entering the REPL)

### v1 acceptance criteria
- Can save a profile once, then `sqlshell -p work` reconnects without re-entering credentials
- Killing/restoring the VPN mid-session does not kill the shell — next command transparently reconnects and clearly logs that it did
- `.run script.sql` executes a multi-statement script and shows results/errors per statement
- Result tables are readable in a normal terminal width without manual formatting

## v2 — Edit mode (Turbo Pascal / edit.com homage)
Only start after v1 is in daily use and stable.

- `editor.py` — Textual app, invoked via `.edit` from the shell or `sqlshell edit script.sql`
- Full-screen buffer, function-key bindings (F5 or Ctrl+Enter = execute buffer or selected block against the active connection from v1's `connection.py` — reuse, don't rewrite)
- Split pane or overlay for results (reuse `render.py`)
- Basic T-SQL syntax highlighting (Textual/Rich lexer)
- Save/load `.sql` files
- Blue-on-white or classic DOS-editor color scheme as a config option, for the nostalgia factor

## Explicit non-goals (v1)
- No query builder / GUI-style object browser (that's what SSMS is for)
- No multi-server parallel execution
- No ORM or schema migration features

## Open questions for Claude Code to flag back if unclear
- Confirm which auth modes are actually needed (SQL auth only, or also Windows/Entra) — affects `profiles.py` and the pyodbc connection string
- Confirm terminal environment (Windows Terminal / PowerShell / WSL?) since that affects prompt_toolkit and color behavior
