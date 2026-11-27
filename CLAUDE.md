# mavyex – notes for agents

Native Rust (egui/eframe 0.37, Rust 2024) dual-pane file manager for **Windows** – a "next-gen" mix of Explorer and
Total Commander. No web UI. User-facing docs (keys, search, install) are in `README.md`; this file is what you need
to change the code safely. The user writes Slovak; answer in Slovak, keep code/comments in English.

## Commands
- `cargo build` / `cargo test` (~28 tests: ops, search, deep, archive, editor, textdiff). `.\dev.ps1` (-Release, -Watch), `.\bump-version.ps1`,
  `.\release.ps1 -Bundle exe|setup|zip|all` (outputs to `release\`, git-ignored).
- **Kill the running app before building** (`taskkill /F /IM mavyex.exe`), otherwise the exe is locked.

## Source map (`src/`)
| File | Role |
|---|---|
| `main.rs` | entry: `--install/--uninstall`, `*_setup.exe` wizard, single-instance check, eframe options |
| `app.rs` (largest) | `App` state, all UI (`ui_pane`, dialogs, palette, jobs window), `Ev` event queue, `run(Action)`, themes |
| `model.rs` | `Entry`, `Tab` (entries/view/cursor/selection/history/sort/filter), `rebuild_view`, path helpers |
| `search.rs` | in-folder search: `SearchMode` (Smart/Fuzzy/Wildcard/Regex), `compile -> Matcher` (score/ranges), `glob_match` |
| `archive.rs` | zip/jar, tar, tar.gz/tgz, gz as folders (`Kind` by name; tar/gz listings via one stream pass, cached in `INDEX` by mtime/size; plain `.gz` = one file named after the archive): virtual paths (zip file path + inner path) (`split`, `list`, `extract`); hooked into `model::read_dir` and `ops`. Editable through *edit sessions* (staging folder, see Architecture) |
| `editor.rs` | F4 external editor: command templates (`{file}` `{dir}` `{line}`), per-extension rules, `launch` (resolves `.cmd`/`.exe` on PATH) |
| `textdiff.rs` | Ctrl+Shift+D side-by-side text diff in its own OS window (`similar` crate), own dark/light theme |
| `sftp.rs` | SFTP via `russh` + `russh-sftp` (pure Rust, `ring`; blocking API over a private tokio runtime) + Pageant agent auth; virtual paths `sftp:\user@host[:port]\~\dir`; connection pool, TOFU host keys, list/transfer/remove |
| `thumbs.rs` | async image thumbnails for the grid view (texture cache) |
| `preview.rs` | Quick Look panel (F3): background loader + UI |
| `props.rs` | Windows Properties dialog (`SHObjectProperties` for one item, `SHMultiFileProperties` for several); local paths only |
| `deep.rs` | "Subfolders" search: background BFS walker streaming `DeepMsg::Batch` |
| `actions.rs` | `Action` enum, `bindings()` key table, palette list, names |
| `ops.rs` | copy/move/trash/delete as background jobs (mpsc + cancel flag, progress, conflict naming) |
| `store.rs` | persisted state `%APPDATA%\mavyex\state.json` (favorites, frecency visits, session, theme, search opts, exclude) |
| `single.rs` | single instance: named mutex + pipe `\\.\pipe\mavyex-<USER>`; `mavyex <path>` opens a tab in the running app |
| `install.rs` | per-user self-installer (HKCU Path, Apps & features, Start menu). **Never run on the user's machine / untested** |
| `icons.rs`, `icon_draw.rs`, `build.rs` | shell icons per extension; generated app icon (also embedded in exe via build.rs) |

## Architecture rules
- **Immediate mode + event queue:** while drawing, UI code pushes `Ev` values; they are applied after drawing
  (`App::apply`). Do not mutate panes/tabs structurally mid-draw. Keyboard goes through `Action` + `bindings()`
  (most specific modifier combos first – the first match consumes the key).
- **Tab view:** `entries` = raw folder content (+ virtual `..` row, `is_up`); `view` = indices after filter/sort.
  Always call `rebuild_view` after changing `filter`, `search`, `sort`, `entries`. Selection is by path.
- **Search:** `Tab.search` (`SearchOpts`: mode, match_case, deep) + `Tab.filter` + `Tab.matcher`. Options are stored in
  `Store` and copied into every tab (`Ev::SetSearch`, new tabs inherit). Search-box keys are consumed only while it
  has focus. `..` is hidden while searching.
- **Subfolder search:** `rebuild_view -> sync_deep` compares a key (filter/opts/exclude/hidden) and (re)starts the
  walker; dropping `DeepJob` cancels it. Results are appended to `entries` with `deep = true` and
  `name` = relative path; `rebuild_view` shows only deep entries while deep search is active. `poll_deep` runs each
  tick. `reload()` re-attaches `deep_res`. Exclude list is manual (`Store.exclude`, comma separated globs) –
  nothing is excluded automatically (user decision). Deep results can be renamed inline (`Tab::note_rename` keeps the renamed item in `Tab.kept` so it stays listed until the query/folder changes); `Alt+Enter`
  (`ShowInFolder`) goes to the containing folder with the cursor on the item.
- **Background work:** file ops (`ops.rs`) and the deep walker run on threads and report via channels; the UI polls.
  Directory reads on the UI thread are still synchronous (slow network/huge dirs can freeze – known, unfixed).
- **FS watcher:** `notify` watches visible folders; events are debounced (200 ms calm / 1 s max); polling fallback
  for unwatched dirs.
- **Async navigation:** `Tab::go/go_back/go_forward` read the folder on a thread and wait 120 ms; slow folders
  set `Tab.loading` and finish in `poll_load` (called from `App::tick`). Visits are recorded only on arrival.
- **Archives:** a path is virtual when an ancestor is an existing `.zip/.jar` file (`archive::split`; `in_archive` is also true
  for the zip file itself – use `split_inner` when you mean "something inside"). Writes are allowed: the first write
  (`archive::real_for_write` / `real_for_edit`, called from `ops::run`, `do_rename`, `do_create`, F4) extracts the whole zip into
  a staging folder (an *edit session*, `SESSIONS`); while a session exists `list/read_head/size/extract/extract_temp` read the
  staging folder, and `ops` maps archive sources/dest to staging paths. `App::check_archives` (each tick) asks
  `Dialog::ArchiveUpdate` once no tab is inside the zip any more (`archive::save` rewrites the zip atomically, `discard` drops
  the copy); quit / window close ask first (`request_quit`). Extraction into staging is synchronous on the UI thread when it
  starts from rename/create/F4 (big zips freeze briefly).
- **F4 / editor:** `Action::EditFile` (F4; on a folder it falls back to `EditPath`). `Store.editor` + `Store.editor_rules`
  (`Dialog::Editor`). Remote files: `edit_file` downloads via `sftp::fetch_temp`, `App.remote_edits` polls the local mtime
  (700 ms quiet period) and uploads with `sftp::upload` on a thread. Upload path was not run against a real server yet.
- **Search filters:** `search::compile` strips `:size/:age/:date/:type/:ext` tokens into `Matcher.filters`
  (`filters_ok(&Entry)`); the rest is the name query. `rebuild_view` and the deep walker apply both.
- **Content search:** `SearchOpts.content` (implies deep). `deep.rs` feeds files to 4 scanner threads; results carry
  `Entry.snippet`; `rebuild_view` accepts deep entries without re-matching their names in content mode.
- **Views:** `Store.view` (`ViewMode`) and `Store.cols`; `Tab.row_h` / `Tab.grid_cols` are set by `ui_pane` each frame
  and used by `ensure_visible` and keyboard paging. `item_interact` is shared by rows and grid cells.
- **SFTP aliases:** `Store.aliases` (`user@host:port` → name) is mirrored into `sftp::ALIASES` (`set_aliases`); resolved in
  `sftp::from_url`, shown via `sftp::title/pretty`. `App::set_alias` saves immediately.
- **SFTP:** a path is remote when `sftp::is_remote` (prefix `sftp:`). `model::read_dir` dispatches to `sftp::list`
  (so async loading, search, preview all work); `ops` handles download/upload/remote delete; `App` branches for rename,
  create, open (temp download), delete (always permanent). Never touch the network from per-keystroke UI code (the
  path-bar suggestions skip remote dirs). Live-verified (read-only) against OpenSSH 10.2: handshake, Pageant auth, listing, download. Upload/delete/rename were not run on a real server. Do NOT switch back to `ssh2`/libssh2-WinCNG: it lacks curve25519/ecdh and fails key exchange with modern OpenSSH (`Session(-5)`).
- **Compare:** `App.compare` + `diff` (name → `Diff`) recomputed each tick; rows draw a colored stripe.
- **Window geometry** is saved in `Store.window` / `jobs_win` (`WinGeom`, validated against the virtual screen).
- **Operations window** is a separate OS window (`show_viewport_immediate`), not an in-app panel.
- Confirm dialogs (delete, F5/F6 transfer) focus the OK button; Enter on a focused button is left to egui.

## Gotchas / workflow
- Bash tool: heredocs with quotes or backslashes break or get mangled. For non-trivial edits write a script file
  with the Write tool and run `python -I script.py`, or use Edit. A hook runs rustfmt after edits.
- egui 0.37: `TextEdit::show(ui).response` is a `WidgetAtomResponse` – use `(*out.response).clone()` for an owned
  `Response`. Use `use eframe::egui` in modules. `Id::new` is deprecated (warnings are expected, harmless).
- Live testing: take window screenshots with PrintWindow of the mavyex process only (never full-desktop – it can
  capture the user's other windows). Back up and restore `%APPDATA%\mavyex\state.json` around tests. If you add temporary
  demo/env hooks, remove them afterwards (grep for `DEMO`).
- Tested live so far: single-instance forwarding, fs watcher, operations window, search modes rendering. Only compiled
  (not exercised): installer, tab drag/drop, slow-click rename, context menus, Right-arrow path autocomplete,
  subfolder search UI, Alt+Enter.

## Ideas offered but not done
See `ROADMAP.md`. Notably: "Open in mavyex" Explorer context menu, bulk rename, workspaces.
