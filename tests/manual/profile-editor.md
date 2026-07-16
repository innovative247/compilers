# Manual TTY checklist — set_profile whole-profile editor

The interactive editor cannot be driven by the headless suite (it reads raw keys via
`Console.ReadKey`). Run these by hand in a real terminal. The redirected-console
fallback (sequential prompts) is what the automated suite exercises.

Launch against a scratch profile:

```
set_profile SCRATCH        # existing profile -> menu -> Edit
set_profile NEWPROF        # unknown name -> create wizard -> editor
```

Confirm each:

- [ ] **Arrow nav** — `Up`/`Down` moves the `>` pointer over visible rows only; hidden
      rows (see Raw/Platform below) are skipped.
- [ ] **Inline edit** — `Enter` on Host/Username/Company edits in place, seeded with the
      current value; `Backspace` works; `Enter` commits, `Esc` cancels just that edit.
- [ ] **Invalid port rejected inline** — edit Port, type letters or `0`; commit shows a
      yellow error and re-prompts without leaving the editor. A valid number commits.
- [ ] **Dirty markers** — a changed row shows a trailing ` *`; unchanged rows do not.
- [ ] **Raw toggle** — `Enter` on Raw Mode flips yes/no immediately; toggling ON hides
      Company and SQL Source (and zeroes Company / clears SQL Source); toggling OFF
      reveals them again. Visibility updates live.
- [ ] **Platform cycle** — `Enter` on Platform cycles Sybase → SQL Server → PostgreSQL →
      back. Cycling to PostgreSQL reveals the Database row; cycling away hides it.
- [ ] **Password** — `Enter` on Password prompts masked (`*`); empty keeps the existing
      value; a new value shows as `****`.
- [ ] **Connection test** — `T` runs the live connection test against the in-progress
      values, prints the result, waits for a key, then redraws the editor intact.
- [ ] **Save** — `S` validates all visible rows then exits; `--view <name>` afterward
      confirms every edited field (including `--data-charset` / Data Charset).
- [ ] **Cancel discards** — with unsaved changes, `Esc` prompts "Discard changes? (y/N)";
      `y` exits without saving (`--view` shows the pre-edit values), `n` returns.
- [ ] **Fallback** — `echo "" | set_profile SCRATCH` (redirected stdin) takes the
      sequential prompt path, never the TUI, and does not hang.
