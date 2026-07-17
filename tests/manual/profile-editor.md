# Manual TTY checklist — set_profile whole-profile editor (the hub)

The interactive editor cannot be driven by the headless suite (it reads raw keys via
`Console.ReadKey`). Run these by hand in a real terminal. The redirected-console
fallback (sequential prompts + the legacy numbered submenu) is what the automated suite
and any piped-stdin invocation exercise.

Launch against a scratch profile:

```
set_profile SCRATCH        # existing profile -> STRAIGHT into the editor hub
set_profile NEWPROF        # unknown name    -> create wizard -> same editor
```

## The editor is the single hub — no intermediate menu

Selecting an existing profile in an interactive terminal no longer shows an
`Open / Copy / Delete / Back / Exit` submenu. It opens the full-screen editor directly,
and every action lives on a numbered menu rendered **below the field list**:

```
  Edit Profile: S254_SBNB

  > Aliases         : SBNCLOUDB
    Raw Mode        : no
    Platform        : SYBASE
    Database        : ---
    Host            : 10.0.0.42
    Port            : 5000  (default 5000)
    Username        : sa
    Password        : ****
    Company         : 101
    Default Language: 1
    Data Charset    : (server default)
    SQL Source      : C:\src\S254

  [Up/Down] move  [Enter] edit

   1. Save
   2. Test connection
   3. Test SQL Source path
   4. Test options
   5. Test table locations
   6. Test changelog
   7. Test symlinks
   8. Test all
   9. Copy
  10. Delete
  98. Back
  99. Exit
```

Raw Mode = yes collapses the six SBN-specific tests to just `2. Test connection`, and the
remaining items renumber live (Copy/Delete/Back/Exit shift up):

```
   1. Save
   2. Test connection
   3. Copy
   4. Delete
  98. Back
  99. Exit
```

New-profile and Copy modes show the same menu **without** Copy/Delete (Save + the tests +
Back + Exit only).

## Interaction model — arrows edit fields, digits pick menu actions

- [ ] **Arrow nav** — `Up`/`Down` moves the `>` pointer over EVERY field row (all fields
      always visible, inapplicable ones dim `---`). Arrows do not touch the menu.
- [ ] **Enter edits the focused field** — with no choice in progress, `Enter` edits the
      row the `>` points at (inline seeded edit / password prompt / bool + platform cycle),
      exactly as before.
- [ ] **Digit starts a menu choice** — typing a digit shows `Choice: 9` on the prompt line
      below the menu, caret parked after it. Keep typing to build multi-digit numbers
      (`10`, `98`, `99`).
- [ ] **Enter commits the choice** — with a non-empty buffer, `Enter` runs that menu item
      (NOT a field edit). An unknown number prints `No menu item N.` and waits for a key.
- [ ] **Backspace edits the buffer** — removes the last digit; emptying it clears the
      prompt line and returns to field-edit meaning for `Enter`.
- [ ] **Esc clears the buffer** — `Esc` with digits typed just clears the choice. `Esc`
      with an empty buffer is Back (discard-confirm when dirty — see below).
- [ ] **Any other key abandons the buffer** — pressing an arrow / `S` mid-choice discards
      the in-progress number and processes that key normally.
- [ ] **Accelerators** — `S` still saves and `Esc` still backs out; the numbered menu is
      the documented surface. (The old separate `[T]` chooser is gone — the Test items ARE
      the chooser now.)

## Actions

- [ ] **1. Save** — full validate-all gate (incl. rows never visited): Profile Name (create/
      copy only), Host, Username, Password, Port (positive int; blank keeps the platform
      default), SQL Source (unless Raw). On failure the cursor jumps to the first offending
      row and the specific error prints red on the prompt line; nothing is saved. On success
      the editor exits, the green `Profile 'NAME' updated/created!` line is in scrollback,
      and control returns to the MAIN MENU (the profile list was just a picker, not a hub —
      it is not re-shown).
- [ ] **2–8. Test \*** — same per-test dispatch and the same validate-all gate as Save
      (invalid ⇒ jump to offender + error, no test runs). The chosen test runs against the
      WORKING COPY (unsaved edits included); a dim `(testing current editor values — not yet
      saved)` line prints once above the output; output scrolls, waits for a key, then the
      editor (fields + menu) redraws intact. `Test all` runs the same sequence as
      `--test --what all`. Raw mode exposes only `Test connection`.
- [ ] **9. Copy** — opens the copy-into-editor flow for THIS profile (params prefilled,
      Profile Name + Aliases blank, title `Copy Profile (from SOURCE)`). On save the new
      profile is created (same validation as New); on save or cancel you return to the
      MAIN MENU. The source is untouched.
- [ ] **10. Delete** — the legacy `Type 'delete' to confirm:` prompt; on confirmation the
      profile is removed and you return to the MAIN MENU; anything else cancels.
- [ ] **98. Back** — leaves the editor (discard-confirm `Discard changes? (y/N)` when
      dirty) and returns to the MAIN MENU, not the profile list (the list was just a picker).
      For `set_profile NAME` direct invocation there is no main menu on the stack, so Back
      exits the program (same as picking it via Main Menu → 2 with nothing further behind it).
- [ ] **99. Exit** — leaves the app entirely (discard-confirm when dirty).

## Field behaviors (unchanged)

- [ ] **Inline edit** — `Enter` on Host/Username/Company edits in place, seeded with the
      current value; `Backspace` works; `Enter` commits, `Esc` cancels just that edit.
- [ ] **Invalid port rejected inline** — edit Port, type letters or `0`; commit shows a
      yellow error and re-prompts without leaving the editor. A valid number commits.
- [ ] **Dirty markers** — a changed row shows a trailing ` *`; unchanged rows do not.
- [ ] **Raw toggle re-renders live** — `Enter` on Raw Mode flips yes/no immediately: it
      dims Company + SQL Source to `---` (zeroing Company / clearing SQL Source; toggling
      OFF restores them) AND the action menu renumbers live (tests collapse / expand). The
      rows never disappear.
- [ ] **Disabled row rejects edit** — with Raw ON, `Enter` on Company or SQL Source shows a
      dim "not applicable in raw mode" note and makes no change.
- [ ] **Platform cycle + Database applicability** — `Enter` on Platform cycles
      SYBASE → MSSQL → POSTGRES → back (uppercase canonical token). Database holds a value
      only on POSTGRES, otherwise dim `---` and `Enter` shows "not applicable unless
      Platform is POSTGRES".
- [ ] **Password** — `Enter` on Password prompts masked (`*`); empty keeps the existing
      value; a new value shows as `****`.

## New profile — name row in-view, save blocked until valid

`New profile` (Main Menu → 1) and `set_profile NEWNAME` open the SAME editor — the profile
name is the FIRST row (`Profile Name`), not a pre-prompt. `set_profile NEWNAME` prefills it
uppercased; Main Menu → 1 opens it blank with the `>` already on it. Blank renders
`(required)`; editing uppercases on commit. Save is blocked (name required, `^[A-Z0-9_]+$`,
not reserved, unique vs existing names AND aliases) until valid.

## Copy profile — params prefilled, name + aliases blank

Reachable via the editor's `9. Copy` (interactive TTY). Opens the SAME editor prefilled
with a clone of the source's parameters, Profile Name + Aliases blank, title
`Copy Profile (from SOURCE)`. Same Save validation as New. Redirected-console Copy takes the
legacy prompt-based flow; headless `set_profile --copy NAME --to NEW` is unchanged.

## Deferred 'Choice:' entry — main menu, profile list, Add-to-IDE (interactive TTY)

The same deferred-entry pattern as the editor's action menu now drives every interactive
`set_profile` menu. The menu renders with **no visible prompt line**; the first keystroke
reveals `Choice: <buf>` (or `Select: <buf>` where a name is accepted) with the caret parked
after it. Enter commits, Backspace edits, Esc clears the buffer (empty-buffer Esc backs out
where the menu has a Back/cancel semantic). Redirected stdin (the headless suite / piped
input) keeps the plain `Choose [..]:` / `Select [..]:` ReadLine prompts unchanged.

- [ ] **Main Menu** — `set_profile` (no args) in a TTY shows `1 New / 2 Existing / 3 Add to
      IDE / 4 Open settings.json / 99 Exit` with no `Choose [1-4]:` line. Typing a digit
      reveals `Choice: 9`; Enter runs it; multi-digit (`99`) builds then commits. Blank
      Enter / Esc just re-shows the menu.
- [ ] **Profile list** — `Existing profile` (or `set_profile` → 2) shows the numbered profile
      list with no `Select […]:` line. `Select: <buf>` accepts a **list number OR a profile
      name** (letters/digits/underscore, e.g. `S254_SBNB`); Enter commits, blank Enter / Esc
      cancels back out of the list. The list is a picker, not a hub — after the editor opens,
      any exit from it (Back/Save/Copy/Delete) unwinds straight to the Main Menu, not back to
      this list.
- [ ] **Add to IDE** — Main Menu → 3 shows `1 VSCode / 98 Back / 99 Exit` with deferred
      `Choice:` entry; Esc / blank Enter = Back. The "default profile" and "default database"
      sub-menus (multi-profile / multi-database) use the same deferred numeric entry.
- [ ] **Redirected fallback** — `echo "2" | set_profile` (piped stdin) prints the classic
      `Choose [1-4]:` / `Select […]:` prompts and does not hang — the deferred `ReadKey`
      path is never taken on a redirected console.

## Robustness

- [ ] **Save confirmation in scrollback** — after Save exits, the green
      `Settings saved to: <path>` + `Profile 'NAME' updated/created!` is visible in
      scrollback (not cleared by a redraw). A failed write prints red instead.
- [ ] **Cancel discards** — with unsaved changes, `Esc`/`98. Back` prompts
      `Discard changes? (y/N)`; `y` exits without saving, `n` returns.
- [ ] **Fallback** — `echo "" | set_profile SCRATCH` (redirected stdin) takes the legacy
      submenu + sequential prompt path, never the TUI, and does not hang.
- [ ] **Bottom-of-buffer launch** — scroll to the very bottom before running
      `set_profile PGTEST` so the menu's last row lands on the terminal's last buffer row;
      editing a field and running a Test both work without an `ArgumentOutOfRangeException`
      (`Parameter 'top'`) — the message row now sits BELOW the full menu block, so the
      scaffold reserves it correctly.
- [ ] **Small-terminal fallback** — resize the terminal below the editor's minimum height
      (now `fields.Length + maxMenuRows + 7` rows — the menu adds up to 12 lines) before
      running `set_profile`; a "Terminal too small" notice prints and the sequential prompt
      flow runs instead of the TUI.
