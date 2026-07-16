# Manual TTY checklist — set_profile whole-profile editor

The interactive editor cannot be driven by the headless suite (it reads raw keys via
`Console.ReadKey`). Run these by hand in a real terminal. The redirected-console
fallback (sequential prompts) is what the automated suite exercises.

Launch against a scratch profile:

```
set_profile SCRATCH        # existing profile -> menu -> 1. Open (view / edit / test)
set_profile NEWPROF        # unknown name -> create wizard -> editor
```

The existing-profile menu is now `1. Open (view / edit / test)`, `2. Copy`, `3. Delete`,
`98. Back`, `99. Exit`. Open routes to the same editor; if you save nothing it is just a
view. View and Test are no longer separate menu items — Test lives on the editor's `[T]`.

Confirm each:

- [ ] **Arrow nav** — `Up`/`Down` moves the `>` pointer over EVERY row (all fields are
      always visible now, including inapplicable ones rendered as dim `---`).
- [ ] **Inline edit** — `Enter` on Host/Username/Company edits in place, seeded with the
      current value; `Backspace` works; `Enter` commits, `Esc` cancels just that edit.
- [ ] **Invalid port rejected inline** — edit Port, type letters or `0`; commit shows a
      yellow error and re-prompts without leaving the editor. A valid number commits.
- [ ] **Dirty markers** — a changed row shows a trailing ` *`; unchanged rows do not.
- [ ] **Raw toggle** — `Enter` on Raw Mode flips yes/no immediately; toggling ON renders
      Company and SQL Source as a dim `---` (non-editable) and zeroes Company / clears
      SQL Source; toggling OFF restores the previous values in place. The rows never
      disappear — only their value/editability changes, and it updates live.
- [ ] **Disabled row rejects edit** — with Raw Mode ON, `Enter` on Company or SQL Source
      shows a dim one-line note "not applicable in raw mode" and makes no change.
- [ ] **Platform cycle + Database applicability** — `Enter` on Platform cycles
      SYBASE → MSSQL → POSTGRES → back (values shown as the uppercase canonical token,
      never a pretty label). The Database row stays visible always: it holds a value only
      on POSTGRES, otherwise it is a dim `---` and `Enter` shows "not applicable unless
      Platform is POSTGRES".
- [ ] **Password** — `Enter` on Password prompts masked (`*`); empty keeps the existing
      value; a new value shows as `****`.
- [ ] **Test chooser** — `T` shows a one-line chooser on the message row:
      `[C]onnection [P]ath [O]ptions [L]ocations [G]changelog [A]ll` (full profile) or
      just `[C]onnection` (raw profile). The chosen test runs against the WORKING COPY
      (unsaved edits included), output scrolls, waits for a key, then the editor redraws
      intact. `Esc` (or any other key) at the chooser cancels back to the editor.
- [ ] **Save** — `S` validates all visible rows then exits; `--view <name>` afterward
      confirms every edited field (including `--data-charset` / Data Charset).
- [ ] **Cancel discards** — with unsaved changes, `Esc` prompts "Discard changes? (y/N)";
      `y` exits without saving (`--view` shows the pre-edit values), `n` returns.
- [ ] **Fallback** — `echo "" | set_profile SCRATCH` (redirected stdin) takes the
      sequential prompt path, never the TUI, and does not hang.
- [ ] **Bottom-of-buffer launch** — scroll the shell to the very bottom of the window
      before running `set_profile PGTEST` (or any existing profile) so the editor's
      footer lands on the terminal's last buffer row; `Edit` → `Enter` on any field
      commits without a crash (regression for the `ArgumentOutOfRangeException:
      Parameter 'top'` in `ClearMessage`/`Message`).
- [ ] **Small-terminal fallback** — resize the terminal below the editor's minimum
      height (fewer than `fields.Length + 5` rows) before running `set_profile`; a
      "Terminal too small" notice prints and the sequential prompt flow runs instead
      of the TUI.
