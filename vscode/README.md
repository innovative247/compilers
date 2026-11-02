# Compilers for VS Code

Run the SQL you are editing with the Compilers, without leaving the editor.

## What it does

- **F5** runs the selection (with `isqlline`) or, when nothing is selected, saves and runs the whole file (with `runsql`) whenever the active editor is a SQL file, even when focus is in the terminal, the Problems or Output panel or the status bar. On a SQL file F5 never starts the debugger. The extension adds `compilers.run` to `terminal.integrated.commandsToSkipShell` by default so F5 in the terminal reaches it; if you set that setting yourself, add `compilers.run` to your list.
- **Compilers: Run file**, **Compilers: Run selection** and **Compilers: Run create file (runcreate)** are in the Command Palette and the editor right-click menu. Run create file is also in the Explorer right-click menu.
- The status bar shows the current **profile** and **database**. Click either to change it. Profiles come from the Compilers `settings.json`, with their aliases. The database list remembers what you used per profile, and you can type any name. You are asked for both the first time you run something.
- The status bar shows a warning-coloured **pick a profile** / **pick a database** while either is unset.
- Output streams in the terminal panel, which opens and takes focus when a run starts, so password prompts still work. Server errors also appear in **Problems**.
- The **Compilers** output channel logs every command, the profile, database and command line used (without the SQL text), and when each run starts and ends with its exit code. Any failure is also shown as an error message.

## About error lines

The server reports `Line N` counted from the start of the `GO` batch (or the procedure), not from the top of the file, so Compilers cannot place an error on the exact file line. The problem is shown on line 1 of the file (the first line of the selection for a selection run), and the message carries the server's batch line, for example `(batch line 3)`.

## Known limits

- Results are written through the `-O` file option, which wraps output at 80 columns, so wide result sets look narrower than in a plain terminal.
- Output is shown as UTF-8. On a Sybase server whose character set is not UTF-8, accented characters may appear garbled; the extension does not know the server's character set, so it cannot convert them.
- A selection that is a single Compilers command word (for example `update` or `help`) is refused, and a selection longer than about 8,000 characters (Windows) must be saved and run as a file.
- In an unsaved editor a selection runs, but errors are not added to Problems because there is no file to attach them to.

## Settings

| Setting | Meaning |
|---|---|
| `compilers.installPath` | Folder that holds `runsql` and `settings.json`. Empty means the one on PATH. |
| `compilers.fileExtensions` | Extra extensions treated as Compilers scripts for F5 and the menu. Default `[".sql"]`. |

## Tests

`npm test` runs the unit tests. `npm run test:integration` starts a real VS Code with its own user-data and extensions folders (see the header of `test/integration/run.js` for the environment variables) and runs `select` scripts against `SRM_LOCAL` / `master` by default.

## Requirements

Compilers installed (`runsql`, `isqlline`, `runcreate` on PATH) and at least one profile set up with `set_profile`.
