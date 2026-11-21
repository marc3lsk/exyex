# exyex

Native (Rust + egui) dual-pane file manager for Windows — a keyboard-first alternative to Explorer and Total Commander.

    cargo run --release

## Features
- Two panes, multiple tabs per pane, back/forward history per tab, session restore
- Smart path bar: click/Ctrl+L to edit, paste any path (quotes, `%ENV%`, `~`, `file:///`), Tab-completion with suggestions
- Command palette (Ctrl+P) with fuzzy search of commands, favorites and frequently used folders; Ctrl+D = folder jump (frecency)
- Copy / move / delete (Recycle Bin) / rename / new folder / new file, progress + cancel (Esc), drag between panes, drop from Explorer
- Filter (Ctrl+F: `text`, `*.ext`, `!exclude`), natural sorting, type-ahead jump, real shell icons

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

State lives in `%APPDATA%\exyex\state.json`.
