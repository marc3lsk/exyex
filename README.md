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
| Alt+Shift+F5 | zip the selection into a new `.zip` in this folder |
| F7 / Shift+F4 | new folder / new file |
| F8, Del / Shift+Del | delete to Recycle Bin / permanently |
| F2, Shift+F6 | rename |
| Ctrl+C / X / V | clipboard (inside exyex) |
| Ctrl+T / W / Shift+T, Ctrl+Tab | new / close / reopen tab, next tab |
| Ctrl+L, Alt+D | edit path |
| F4 | edit the file under the cursor in the external editor (on a folder: edit path) – see *Editing files* |
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
Renaming a result (F2) keeps it in the list even if the new name no longer matches the query.
Reparse points / symlinks are not followed, hidden items follow the "show hidden" setting.

A manual **exclude** box appears next to the chip: names or wildcards separated by commas, e.g.
`node_modules, .git, *.tmp`. Nothing is excluded unless you type it; the list is remembered.

### Filters (`:` prefix)
Tokens that start with a colon filter by metadata and combine with the name query and with each other:

| Filter | Meaning |
|---|---|
| `:size>10mb` `:size<1kb` `:size=0` | file size (`b kb mb gb tb`; items that are folders never match) |
| `:age<7d` / `:modified<7d` | modified less than 7 days ago (`min h d w mo y`); `>` = older |
| `:date>2024-01-01` / `:modified<2023-06-01` | modified after / before a date (`=` = that day) |
| `:type=img` / `:type=video,audio` / `:ext=rs,toml` | kind: `img video audio doc archive code dir file` or plain extensions |

Example: `report :type=doc :age<30d :size>100kb`. A malformed filter turns the box red.

### Content search (Alt+T)
The **Content** chip searches *inside* files in all subfolders (4 threads, binary files and files over 8 MB are skipped).
The query is matched per line using the selected mode (Smart / Fuzzy / Wildcard / Regex); each result shows the first
matching line (`12: text  (+3 more)`). Narrow it with filters, e.g. `TODO :ext=rs :age<7d`, and with the exclude box.

## Views and columns
**Alt+V** (or the ☰ button bottom right of a pane) cycles *Details → Compact → Grid*. Grid shows image thumbnails
(png/jpg/gif/bmp/webp/ico/svg) and large shell icons; arrow keys move in 2D there. Right-click the column header or the
view button to choose the columns (Type / Size / Modified). The choice is remembered.

## SFTP (Pageant)
Browse and transfer files on SSH servers; authentication uses the keys loaded in **Pageant** (no passwords or key files are
handled by exyex). **Ctrl+Shift+K** (or palette → *Connect to SFTP server*) opens the path bar with `sftp://`; type
`sftp://user@host[:port]/path` (a bare `sftp://user@host` opens the home folder) and press Enter.

- Remote folders behave like local ones: tabs, history, breadcrumbs, favorites (★), recents, filter/search by name,
  Quick Look (text, small images), compare panes. The path bar shows them as `sftp:\\user@host\~\dir` (`~` = remote home).
- **Favorites:** the ☆ chip / `Ctrl+B` favorites the current remote folder; right-click any folder (also a server in the
  `sftp:` list) → *Add / remove favorite ★*.
- **Aliases:** right-click → *SFTP alias…* (or palette → *SFTP: set alias for this connection*) gives a server a short name,
  e.g. `prod`. Then type `sftp://prod/var/www`; the alias is shown in tab titles, the `sftp:` list, favorites and recents
  (and the path-bar suggestions also match it). An empty alias removes it. Stored in `state.json`.
- `sftp:` (palette → *SFTP: known connections*, or `..` from a server root) lists servers from your favorites and recents.
- F5 / F6 / drag / clipboard copy and move between a remote and a local folder (both directions, folders recursively,
  progress in the operations window, mtime preserved). Remote → remote is not supported (copy through a local folder).
- New folder / new file / rename / delete work on the server. Delete is always permanent (there is no recycle bin).
- Enter on a remote file downloads a temporary copy and opens it (changes are **not** uploaded back).
- The first connection to a host saves its key fingerprint in `%APPDATA%\exyex\known_hosts.json`; a changed key is refused.
- Not available remotely: content search (name search in subfolders works but is slow), thumbnails; remote tabs are not
  restored at startup.

## Quick Look, compare, archives
- **F3 – Quick Look:** side panel that follows the cursor: text (first 64 KB), images (png/jpg/gif/bmp/webp/ico),
  folder summaries, zip contents, hex head for binary files. Close with the ✕ or F3 again.
- **Ctrl+M – compare panes:** marks items by name against the other pane: green = only here, orange = newer here,
  red = older here, purple = different size. **Ctrl+Shift+M** selects what is missing/newer/different here;
  **Alt+Shift+S** does that and opens the copy dialog (one-way sync to the other pane). Folders are compared by existence.
- **Ctrl+Shift+D – text compare:** side-by-side diff of two text files in its own window: select two files in one pane, or put the cursor on a
  file in each pane. Changed lines are paired and the changed characters highlighted; **F7 / F8** jump to the previous / next
  difference, checkboxes ignore whitespace / case, Esc closes. Works on local, SFTP and zip-inner files (text only, up to 4 MB).
- **Zip / jar as folders:** Enter on a `.zip` browses it (path bar, search and subfolder search work inside).
  Copy out (F5, drag, clipboard) extracts. Archives are **editable**: copy / move / paste files in, delete, rename,
  new folder / file and F4 all work. The first change extracts the archive into a working copy; when you leave the
  archive (all tabs out of it) exyex asks **Update archive / Discard changes / Later**, and also before exiting.
  Updating rewrites the zip (everything is re-compressed with deflate; dates are kept).
- **Tabs:** drag a tab left/right inside its strip to reorder (the blue mark shows where it lands).
- Slow or network folders load in the background (spinner in the footer); window position/size is remembered.
- Terminals: Alt+Shift+P PowerShell, Alt+Shift+C cmd, Alt+Shift+E Explorer here, Ctrl+Shift+O Windows Terminal.

## Editing files (F4)
**F4** opens the file under the cursor in an external editor (default: Notepad). Set your own with
**Ctrl+P → Settings: editor for F4**: a command template (`code {file}`, `"C:\Program Files\Notepad++\notepad++.exe" -n{line} {file}`;
placeholders `{file}` `{dir}` `{line}`, the file is appended when `{file}` is missing) and optional per-extension rules,
one per line: `rs,toml = code {file}`. Files inside a zip are edited in the archive's working copy (see above).
**SFTP files:** F4 downloads the file, opens the editor and **uploads it back every time you save**
(the status bar confirms each upload).

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
