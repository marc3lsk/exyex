# exyex

Native (Rust + egui) dual-pane file manager for Windows — a keyboard-first alternative to Explorer and Total Commander.

    cargo run --release

## Features
- Two panes, multiple tabs per pane, back/forward history per tab, session restore
- Smart path bar: click/Ctrl+L to edit, paste any path (quotes, `%ENV%`, `~`, `file:///`), Tab-completion with suggestions
- Command palette (Ctrl+P) with fuzzy search of commands, favorites and frequently used folders; Ctrl+D = folder jump (frecency)
- Copy / move / delete (Recycle Bin) / rename / new folder / new file, progress + cancel (Esc), drag between panes, drop from Explorer
- Search in the folder (Ctrl+F), live while you type, matches are highlighted - see below
- Natural sorting, type-ahead jump, real shell icons

## Keys (Total Commander + Explorer hybrid)
| Key | Action |
|---|---|
| Tab | switch pane |
| F5 / F6 | copy / move to other pane |
| F7 / Shift+F4 | new folder / new file |
| F8, Del / Shift+Del | delete to Recycle Bin / permanently |
| F2, Shift+F6 | rename |
| Ctrl+C / X / V | clipboard (inside exyex) |
| Ctrl+T / W / Shift+T, Ctrl+Tab | new / close / reopen tab, next tab |
| Ctrl+L, F4, Alt+D | edit path |
| Alt+←/→/↑, Backspace | back / forward / up |
| Space, Ins | toggle selection; Ctrl+A / I select all / invert; Shift+arrows range |
| Ctrl+F3..F6 | sort name / ext / date / size |
| Ctrl+H | hidden files; Ctrl+B favorite; Ctrl+U swap panes; Ctrl+Shift+C copy path |

## Search (Ctrl+F)
Type to filter the current folder instantly. Mode chips next to the box (or Alt+1..4), Alt+C = match case:

| Mode | What it does |
|---|---|
| Smart | words are combined: plain text = contains, `*.rs` / `?` = wildcard, `!draft` = exclude, `~rdme` = fuzzy, `re:^img_\d+` = regex |
| Fuzzy | letters must appear in order (`rdme` finds `README.md`); best matches first |
| Wildcard | `*` and `?`, whole name; plain text = contains |
| Regex | Rust regex, matched anywhere in the name; an invalid pattern turns the box red and keeps the list |

Up/Down/PageUp/PageDown move through the results while the box has focus, Enter opens the selected result,
Tab hands the keyboard to the list, Esc closes the search.

### Subfolders (Alt+S)
The **Subfolders** chip searches the whole tree below the current folder, in the background; results stream in and
show the path relative to the folder (`src\ui\README.md`). The query is matched against that relative path.
Enter opens a file, or goes into a folder. The count shows `…` while running and `+ (limit)` after 50 000 results.
**Alt+Enter** (or right-click → *Open containing folder*) jumps to the folder of the selected result with the cursor on that file.
Reparse points / symlinks are not followed, hidden items follow the "show hidden" setting.

A manual **exclude** box appears next to the chip: names or wildcards separated by commas, e.g.
`node_modules, .git, *.tmp`. Nothing is excluded unless you type it; the list is remembered.

## Install / command line
Run `exyex_<version>_x64_setup.exe` (or `exyex.exe --install`). It installs per user (no admin):
copies to `%LOCALAPPDATA%\Programs\exyex`, adds that folder to your user PATH, adds a Start menu shortcut and
an "Apps & features" entry. `exyex --uninstall` (or Apps & features) removes it again.

Then, in any **new** terminal:

    exyex .            # opens the current folder as a new tab in the running exyex
    exyex C:\some\dir  # same; starts exyex if it is not running yet

exyex is single-instance: a second `exyex <path>` hands the path to the running window and exits.
If that folder is already open in a tab, it just switches to it.

State lives in `%APPDATA%\exyex\state.json`.
