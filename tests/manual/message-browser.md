# Manual TTY checklist — set_messages file-first message browser

The browser is driven by raw `Console.ReadKey` / absolute-row rendering, so the headless
suite cannot exercise it (the suite only hits the headless flags, which route past the
browser). Run these by hand in a **real terminal**. `compile_msg` is unchanged — it still
opens the legacy Import / Export / Add numbered menu; only `set_messages` opens the browser.

Launch against a **non-raw** profile that has a resolvable SQL source with `css.*_msgrp`
files (e.g. a local SBN checkout profile). Use a **scratch copy** for any edit/delete —
never the real message tree.

```
set_messages MYPROFILE        # no flags -> interactive file-first browser
compile_msg  MYPROFILE        # no flags -> legacy Import/Export/Add menu (regression check)
```

## Profile validation (each should print a red error and exit 1)

- [ ] Raw-mode profile -> "requires option file processing / not available in raw mode".
- [ ] Non-raw profile whose SQL source dir is missing -> "message source directory not found".
- [ ] Non-raw profile with a valid dir but no `css.*_msgrp` -> "no live message groups".
- [ ] Redirected stdin/stdout (`set_messages MYPROFILE < NUL`) -> the headless-flags hint,
      exit 1, **no hang**.

## Type screen

- [ ] Numbered vertical list, one row per live type: `1. GUI Messages (css.gui_msgrp)` etc.
- [ ] A dim `Source: <path>` line prints under the screen title, showing the resolved
      `GetPath_Setup` directory for the profile (e.g. `Source: C:\_innovative\_source\
      current.sql\css\setup`).
- [ ] The prompt line shows `Choice [99]: ` from the start (before any key is pressed) —
      Exit is the default, so plain Enter exits with code 0, same as `99`.
- [ ] An out-of-range number reprints the list with a red "No type N".

## Group screen (SBN-GUI-style table)

- [ ] The dim `Source: <path>` line prints under the screen title, same as the type screen
      (both the scrolling table and the small-terminal fallback list).
- [ ] Columns line up: `GROUP  START#  ROWS  DESCRIPTION`; row count matches the SBN GUI's
      group view for the same type.
- [ ] Up/Down move the `>` highlight; the window scrolls when the group list is taller than
      the terminal (no flicker, no wrap past the right edge).
- [ ] The prompt line shows the bare `Choice: ` label from the start (no default — plain
      Enter with no digits typed opens the currently highlighted `>` row instead, which is
      its own well-defined behavior, not a numbered default). Typing a row number then
      Enter opens that group by number; `98` Back to the type screen; `99` Exit.
- [ ] `C` -> create-group prompts (group <=6 / start default 0 / description); a green
      confirmation appears and the new group shows in the refreshed table.
- [ ] `I` -> Install to profile: on a **non-GONZO** profile it confirms then runs the compile;
      on **GONZO** it is blocked with the canonical-source / export-only message.

## Group actions menu

- [ ] The dim `Source: <path>` line prints under the screen title.
- [ ] `1` Add / `2` Find / `3` Open in editor / `98` Back / `99` Exit render and dispatch.
- [ ] The prompt line shows `Choice [98]: ` from the start — Back is the default (plain
      Enter backs out to the group screen).
- [ ] `3` opens `css.<type>_msg` in `$EDITOR`/vim; after the editor closes the file is
      reloaded (edit a row in vim, save, confirm the change is visible in Find).

## Add message (reserved msgno contract)

- [ ] type/group are fixed to the current selection; text is required (empty text cancels).
- [ ] lang default 1, cmpy default 0, upd-flg default `X` for gui / space otherwise.
- [ ] The dry-run preview shows the **reserved MSGNO** and the exact tab row before the
      y/N confirm; `N` writes nothing; `y` writes and prints green `MSGNO <n> saved.`
- [ ] Re-add into the same group -> the reserved number advances (global max+1 / block floor).

## Find (incremental search)

- [ ] Search the real ~27k-row `css.gui_msg`: each keystroke re-filters with **no perceptible
      lag**; only the result window repaints (header/footer stay put).
- [ ] Header shows `Filter: <buf>_`; footer shows `showing N of M`.
- [ ] Up/Down move the selection and scroll-clamp; Backspace trims the filter.
- [ ] `Tab` prompts cmpy/lang refine; the chips `[cmpy=..] [lang=..]` appear and the result
      count drops accordingly; blank clears a chip back to "any".
- [ ] Enter opens the detail screen; Esc returns to the group actions menu.

## Detail / Edit / Delete (use a SCRATCH fixture, not the real tree)

- [ ] All row fields shown (msgno/cmpy/lang/group/flag/text).
- [ ] The prompt line shows `Choice [98]: ` from the start — Back is the default; Delete
      (item `2`, a destructive action) is never defaulted and always requires explicit entry.
- [ ] `1` Edit: Enter keeps current text/flag; a real change prints green `EDITED <msgno>`
      and bounces back to the refreshed Find list (the edited row reflects the new text).
- [ ] `2` Delete: requires typing `delete` to confirm; prints green `DELETED <msgno>`; the
      row is gone from the refreshed Find list.
- [ ] `98` Back returns without changes.

## Navigation / robustness

- [ ] Back/Exit unwind correctly at every level (detail -> find -> group actions -> group
      -> type -> exit).
- [ ] Shrink the terminal below ~12 rows / 40 cols: the group screen falls back to a plain
      numbered list and Find falls back to a single-shot prompt search (no crash, no garbled
      absolute-row output).
