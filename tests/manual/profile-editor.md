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

## New profile — name row in-view, save blocked until valid

`New profile` (Main Menu → 1) and `set_profile NEWNAME` now open the SAME editor for
everything — the profile name is the FIRST row ("Profile Name"), not a pre-prompt.
`set_profile NEWNAME` prefills that row with `NEWNAME` (uppercased); Main Menu → 1 opens
with it blank and the `>` pointer already on it.

- [ ] **Name row present + first** — the editor's top row is `Profile Name`; a blank name
      renders `(required)`. Editing it (Enter) uppercases on commit.
- [ ] **Save blocked until valid** — with any required field invalid, `[S]` does NOT exit:
      it jumps the cursor to the first offending row and prints the specific error in red
      on the message line. Fix all of them and `[S]` saves. Required set enforced at `[S]`
      even for rows you never visited: **Profile Name** (required, `^[A-Z0-9_]+$`, not a
      reserved word, unique vs existing profile names AND aliases), **Host**, **Username**,
      **Password**, **Port** (must be a positive int; blank keeps the platform default),
      and **SQL Source** (required unless Raw Mode — a missing directory still only warns).
- [ ] **Name uniqueness** — typing an existing profile name or an existing alias is
      rejected at `[S]` with the collision named.
- [ ] **Save creates the profile** — after `[S]`, `--view NEWNAME` shows every entered
      field; the green `Profile 'NEWNAME' created!` line is in the scrollback.

## Copy profile — params prefilled, name + aliases blank

`Existing profile → 2. Copy` (interactive TTY) now opens the SAME editor prefilled with a
clone of the source's parameters, but with **Profile Name and Aliases blank**. Title reads
`Copy Profile (from SOURCE)`.

- [ ] **Prefilled from source** — Host/Port/Username/Password/Platform/Company/SQL Source/
      Raw Mode/Charset all carry the source's values and show CLEAN (no ` *`) until edited.
- [ ] **Name + aliases blank** — the `Profile Name` row is blank/`(required)`; `Aliases`
      is `(none)`. Everything else clean.
- [ ] **Same validation** — `[S]` is blocked until a valid, unique name is entered (same
      rules as New profile). Saving creates the new profile from the (possibly edited)
      prefilled values; the source is untouched.
- [ ] **Redirected-console fallback** — piping stdin (`echo "" | ...`) to the Copy path
      takes the legacy prompt-based copy (asks new name, clones, saves), never the TUI.
      Headless `set_profile --copy NAME --to NEW` is unchanged.

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
- [ ] **Test chooser** — `T` shows a vertical chooser in the scroll region below the
      footer (same area test output prints in):
      ```
        Test:
          [C] Connection
          [P] SQL Source path
          [O] Options
          [L] Table Locations
          [G] Changelog
          [S] Symlinks
          [A] All
          [Esc] cancel
      ```
      Raw profile shows only `[C] Connection` and `[Esc] cancel` — matches the legacy
      TestProfileMenu raw behavior: only Connection is available, everything else
      (including SQL Source path) is hidden. The chosen test runs against the WORKING
      COPY (unsaved edits included), output scrolls, waits for a key, then the editor
      redraws intact. `[A]ll` (full profile only) runs the same sequence as
      `--test --what all` for that profile. `Esc` (or any other key) at the chooser
      cancels back to the editor.
- [ ] **Test unsaved-values note** — right before a `[T]` test's output scrolls, a dim
      (dark gray) line prints: `(testing current editor values — not yet saved)`. It
      appears once per test run, above the test output, and is not repeated on the
      "Press any key to return to the editor..." redraw.
- [ ] **Save** — `S` validates all visible rows then exits; `--view <name>` afterward
      confirms every edited field (including `--data-charset` / Data Charset).
- [ ] **Save confirmation** — after `S` exits the editor and the profile is written, a
      green confirmation prints (`Settings saved to: <path>` followed by `Profile
      'NAME' updated/created!`), visible in the scrollback after the TUI exits (not
      cleared or overwritten by a redraw). If the write fails (e.g. settings.json
      locked/read-only), a red error prints instead and no green line appears.
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
