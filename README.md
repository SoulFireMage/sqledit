# sqlshell
***DeepSeek 4.1 Experimental Work - Use with suitable caution. Old school ide for sql :P***
`sqlshell` is a resilient terminal client and full-screen terminal IDE for
Microsoft SQL Server and PostgreSQL. It owns a persistent connection, detects
common connection-loss errors, reconnects using the selected profile, and
retries the interrupted command once.

Each profile names the engine it talks to: `mssql` connects through pyodbc, and
`postgres` connects through psycopg. Everything else — profiles, the shell, the
IDE, exports, and reconnection — is shared.

It provides two interfaces over the same profiles and connection engine:

- A lightweight interactive SQL shell for quick queries and scripts.
- A Turbo-Pascal-inspired full-screen IDE for editing and running SQL files.

The current IDE release is `2.0.0b2`. The remaining work required for stable
2.0 is tracked in [sql-shell-v2-plan.md](sql-shell-v2-plan.md).

## Requirements

- Python 3.11 or newer.
- Microsoft ODBC Driver 17 or 18 for SQL Server, for `mssql` profiles.
- `psycopg` (installed with the `postgres` extra), for `postgres` profiles.
- Windows Terminal, PowerShell, or another terminal with ANSI colour support.
- Network access to the SQL Server, including any required VPN connection.

Check the local environment at any time:

```powershell
python -m sqlshell doctor
python -m sqlshell --version
```

`doctor` reports the installed SQL Server ODBC drivers and the PostgreSQL
driver, and is satisfied when at least one of them is present.

## Installation

Install the command-line shell from this project folder:

```powershell
python -m pip install -e .
```

Install the optional full-screen IDE dependencies:

```powershell
python -m pip install -e ".[ide]"
```

Install PostgreSQL support:

```powershell
python -m pip install -e ".[postgres]"
```

For development, including the automated test dependencies:

```powershell
python -m pip install -e ".[dev]"
python -m pytest -q
```

All examples use `python -m sqlshell`, which works without changing `PATH`. The
shorter `sqlshell` command is equivalent when Python's user Scripts directory is
on `PATH`.

## Quick start

Create and select a connection profile:

```powershell
python -m sqlshell profile add work
python -m sqlshell profile use work
```

`profile add` asks for the engine first (`mssql` or `postgres`) and then offers
the defaults that suit it, such as port 5432 and the `postgres` database.

Launch the interactive shell using the active profile, or name one explicitly:

```powershell
python -m sqlshell
python -m sqlshell -p work
```

Launch the full-screen IDE:

```powershell
python -m sqlshell edit -p work
```

## Connection profiles

Profile metadata contains the engine, server, optional port, database,
authentication type, username, driver, TLS choices, and timeouts.
SQL-authentication passwords are stored in the operating-system credential
manager through `keyring`; they are never written to the profile file.

### Create a profile

```powershell
python -m sqlshell profile add work
```

The command prompts for the engine, server, port, default database,
authentication mode, credentials when applicable, and the TLS choices. A
PostgreSQL profile is asked whether to encrypt at all, because many servers,
including the stock `postgres` Docker image, run with `ssl` off.

Authentication modes depend on the engine:

| Engine | Modes | Default port | Default database |
|---|---|---|---|
| `mssql` | `sql`, `windows`, `entra` | driver default (1433) | `master` |
| `postgres` | `sql`, `none` | 5432 | `postgres` |

`sql` means a username and password held in the credential manager; `none` suits
a PostgreSQL server that trusts the connection without a password.

A PostgreSQL profile can be created in one step from an existing one, or edited
directly:

```powershell
python -m sqlshell profile edit home --engine postgres --server 192.168.0.94 --port 5432 --database mydb --auth sql --username postgres --no-encrypt
python -m sqlshell profile edit home --password
```

`--no-encrypt` maps to `sslmode=disable`. With encryption on, a profile that
validates the certificate uses `sslmode=verify-full`, and
`--trust-server-certificate` uses `sslmode=require`.

### List and select profiles

```powershell
python -m sqlshell profile list
python -m sqlshell profile use work
```

The active profile is used whenever `-p` is omitted.

### Inspect a profile

```powershell
python -m sqlshell profile show work
```

This displays every non-secret setting and reports whether a password exists.
It never displays the password itself. Values are shown literally: `richard`
and `[richard]` are different SQL Server usernames because the square brackets
are sent as part of the value.

### Edit a profile interactively

```powershell
python -m sqlshell profile edit work
```

The current values are shown as defaults. Press Enter to retain a value. This
mode does not change the password.

### Edit individual settings

Settings can also be changed without stepping through every prompt:

```powershell
python -m sqlshell profile edit work --server sql01
python -m sqlshell profile edit work --engine postgres
python -m sqlshell profile edit work --port 5432
python -m sqlshell profile edit work --database Reporting
python -m sqlshell profile edit work --username richard
python -m sqlshell profile edit work --auth sql
python -m sqlshell profile edit work --driver "ODBC Driver 18 for SQL Server"
python -m sqlshell profile edit work --login-timeout 10
python -m sqlshell profile edit work --query-timeout 120
python -m sqlshell profile edit work --query-timeout 0
python -m sqlshell profile edit work --encrypt
python -m sqlshell profile edit work --no-encrypt
python -m sqlshell profile edit work --trust-server-certificate
python -m sqlshell profile edit work --validate-server-certificate
```

A query timeout of `0` means unlimited. Multiple edit options can be supplied in
one command. To clear a port and return to the driver default, use the
interactive `profile edit NAME` and leave the port blank.

### Replace a SQL-authentication password

```powershell
python -m sqlshell profile edit work --password
```

The new password is entered twice without being echoed. It is passed directly
to the credential manager and does not appear on the command line, in terminal
history, or in `profiles.toml`.

### Remove a profile

```powershell
python -m sqlshell profile remove work
```

Removing a SQL-authentication profile also removes its saved credential.

## TLS certificates and ODBC Driver 18

This section describes `mssql` profiles; the PostgreSQL equivalents are the
`sslmode` mappings described under *Create a profile*.

ODBC Driver 18 enables encrypted connections and certificate validation by
default. The preferred solution for an internal or privately issued SQL Server
certificate is to install its issuing CA certificate in the Windows trust
store.

For a server whose identity you have verified another way, keep TLS encryption
enabled while bypassing certificate-chain validation for that profile:

```powershell
python -m sqlshell profile configure work --trust-server-certificate
```

Restore normal certificate validation later:

```powershell
python -m sqlshell profile configure work --validate-server-certificate
```

These commands do not read, change, or prompt for the saved password.

## Interactive SQL shell

Launch using an explicit or active profile:

```powershell
python -m sqlshell -p work
python -m sqlshell
```

Anything not beginning with `.` is executed as SQL. The prompt provides command
history, up-arrow recall, automatic suggestions, and syntax highlighting that
follows the profile's engine (T-SQL or PostgreSQL).

### Dot-commands

| Command | Description |
|---|---|
| `.connect PROFILE` | Switch to another saved profile. |
| `.run FILE.sql` | Execute a UTF-8 SQL script, batch by batch. |
| `.save FILE.sql` | Save the most recently executed query. |
| `.edit [FILE.sql]` | Open the full-screen IDE using the live connection. |
| `.history` | Display command history. |
| `.help` | Display shell help and export syntax. |
| `.exit` | Close the shell. Ctrl+D also exits. |

On SQL Server, script files are split on lines containing `GO`, with optional
repetition using `GO N`. On PostgreSQL they are split on semicolons that sit
outside quoted text, dollar-quoted bodies such as function definitions, and
comments. Errors and results are reported for each batch, and later batches
still run after an earlier batch fails.

### Export query results

Redirect one tabular result set to CSV or TSV directly from the SQL prompt:

```sql
SELECT * FROM dbo.Equipment > results.csv
SELECT * FROM dbo.Equipment > results.tsv
SELECT * FROM dbo.Equipment > "C:\Exports\equipment results.csv"
SELECT * FROM dbo.Equipment >> results.csv
```

- `>` creates or replaces the file.
- `>>` appends rows and avoids duplicating the header.
- The filename extension selects CSV or tab-separated output.
- New CSV files use UTF-8 with a byte-order mark for convenient Excel opening.
- Commas, quotes, and line breaks inside values use standard CSV escaping.
- `NULL` is written as an empty field and binary values use hexadecimal form.
- A redirected query must produce exactly one tabular result set.

SQL comparisons such as `WHERE quantity > 10` are not treated as redirection.

## One-shot script execution

Execute a script without entering the interactive shell:

```powershell
python -m sqlshell -p work -f .\report.sql
```

Use full-width table cells rather than ellipsis truncation:

```powershell
python -m sqlshell -p work -f .\report.sql -x
```

The process returns a non-zero exit code when the connection or any script batch
fails, making this mode suitable for PowerShell scripts and scheduled jobs.

## Full-screen SQL IDE

Install the optional dependencies first, then launch an empty editor or open a
file immediately:

```powershell
python -m pip install -e ".[ide]"
python -m sqlshell edit -p work
python -m sqlshell edit query.sql -p work
python -m sqlshell edit query.sql -p work --max-rows 10000
```

The default display limit is 5,000 rows per result set. This protects the UI
from accidentally loading a very large query into memory. A warning is written
to Messages when a result is truncated. Use the command-line shell's redirect
syntax for deliberate full-result exports until streamed IDE results are added.

The IDE can also be opened from an existing shell connection:

```text
sql> .edit
sql> .edit query.sql
```

### IDE keyboard reference

| Key | Action |
|---|---|
| F1 | Open keyboard help. |
| F2 | Save; prompts for a filename when the buffer is untitled. |
| F3 | Open a SQL file, protecting unsaved changes. |
| F4 | Switch connection profile. |
| F5 or Ctrl+Enter | Execute selected SQL, or the complete buffer when nothing is selected. |
| Shift+F5 | Execute the batch containing the cursor: a `GO` batch on SQL Server, a statement on PostgreSQL. |
| F6 | Toggle focus between the editor and result grid. |
| F8 | Export the active result set to CSV or TSV. |
| F9 | Toggle between Turbo blue and modern dark themes. |
| F10 | Open the File menu. |
| Ctrl+Shift+S | Save as. |
| Ctrl+P | Open Textual's searchable command palette. |
| Ctrl+Q | Quit, prompting before discarding unsaved changes. |

Queries run on a background worker so the editor remains responsive. The lower
pane contains a result grid and a Messages tab for timings, errors, row-limit
warnings, connection changes, and VPN reconnection events. When a query returns
multiple tabular result sets, a selector appears above the grid.

### IDE menus and file browser

The File, Edit, Search, Query, Connection, and Help menus respond to mouse clicks.
They can also be opened with Alt+F, Alt+E, Alt+S, Alt+Q, Alt+C, and Alt+H. F10
opens the File menu in the traditional keyboard-first style.

Open, Save As, and Export use a filesystem browser. It filters files to the
relevant extensions, supports keyboard and mouse navigation, includes Up and
New Folder actions, remembers the most recently used directory, and confirms
before replacing an existing file. Ctrl+F opens Find; subsequent Find Next from
the Search menu wraps around the document.

## Connection resilience

The connection manager treats common connection-loss and timeout states as
transient: ODBC SQLSTATEs on SQL Server, and class 08 and 57P0x conditions, a
dropped socket, or a shutdown server on PostgreSQL. It reports the loss,
reconnects using the selected profile, reports successful reconnection, and
retries the interrupted SQL command once.

Syntax, permission, and other SQL errors are not retried. Because the original
outcome of a command can be uncertain after a network failure, use care with
non-idempotent updates during an unstable VPN session.

## Files and credential storage

| Data | Location |
|---|---|
| Profile metadata | `%APPDATA%\sqlshell\profiles.toml` |
| SQL prompt history | `%APPDATA%\sqlshell\history` |
| SQL-authentication passwords | Windows Credential Manager via `keyring` |

Passwords are not stored in the project, profile TOML, history file, exported
results, or logs.

## Troubleshooting

### No profile selected

```powershell
python -m sqlshell profile use work
python -m sqlshell -p work
```

### Login failed

Inspect the exact non-secret values and correct them if necessary:

```powershell
python -m sqlshell profile show work
python -m sqlshell profile edit work
python -m sqlshell profile edit work --password
```

Do not add decorative square brackets around SQL usernames, server names, or
database names.

### Certificate chain is not trusted

Install the issuing CA certificate where possible. For a server you trust, use:

```powershell
python -m sqlshell profile configure work --trust-server-certificate
```

### IDE dependencies are missing

```powershell
python -m pip install -e ".[ide]"
```

### `sqlshell` command is not found

Use the module form, which does not depend on the Scripts directory being on
`PATH`:

```powershell
python -m sqlshell --help
```
