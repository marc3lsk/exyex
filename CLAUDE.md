# mavyex – notes for agents

C# / WPF on **.NET 10** dual-pane file manager for **Windows** – a "next-gen" mix of Explorer and Total Commander.
Branch `dotnet10` is the 1:1 port of the Rust/egui version (branch `main`, versions ≤ 0.1.x). User-facing docs (keys,
search, install) are in `README.md`. The user writes Slovak; answer in Slovak, keep code/comments in English.

## Commands
- `dotnet build mavyex.slnx`, `dotnet test mavyex.slnx` (~150 xUnit tests in `tests/Mavyex.Tests`: VPath, store,
  ops, archive, search/deep, sftp (pure parts), editor, preview, text diff).
- `.\dev.ps1` (-Watch, -DataDir), `.\bump-version.ps1` (`<Version>` in the csproj), `.\release.ps1 -Bundle exe|setup|zip|all
  [-SelfContained]` (single-file publish → `release\`, git-ignored).
- `nuget.config` clears machine package sources (a company feed in the global config is often unreachable).
- A running `mavyex.exe` from `src\Mavyex\bin` locks the build output – stop it first. Never kill the user's
  installed mavyex without asking.
- `MAVYEX_HOME=<dir>`: other settings folder **and** its own single-instance mutex/pipe → test instances run next to
  the real one. `MAVYEX_SOFTWARE_RENDER=1`: software rendering so PrintWindow screenshots show WPF content.

## Source map (`src/Mavyex/`)
| File | Role |
|---|---|
| `Program.cs` | `Main`: `--install/--uninstall`, `*_setup.exe` wizard, single instance, crash.log, `Application` (no App.xaml) |
| `Core/VPath.cs` | **Rust `Path` semantics on string paths** (Parent/FileName/StartsWith/StripPrefix/Join/LexicalNormalize, ParseUserPath). Use it for all virtual paths |
| `Core/Entry.cs`, `Fs.cs` | listing row; `Fs.ReadDir` dispatches local / archive / SFTP (model.rs read_dir), `WithUp`, `IsDirPath`, `DirMtime` |
| `Core/Tab.cs` | model.rs Tab: entries/view/cursor/selection(by path)/history/sort/filter/deep search/async navigation (120 ms, then `PollLoad`) |
| `Core/Store.cs` | `state.json` (snake_case, Rust-compatible) + SearchOpts/SortKey/ViewMode/Cols/WinGeom |
| `Core/Actions.cs`, `Text.cs` | `Act` enum, palette list, names, `Bindings` (most specific modifiers first), shortcut labels; natural sort, sizes, palette fuzzy |
| `Core/TextDiff.cs` | non-UI diff engine for the compare window (DiffPlex) |
| `Services/Archive.cs` | zip/jar, tar, tar.gz/tgz, gz as folders; listing cache; edit sessions (staging folder, `Save`/`Discard`) |
| `Services/Sftp.cs` | SSH.NET + SshNet.Agent (Pageant only); pool, TOFU `known_hosts.json` (Rust format), aliases, transfers, chown |
| `Services/Ops.cs`, `RecycleBin.cs` | background copy/move/trash/delete/zip jobs (`Job` polled by the UI, `Completed` event), conflict policies |
| `Services/Search.cs`, `Deep.cs` | Smart/Fuzzy/Wildcard/Regex + `:size :age :date :type :ext` filters; subfolder / content walker (`Drain`, callback) |
| `Services/Editor.cs`, `Props.cs` | F4 command templates + per-extension rules; Windows Properties dialog |
| `Native/SingleInstance.cs`, `Installer.cs` | mutex + CurrentUserOnly named pipe; per-user installer (PATH, Apps & features, Start menu). **Installer never run on the user's machine** |
| `UI/MainWindow*.cs` | app.rs App: state + `Render()`; `.Actions` (Run), `.Events` (keys, mouse, menus, path bar, drag & drop), `.Overlays` (palette, dialogs) |
| `UI/PaneView.cs` | one pane: tab strip, drives, nav + path bar/suggestions, search bar, header, virtualized list, footer |
| `UI/FileRows.cs` | custom-drawn `FileRow` / `GridRow` (OnRender like egui), `RowContext`, `RowPaint.Columns` |
| `UI/Widgets.cs` | `Paint` colours/icons, `IconBtn`, `Chip`, `TabPill` |
| `UI/PreviewPanel.cs`, `PreviewLoader.cs`, `Thumbs.cs`, `ShellIcons.cs`, `ImageDecode.cs` | Quick Look, grid thumbnails, shell icons |
| `UI/TextDiffWindow.cs`, `JobsWindow.cs` | text compare window; operations window |
| `UI/Theme.cs`, `Styles.xaml` | light/dark DynamicResources (PanelBg Fg WeakFg Accent RowSel …), dark title bar |

## Architecture rules
- **State → view:** events mutate state (Panes/Tabs/Store/dialog/palette), then call `Render()`; PaneView updates its
  persistent elements and only rebuilds the list's ItemsSource when `Tab.View` changed (new list instance after
  `RebuildView`). Always call `Tab.RebuildView` after changing Filter / Search / Sort / Entries. Selection is by path.
- Rows are drawn by `FileRow`/`GridRow` from `RowContext`; realized rows are tracked in `PaneView.Realized` and
  invalidated on render. Mouse handling is on the ListBox (Preview events → `HitRow`).
- Keyboard: `MainWindow.OnPreviewKey` → palette / dialog / text boxes first (their own PreviewKeyDown handlers), then
  `Actions.Bindings`, Ctrl+1..9, cursor keys, Esc; type-ahead via PreviewTextInput.
- `Tick()` (120 ms DispatcherTimer): drives, jobs, archive "save changes?" check, remote edit uploads, FileSystemWatcher
  events (debounced 200 ms / 1 s), compare marks, pending navigations, deep results, mtime polling for unwatched
  folders, window geometry, debounced state save (1 s).
- Palette and dialogs are in-window overlays; the operations window and text diff are separate Windows.
- Drag & drop is real OLE drag: custom `mavyex-paths` format (+ FileDrop for local files, so dropping into Explorer
  works). Internal: Shift = move, Ctrl = copy, else move within a volume; from Explorer: copy. Tabs: `mavyex-tab`.
- Archives / SFTP / ops / search behave exactly like the Rust modules (see their file headers for deviations).
- **Compare (Ctrl+M), deviation from Rust:** same size + different date is decided by content, not by the date –
  local pairs byte by byte, anything involving SFTP / archives by SHA-256 (`Services/ContentHash.cs`; remote side via
  `Sftp.Sha256Many` = one `sha256sum` per server over the exec connection, nothing downloaded). Pending checks are
  not marked; if hashing fails (no sha256sum) the old date rule applies. Same size + same date (±2 s) = identical
  without hashing; different size = marked (newer / older by date, else "different size").

## Gotchas / workflow
- WPF + `InvariantGlobalization` crashes – never enable it. `Path` is aliased to System.IO.Path (GlobalUsings.cs);
  use `System.Windows.Shapes.Path` explicitly for shapes.
- Bash tool heredocs mangle backslashes: use the Edit/Write tools or a script file.
- Live testing: test instance with `MAVYEX_HOME=<scratch>` + `MAVYEX_SOFTWARE_RENDER=1`, screenshot only that process's
  window with PrintWindow (popups like the path suggestions are separate windows and don't show up). Reset its
  state.json before each run, otherwise it opens the last folders. Temporary demo hooks must be removed (grep `DEMO`).
- Tested live (screenshots): panes in dark theme, shell icons, search bar + highlight, compare marks, Quick Look,
  grid view, palette, copy dialog, browsing a zip, inline rename. **Not exercised:** real keyboard/mouse input, drag &
  drop, slow-click rename, context menus, tab dragging, SFTP against a server, jobs window with a real long copy,
  text diff from the app, installer.

## Ideas offered but not done
See `ROADMAP.md`. Notably: "Open in mavyex" Explorer context menu, bulk rename, workspaces.
