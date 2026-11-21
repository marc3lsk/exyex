use crate::{
    actions::{self, Action, Binding, PALETTE_ACTIONS},
    icons::Icons,
    model::*,
    ops::{self, Job, JobState, Msg, OpKind},
    search::{SearchMode, SearchOpts},
    store::*,
};
use eframe::egui::{self, Align, Align2, Color32, Context, FontId, Key, Layout, Modifiers, Pos2, Rect, Sense, Stroke, Ui, pos2, vec2};
use std::{
    collections::{HashMap, HashSet},
    path::{Path, PathBuf},
    sync::atomic::Ordering,
    time::{Duration, Instant},
};

const ACCENT: Color32 = Color32::from_rgb(76, 139, 245);

// ------------------------------------------------------------------- types

struct Sugg {
    path: PathBuf,
    /// "" = subfolder completion, otherwise a label like "favorite" / "recent".
    tag: &'static str,
}

struct PathEdit {
    text: String,
    orig: String,
    fresh: bool,
    sugg_for: String,
    sugg: Vec<Sugg>,
    sel: Option<usize>,
    popup_rect: Rect,
}

struct Pane {
    /// (active tab, tab count) last frame: when it changes the strip scrolls to the active tab.
    seen_tabs: (usize, usize),
    tabs: Vec<Tab>,
    active: usize,
    path_edit: Option<PathEdit>,
    focus_filter: bool,
    rect: Rect,
}

impl Pane {
    fn tab(&self) -> &Tab {
        &self.tabs[self.active]
    }
    fn tab_mut(&mut self) -> &mut Tab {
        &mut self.tabs[self.active]
    }
}

enum Dialog {
    NewFolder { text: String, fresh: bool },
    NewFile { text: String, fresh: bool },
    Delete { paths: Vec<PathBuf>, permanent: bool },
    /// F5 / F6: confirm copying or moving the selection to the other pane.
    Transfer { kind: OpKind, paths: Vec<PathBuf>, dest: PathBuf },
}

/// In-place rename editor shown inside a file-list row.
struct InlineRename {
    pi: usize,
    dir: PathBuf,
    path: PathBuf,
    text: String,
    fresh: bool,
    seen_focus: bool,
    commit: bool,
    cancel: bool,
}

struct Palette {
    query: String,
    folders_only: bool,
    sel: usize,
    fresh: bool,
    folders: Vec<(PathBuf, &'static str, i32)>,
}

enum Item {
    Action(Action),
    Folder(PathBuf, &'static str),
    Goto(PathBuf),
}

#[derive(Clone, Copy)]
enum OpenMode {
    Here,
    NewTab,
    OtherPane,
}

struct Drag {
    from: usize,
    paths: Vec<PathBuf>,
}

#[derive(Clone, Copy)]
enum TabCmd {
    Close,
    Others,
    Left,
    Right,
    Duplicate,
    OpenOther,
    MoveOther,
    CopyPath,
}

enum Ev {
    Click { pi: usize, vi: usize, ctrl: bool, shift: bool },
    Open { pi: usize, vi: usize },
    MiddleOpen { pi: usize, vi: usize },
    ContextAt { pi: usize, vi: usize },
    Sort(usize, SortKey),
    Tab(usize, usize),
    CloseTab(usize, usize),
    NewTab(usize),
    SetSearch(SearchOpts),
    SetExclude(String),
    TabDrag(usize, usize),
    TabRects(usize, Vec<Rect>),
    TabMenu(usize, usize, TabCmd),
    Go(usize, PathBuf),
    GoTyped(usize, String),
    DragStart { pi: usize, paths: Vec<PathBuf> },
    DropHover(PathBuf),
    Run(Action),
}

pub struct App {
    panes: [Pane; 2],
    active: usize,
    store: Store,
    icons: Icons,
    bindings: Vec<Binding>,
    palette: Option<Palette>,
    dialog: Option<Dialog>,
    jobs: Vec<Job>,
    jobs_open: bool,
    clipboard: Option<(Vec<PathBuf>, bool)>,
    drag: Option<Drag>,
    drop_hover: Option<PathBuf>,
    status: String,
    closed_tabs: Vec<(usize, PathBuf)>,
    inline: Option<InlineRename>,
    /// Pending slow-second-click rename: (pane, dir, path, time of click).
    rename_timer: Option<(usize, PathBuf, PathBuf, Instant)>,
    prev_active: usize,
    /// Requests from later `exyex <path>` launches (open as a tab here).
    ipc: Option<crate::single::Server>,
    /// Tab being dragged to the other pane: (pane, tab index).
    tab_drag: Option<(usize, usize)>,
    tab_rects: [Vec<Rect>; 2],
    preview: bool,
    pv: crate::preview::Previewer,
    compare: bool,
    diff: [HashMap<String, Diff>; 2],
    typeahead: (String, Instant),
    drives: Vec<PathBuf>,
    /// OS file-system notifications for the folders shown in the active tabs.
    watcher: Option<notify::RecommendedWatcher>,
    fs_rx: std::sync::mpsc::Receiver<Vec<PathBuf>>,
    /// folders successfully watched (key -> path); others fall back to polling
    watched: HashMap<String, PathBuf>,
    watch_failed: HashSet<String>,
    /// folders with pending external changes (key -> first event time)
    fs_dirty: HashMap<String, Instant>,
    fs_last: Option<Instant>,
    dirty: bool,
    last_save: Instant,
    last_title: String,
    quit: bool,
}

// -------------------------------------------------------------------- setup

/// Case-insensitive, separator-normalized key for comparing folder paths.
fn path_key(p: &Path) -> String {
    p.to_string_lossy().to_lowercase().trim_start_matches(r"\\?\").trim_end_matches('\\').to_string()
}

fn list_drives() -> Vec<PathBuf> {
    #[cfg(windows)]
    {
        let mask = unsafe { windows::Win32::Storage::FileSystem::GetLogicalDrives() };
        (0..26)
            .filter(|i| mask & (1 << i) != 0)
            .map(|i| PathBuf::from(format!("{}:\\", (b'A' + i as u8) as char)))
            .collect()
    }
    #[cfg(not(windows))]
    {
        vec![PathBuf::from("/")]
    }
}

fn setup_style(ctx: &Context) {
    // Prefer Segoe UI (full Unicode coverage, native look) when available.
    let mut fonts = egui::FontDefinitions::default();
    let mut add = |key: &str, file: &str, front: bool| {
        if let Ok(bytes) = std::fs::read(format!("C:\\Windows\\Fonts\\{file}")) {
            fonts.font_data.insert(key.into(), std::sync::Arc::new(egui::FontData::from_owned(bytes)));
            for fam in [egui::FontFamily::Proportional, egui::FontFamily::Monospace] {
                let list = fonts.families.entry(fam).or_default();
                if front {
                    list.insert(0, key.into());
                } else {
                    list.push(key.into());
                }
            }
        }
    };
    add("seguisym", "seguisym.ttf", false);
    add("msgothic", "msgothic.ttc", false);
    add("segoeui", "segoeui.ttf", true);
    ctx.set_fonts(fonts);

    use egui::Theme;
    let mut dark = egui::Visuals::dark();
    dark.selection.bg_fill = ACCENT.gamma_multiply(0.6);
    dark.panel_fill = Color32::from_rgb(28, 29, 33);
    dark.extreme_bg_color = Color32::from_rgb(20, 21, 24);
    dark.widgets.noninteractive.bg_stroke = Stroke::new(1.0, Color32::from_gray(52));
    ctx.set_visuals_of(Theme::Dark, dark);

    let mut light = egui::Visuals::light();
    light.selection.bg_fill = ACCENT.gamma_multiply(0.45);
    light.panel_fill = Color32::from_rgb(246, 246, 248);
    light.extreme_bg_color = Color32::WHITE;
    light.override_text_color = Some(Color32::from_gray(20));
    light.widgets.noninteractive.bg_stroke = Stroke::new(1.0, Color32::from_gray(210));
    ctx.set_visuals_of(Theme::Light, light);

    ctx.all_styles_mut(|s| {
        s.spacing.item_spacing = vec2(6.0, 4.0);
        s.spacing.button_padding = vec2(6.0, 2.0);
    });
}

fn apply_theme(ctx: &Context, t: ThemeChoice) {
    ctx.set_theme(match t {
        ThemeChoice::System => egui::ThemePreference::System,
        ThemeChoice::Light => egui::ThemePreference::Light,
        ThemeChoice::Dark => egui::ThemePreference::Dark,
    });
}

/// Neutral overlay (hover etc.): white on dark, black on light.
fn tint(dark: bool, alpha: u8) -> Color32 {
    if dark { Color32::from_white_alpha(alpha) } else { Color32::from_black_alpha(alpha) }
}
fn line_color(dark: bool) -> Color32 {
    if dark { Color32::from_gray(52) } else { Color32::from_gray(205) }
}
fn selected_text(dark: bool) -> Color32 {
    if dark { Color32::from_rgb(255, 196, 92) } else { Color32::from_rgb(176, 84, 0) }
}

impl App {
    pub fn new(cc: &eframe::CreationContext<'_>, ipc: Option<crate::single::Server>) -> Self {
        if let Some(s) = &ipc {
            s.set_ctx(cc.egui_ctx.clone());
        }
        setup_style(&cc.egui_ctx);
        let (fs_tx, fs_rx) = std::sync::mpsc::channel::<Vec<PathBuf>>();
        let repaint = cc.egui_ctx.clone();
        let watcher = notify::recommended_watcher(move |res: notify::Result<notify::Event>| {
            if let Ok(ev) = res {
                if !matches!(ev.kind, notify::EventKind::Access(_)) {
                    let _ = fs_tx.send(ev.paths);
                    repaint.request_repaint(); // wake the UI even when idle
                }
            }
        })
        .ok();
        let store = Store::load();
        apply_theme(&cc.egui_ctx, store.theme);
        let home = dirs::home_dir().unwrap_or_else(|| PathBuf::from("C:\\"));
        let alt = dirs::download_dir().or_else(dirs::document_dir).unwrap_or_else(|| home.clone());
        let show_hidden = store.show_hidden;

        let make = |idx: usize, default: &Path| -> Pane {
            let mut tabs: Vec<Tab> = vec![];
            let mut active = 0;
            if let Some(ps) = store.panes.get(idx) {
                for ts in &ps.tabs {
                    let p = PathBuf::from(&ts.path);
                    if p.is_dir() {
                        let mut t = Tab::new(p, show_hidden);
                        t.search = store.search;
                        t.exclude = store.exclude.clone();
                        t.sort = ts.sort;
                        t.asc = ts.asc;
                        t.rebuild_view(None);
                        tabs.push(t);
                    }
                }
                active = ps.active.min(tabs.len().saturating_sub(1));
            }
            if tabs.is_empty() {
                let mut t = Tab::new(default.to_path_buf(), show_hidden);
                t.search = store.search;
                t.exclude = store.exclude.clone();
                tabs.push(t);
            }
            Pane { tabs, active, path_edit: None, focus_filter: false, rect: Rect::NOTHING, seen_tabs: (usize::MAX, 0) }
        };

        let mut app = App {
            panes: [make(0, &home), make(1, &alt)],
            active: store.active_pane.min(1),
            icons: Icons::default(),
            bindings: actions::bindings(),
            palette: None,
            dialog: None,
            jobs: vec![],
            jobs_open: true,
            clipboard: None,
            drag: None,
            drop_hover: None,
            status: "Ready — Ctrl+P opens the command palette".into(),
            closed_tabs: vec![],
            inline: None,
            rename_timer: None,
            prev_active: 0,
            ipc,
            tab_drag: None,
            tab_rects: [vec![], vec![]],
            preview: false,
            pv: Default::default(),
            compare: false,
            diff: [HashMap::new(), HashMap::new()],
            typeahead: (String::new(), Instant::now()),
            drives: list_drives(),
            watcher,
            fs_rx,
            watched: HashMap::new(),
            watch_failed: HashSet::new(),
            fs_dirty: HashMap::new(),
            fs_last: None,
            dirty: false,
            last_save: Instant::now(),
            last_title: String::new(),
            quit: false,
            store,
        };
        // `exyex <folder>` opens the folder in a new tab of the active pane.
        if let Some(arg) = std::env::args().nth(1) {
            if let Some(p) = parse_user_path(&arg, &std::env::current_dir().unwrap_or_default()) {
                if p.is_dir() {
                    app.open_new_tab(app.active, p);
                }
            }
        }
        app
    }

    // ------------------------------------------------------------- helpers

    fn tab(&self) -> &Tab {
        self.panes[self.active].tab()
    }
    fn tab_mut(&mut self) -> &mut Tab {
        self.panes[self.active].tab_mut()
    }
    fn say(&mut self, s: impl Into<String>) {
        self.status = s.into();
    }

    fn save_state(&mut self) {
        self.store.panes = self
            .panes
            .iter()
            .map(|p| PaneSession {
                active: p.active,
                tabs: p
                    .tabs
                    .iter()
                    .map(|t| TabSession { path: t.path.to_string_lossy().into_owned(), sort: t.sort, asc: t.asc })
                    .collect(),
            })
            .collect();
        self.store.active_pane = self.active;
        self.store.save();
        self.dirty = false;
        self.last_save = Instant::now();
    }

    fn navigate(&mut self, pi: usize, to: PathBuf) -> bool {
        match self.panes[pi].tab_mut().go(to.clone()) {
            Ok(()) => {
                if self.panes[pi].tab().loading.is_none() {
                    self.store.record_visit(&to);
                    self.dirty = true;
                }
                true
            }
            Err(e) => {
                self.say(e);
                false
            }
        }
    }

    fn open_new_tab(&mut self, pi: usize, path: PathBuf) {
        let sh = self.store.show_hidden;
        let mut t = Tab::new(path.clone(), sh);
        t.search = self.store.search;
        t.exclude = self.store.exclude.clone();
        if let Some(cur) = self.panes[pi].tabs.get(self.panes[pi].active) {
            t.sort = cur.sort;
            t.asc = cur.asc;
            t.rebuild_view(None);
        }
        let pane = &mut self.panes[pi];
        pane.tabs.insert(pane.active + 1, t);
        pane.active += 1;
        self.store.record_visit(&path);
        self.dirty = true;
    }

    fn reload_all(&mut self) {
        for p in &mut self.panes {
            p.tab_mut().reload(None);
        }
    }

    fn other(&self) -> usize {
        1 - self.active
    }

    fn start_op(&mut self, ctx: &Context, kind: OpKind, srcs: Vec<PathBuf>, dest: Option<PathBuf>) {
        if srcs.is_empty() {
            return;
        }
        self.jobs.push(ops::spawn(kind, srcs, dest, ctx.clone()));
        self.jobs_open = true; // show the operations window
    }

    fn poll_jobs(&mut self, ctx: &Context) {
        let mut finished: Vec<(OpKind, JobState)> = vec![];
        for job in self.jobs.iter_mut().filter(|j| j.state == JobState::Running) {
            while let Ok(m) = job.rx.try_recv() {
                match m {
                    Msg::Progress { done, total, current } => {
                        job.done = done;
                        job.total = total;
                        job.current = current;
                    }
                    Msg::Done(r) => {
                        job.finished = Some(Instant::now());
                        job.state = match r {
                            Ok(()) => JobState::Done,
                            Err(e) if e == "Cancelled" => JobState::Cancelled,
                            Err(e) => JobState::Failed(e),
                        };
                        finished.push((job.kind, job.state.clone()));
                    }
                }
            }
        }
        if !finished.is_empty() {
            for p in &mut self.panes {
                p.tab_mut().selected.clear();
            }
            self.reload_all();
            if let Some((kind, state)) = finished.pop() {
                let msg = match state {
                    JobState::Done => match kind {
                        OpKind::Copy => "Copied.",
                        OpKind::Move => "Moved.",
                        OpKind::Trash => "Moved to Recycle Bin.",
                        OpKind::Delete => "Deleted.",
                    }
                    .to_string(),
                    JobState::Cancelled => "Cancelled.".to_string(),
                    JobState::Failed(e) => format!("⚠ {e}"),
                    JobState::Running => String::new(),
                };
                self.say(msg);
            }
        }
        // Successful / cancelled entries disappear after a few seconds; failures stay until dismissed.
        self.jobs.retain(|j| !(matches!(j.state, JobState::Done | JobState::Cancelled) && j.finished.is_some_and(|t| t.elapsed() > Duration::from_secs(3))));
        if self.jobs.iter().any(|j| j.finished.is_some()) {
            ctx.request_repaint_after(Duration::from_millis(500));
        }
    }

    /// `exyex <path>` from another terminal: show the folder as a tab in this window.
    fn handle_external_open(&mut self, ctx: &Context, req: Option<PathBuf>) {
        ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(false));
        ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
        let Some(p) = req else { return };
        let (dir, focus) = if p.is_dir() {
            (p, None)
        } else if p.is_file() {
            match (p.parent(), p.file_name()) {
                (Some(d), Some(n)) => (d.to_path_buf(), Some(n.to_string_lossy().into_owned())),
                _ => return,
            }
        } else {
            self.say(format!("⚠ Path not found: {}", p.display()));
            return;
        };
        // Already open somewhere? Just switch to it.
        let existing = self.panes.iter().enumerate().find_map(|(pi, pane)| pane.tabs.iter().position(|t| t.path == dir).map(|ti| (pi, ti)));
        if let Some((pi, ti)) = existing {
            self.panes[pi].active = ti;
            self.active = pi;
        } else {
            let pi = self.active;
            self.open_new_tab(pi, dir);
        }
        if let Some(name) = focus {
            self.tab_mut().reload(Some(name));
        }
        self.dirty = true;
    }

    /// Keeps exactly the folders of the two active tabs watched.
    fn sync_watches(&mut self) {
        let Some(w) = self.watcher.as_mut() else { return };
        use notify::{RecursiveMode, Watcher};
        let want: HashMap<String, PathBuf> = self.panes.iter().map(|p| (path_key(&p.tab().path), p.tab().path.clone())).collect();
        let stale: Vec<String> = self.watched.keys().filter(|k| !want.contains_key(*k)).cloned().collect();
        for k in stale {
            if let Some(path) = self.watched.remove(&k) {
                let _ = w.unwatch(&path);
            }
        }
        self.watch_failed.retain(|k| want.contains_key(k));
        for (k, path) in want {
            if self.watched.contains_key(&k) || self.watch_failed.contains(&k) {
                continue;
            }
            if w.watch(&path, RecursiveMode::NonRecursive).is_ok() {
                self.watched.insert(k, path);
            } else {
                self.watch_failed.insert(k);
            }
        }
    }

    /// Debounced reload of folders that changed outside of exyex: waits until
    /// the burst of events calms down (200 ms), but never longer than 1 s.
    fn apply_fs_events(&mut self, ctx: &Context) {
        let now = Instant::now();
        while let Ok(paths) = self.fs_rx.try_recv() {
            for p in paths {
                self.fs_dirty.entry(path_key(&p)).or_insert(now);
                if let Some(parent) = p.parent() {
                    self.fs_dirty.entry(path_key(parent)).or_insert(now);
                }
            }
            self.fs_last = Some(now);
        }
        if self.fs_dirty.is_empty() {
            return;
        }
        let calm = self.fs_last.is_some_and(|t| t.elapsed() >= Duration::from_millis(200));
        let overdue = self.fs_dirty.values().any(|t| t.elapsed() >= Duration::from_millis(1000));
        if !(calm || overdue) {
            ctx.request_repaint_after(Duration::from_millis(100));
            return;
        }
        let dirty = std::mem::take(&mut self.fs_dirty);
        self.fs_last = None;
        for p in &mut self.panes {
            let t = p.tab_mut();
            if dirty.contains_key(&path_key(&t.path)) {
                t.reload(None);
            }
        }
    }

    /// Recomputes the compare marks (only while compare mode is on).
    fn refresh_diff(&mut self) {
        if !self.compare {
            if !self.diff[0].is_empty() || !self.diff[1].is_empty() {
                self.diff = [HashMap::new(), HashMap::new()];
            }
            return;
        }
        self.diff = [diff_map(self.panes[0].tab(), self.panes[1].tab()), diff_map(self.panes[1].tab(), self.panes[0].tab())];
    }

    /// Selects everything in pane `pi` that is missing / newer / different in the other pane.
    fn select_diff(&mut self, pi: usize) -> usize {
        let map = diff_map(self.panes[pi].tab(), self.panes[1 - pi].tab());
        let t = self.panes[pi].tab_mut();
        t.selected.clear();
        for e in &t.entries {
            if e.is_up || e.deep {
                continue;
            }
            if matches!(map.get(&e.name.to_lowercase()), Some(Diff::OnlyHere | Diff::Newer | Diff::Size)) {
                t.selected.insert(e.path.clone());
            }
        }
        t.selected.len()
    }

    fn tick(&mut self, ctx: &Context) {
        let mut requests = vec![];
        if let Some(s) = &self.ipc {
            while let Ok(r) = s.rx.try_recv() {
                requests.push(r);
            }
        }
        for r in requests {
            self.handle_external_open(ctx, r);
        }
        if let Some((pi, dir, path, t0)) = self.rename_timer.clone() {
            if t0.elapsed() >= Duration::from_millis(450) {
                self.rename_timer = None;
                let tab = self.panes[pi].tab();
                let ok = self.active == pi
                    && tab.path == dir
                    && self.dialog.is_none()
                    && self.palette.is_none()
                    && self.inline.is_none()
                    && tab.current().is_some_and(|e| e.path == path && !e.is_up)
                    && (tab.selected.is_empty() || (tab.selected.len() == 1 && tab.selected.contains(&path)));
                if ok {
                    self.start_inline(pi);
                }
            } else {
                ctx.request_repaint_after(Duration::from_millis(100));
            }
        }
        if self.inline.as_ref().is_some_and(|r| self.panes[r.pi].tab().path != r.dir) {
            self.inline = None; // navigated away while editing
        }
        self.poll_jobs(ctx);
        self.sync_watches();
        self.apply_fs_events(ctx);
        self.refresh_diff();
        let mut loading = false;
        let mut arrived = vec![];
        let mut errors = vec![];
        for p in &mut self.panes {
            for t in &mut p.tabs {
                if t.loading.is_none() {
                    continue;
                }
                let lp = t.poll_load();
                loading |= lp.pending;
                arrived.extend(lp.arrived);
                errors.extend(lp.err);
            }
        }
        for p in arrived {
            self.store.record_visit(&p);
            self.dirty = true;
        }
        for e in errors {
            self.say(e);
        }
        if loading {
            ctx.request_repaint_after(Duration::from_millis(80));
        }
        let mut walking = false;
        for p in &mut self.panes {
            for t in &mut p.tabs {
                walking |= t.poll_deep();
            }
        }
        if walking {
            ctx.request_repaint_after(Duration::from_millis(60));
        }
        // Folders we could not watch (e.g. some network shares): fall back to a slow mtime poll.
        for p in &mut self.panes {
            let t = p.tab_mut();
            if self.watched.contains_key(&path_key(&t.path)) {
                continue;
            }
            if t.last_poll.elapsed() > Duration::from_millis(2000) {
                t.last_poll = Instant::now();
                if dir_mtime(&t.path) != t.mtime {
                    t.reload(None);
                }
            }
        }
        // A tab that just became active may be stale (only active tabs are watched).
        for p in &mut self.panes {
            if p.seen_tabs.0 != usize::MAX && p.seen_tabs.0 != p.active {
                p.tab_mut().reload(None);
            }
        }
        ctx.request_repaint_after(Duration::from_millis(1200));

        if let Some(g) = window_geom(ctx) {
            // keep the last normal (not maximized) size as the one to restore to
            let prev = self.store.window;
            let next = if g.maximized { prev.map(|p| WinGeom { maximized: true, ..p }).unwrap_or(g) } else { g };
            if prev != Some(next) {
                self.store.window = Some(next);
                self.dirty = true;
            }
        }

        let title = format!("exyex — {}", self.tab().path.display());
        if title != self.last_title {
            ctx.send_viewport_cmd(egui::ViewportCommand::Title(title.clone()));
            self.last_title = title;
        }
        if self.dirty {
            // Debounced save. egui only repaints on input, so schedule a wake-up
            // – otherwise the last change would wait for the next mouse move.
            let wait = Duration::from_secs(1);
            if self.last_save.elapsed() >= wait {
                self.save_state();
            } else {
                ctx.request_repaint_after(wait - self.last_save.elapsed());
            }
        }
        // Files dropped from Explorer: copy into the pane under the pointer.
        let dropped: Vec<PathBuf> = ctx.input(|i| i.raw.dropped_files.iter().filter_map(|f| Some(f.path().to_path_buf())).collect());
        if !dropped.is_empty() {
            let pos = ctx.input(|i| i.pointer.latest_pos());
            let pi = pos.and_then(|p| self.panes.iter().position(|pn| pn.rect.contains(p))).unwrap_or(self.active);
            let dest = self.panes[pi].tab().path.clone();
            self.start_op(ctx, OpKind::Copy, dropped, Some(dest));
        }
    }

    // ------------------------------------------------------------ commands

    fn run(&mut self, ctx: &Context, a: Action) {
        {
            let here = crate::archive::in_archive(&self.tab().path);
            let there = crate::archive::in_archive(&self.panes[1 - self.active].tab().path);
            let writes_here = matches!(a, Delete | DeletePermanent | Rename | NewFolder | NewFile | ClipCut | ClipPaste | Move);
            let writes_there = matches!(a, Copy | Move);
            if (here && writes_here) || (there && writes_there) {
                self.say("Archives are read-only (you can copy files out of them)");
                return;
            }
        }
        use Action::*;
        let pi = self.active;
        match a {
            Palette => self.open_palette(false),
            FolderJump => self.open_palette(true),
            SwitchPane => self.active = self.other(),
            SwapPanes => {
                let [a, b] = &mut self.panes;
                std::mem::swap(&mut a.tabs, &mut b.tabs);
                std::mem::swap(&mut a.active, &mut b.active);
            }
            MirrorToOther => {
                let p = self.tab().path.clone();
                let o = self.other();
                self.navigate(o, p);
            }
            NewTab => {
                let p = self.tab().path.clone();
                self.open_new_tab(pi, p);
            }
            CloseTab => {
                let pane = &mut self.panes[pi];
                if pane.tabs.len() > 1 {
                    let t = pane.tabs.remove(pane.active);
                    self.closed_tabs.push((pi, t.path));
                    let pane = &mut self.panes[pi];
                    pane.active = pane.active.min(pane.tabs.len() - 1);
                    self.dirty = true;
                } else {
                    self.say("Can't close the last tab of a pane");
                }
            }
            CloseOtherTabs => {
                let a = self.panes[pi].active;
                self.close_tabs_where(pi, a, |_| true);
            }
            CloseTabsLeft => {
                let a = self.panes[pi].active;
                self.close_tabs_where(pi, a, |i| i < a);
            }
            CloseTabsRight => {
                let a = self.panes[pi].active;
                self.close_tabs_where(pi, a, |i| i > a);
            }
            ReopenTab => {
                if let Some((p, path)) = self.closed_tabs.pop() {
                    self.open_new_tab(p, path);
                }
            }
            NextTab | PrevTab => {
                let pane = &mut self.panes[pi];
                let n = pane.tabs.len();
                pane.active = if a == NextTab { (pane.active + 1) % n } else { (pane.active + n - 1) % n };
                self.dirty = true;
            }
            Up => {
                if let Err(e) = self.tab_mut().up() {
                    self.say(e);
                } else if self.tab().loading.is_none() {
                    let p = self.tab().path.clone();
                    self.store.record_visit(&p);
                    self.dirty = true;
                }
            }
            Back => {
                if let Err(e) = self.tab_mut().go_back() {
                    self.say(e);
                } else if self.tab().loading.is_none() {
                    let p = self.tab().path.clone();
                    self.store.record_visit(&p);
                    self.dirty = true;
                }
            }
            Forward => {
                if let Err(e) = self.tab_mut().go_forward() {
                    self.say(e);
                } else if self.tab().loading.is_none() {
                    let p = self.tab().path.clone();
                    self.store.record_visit(&p);
                    self.dirty = true;
                }
            }
            Open => {
                let Some(e) = self.tab().current().cloned() else { return };
                if e.is_dir {
                    self.navigate(pi, e.path);
                } else if crate::archive::is_archive_name(&e.path) && crate::archive::split_inner(&e.path).is_none() {
                    self.navigate(pi, e.path); // browse the archive like a folder
                } else if let Some((zip, inner)) = crate::archive::split_inner(&e.path) {
                    match crate::archive::extract_temp(&zip, &inner) {
                        Ok(tmp) => {
                            if let Err(err) = open::that_detached(&tmp) {
                                self.say(format!("⚠ Could not open {}: {err}", e.name));
                            }
                        }
                        Err(err) => self.say(format!("⚠ {err}")),
                    }
                } else if let Err(err) = open::that_detached(&e.path) {
                    self.say(format!("⚠ Could not open {}: {err}", e.name));
                }
            }
            ShowInFolder => {
                let Some(e) = self.tab().current().filter(|e| !e.is_up).cloned() else { return };
                let Some(parent) = e.path.parent().map(PathBuf::from) else { return };
                let name = display_name(&e.path);
                match self.panes[pi].tab_mut().go_focus(parent.clone(), Some(name)) {
                    Ok(()) => {
                        self.store.record_visit(&parent);
                        self.dirty = true;
                    }
                    Err(err) => self.say(err),
                }
            }
            QuickLook => self.preview = !self.preview,
            CompareToggle => {
                self.compare = !self.compare;
                self.refresh_diff();
                self.say(if self.compare { "Comparing panes: green = only here, orange = newer here, red = older here, purple = different size" } else { "Compare off" });
            }
            SelectDiff => {
                let n = self.select_diff(pi);
                self.say(if n == 0 { "Nothing differs – this pane has no new or newer items".to_string() } else { format!("{n} item(s) selected") });
            }
            SyncToOther => {
                if self.select_diff(pi) == 0 {
                    self.say("Panes are in sync – nothing to copy");
                } else {
                    self.run(ctx, Copy);
                }
            }
            Root => {
                if let Some(root) = self.tab().path.ancestors().last().map(PathBuf::from) {
                    if root != self.tab().path {
                        self.navigate(pi, root);
                    }
                }
            }
            OpenInNewTab => {
                if let Some(e) = self.tab().current().cloned() {
                    let target = if e.is_dir { e.path } else { self.tab().path.clone() };
                    self.open_new_tab(pi, target);
                }
            }
            Refresh => {
                self.drives = list_drives();
                self.reload_all();
            }
            EditPath => {
                let text = self.tab().path.to_string_lossy().into_owned();
                self.panes[pi].path_edit = Some(PathEdit { orig: text.clone(), text, fresh: true, sugg_for: String::from("\u{1}"), sugg: vec![], sel: None, popup_rect: Rect::NOTHING });
            }
            Filter => {
                self.tab_mut().filter_open = true;
                self.panes[pi].focus_filter = true;
            }
            Copy | Move => {
                let paths = self.tab().targets();
                let dest = self.panes[self.other()].tab().path.clone();
                if !paths.is_empty() {
                    self.dialog = Some(Dialog::Transfer { kind: if a == Copy { OpKind::Copy } else { OpKind::Move }, paths, dest });
                }
            }
            NewFolder => self.dialog = Some(Dialog::NewFolder { text: "New folder".into(), fresh: true }),
            NewFile => self.dialog = Some(Dialog::NewFile { text: "New file.txt".into(), fresh: true }),
            Delete | DeletePermanent => {
                let paths = self.tab().targets();
                if !paths.is_empty() {
                    self.dialog = Some(Dialog::Delete { paths, permanent: a == DeletePermanent });
                }
            }
            Rename => self.start_inline(pi),
            ClipCopy | ClipCut => {
                let paths = self.tab().targets();
                if !paths.is_empty() {
                    let n = paths.len();
                    self.clipboard = Some((paths, a == ClipCut));
                    self.say(format!("{} item(s) {} — paste with Ctrl+V", n, if a == ClipCut { "cut" } else { "copied" }));
                }
            }
            ClipPaste => {
                if let Some((paths, cut)) = self.clipboard.clone() {
                    let dest = self.tab().path.clone();
                    self.start_op(ctx, if cut { OpKind::Move } else { OpKind::Copy }, paths, Some(dest));
                    if cut {
                        self.clipboard = None;
                    }
                } else {
                    self.say("Clipboard is empty");
                }
            }
            CopyFolderPath => {
                let text = self.tab().path.display().to_string();
                ctx.copy_text(text);
                self.say("Folder path copied to clipboard");
            }
            CopyPath => {
                let t = self.tab().targets();
                let text = t.iter().map(|p| p.display().to_string()).collect::<Vec<_>>().join("\n");
                ctx.copy_text(text);
                self.say("Path copied to clipboard");
            }
            SelectAll => self.tab_mut().select_all(),
            SelectNone => self.tab_mut().selected.clear(),
            InvertSelection => self.tab_mut().invert(),
            ToggleSelect => {
                let t = self.tab_mut();
                let c = t.cursor;
                t.toggle_at(c);
                t.move_cursor(1, false);
            }
            ToggleHidden => {
                self.store.show_hidden = !self.store.show_hidden;
                let sh = self.store.show_hidden;
                for p in &mut self.panes {
                    for t in &mut p.tabs {
                        t.show_hidden = sh;
                        t.rebuild_view(None);
                    }
                }
                self.dirty = true;
            }
            SortName => self.tab_mut().set_sort(SortKey::Name),
            SortExt => self.tab_mut().set_sort(SortKey::Ext),
            SortSize => self.tab_mut().set_sort(SortKey::Size),
            SortDate => self.tab_mut().set_sort(SortKey::Modified),
            ToggleFavorite => {
                let p = self.tab().path.clone();
                let on = self.store.toggle_favorite(&p);
                self.dirty = true;
                self.say(if on { "Added to favorites ★" } else { "Removed from favorites" });
            }
            Reveal => {
                let target = self.tab().current().map(|e| e.path.clone()).unwrap_or_else(|| self.tab().path.clone());
                #[cfg(windows)]
                {
                    use std::os::windows::process::CommandExt;
                    let _ = std::process::Command::new("explorer.exe")
                        .raw_arg(format!("/select,\"{}\"", target.display()))
                        .spawn();
                }
                #[cfg(not(windows))]
                let _ = open::that_detached(target.parent().unwrap_or(&target));
            }
            ExplorerHere => {
                let dir = self.tab().path.clone();
                if let Err(e) = std::process::Command::new("explorer.exe").arg(&dir).spawn() {
                    self.say(format!("⚠ Could not start Explorer: {e}"));
                }
            }
            PowerShellHere | CmdHere => {
                let dir = self.tab().path.clone();
                let exe = if matches!(a, PowerShellHere) { "powershell.exe" } else { "cmd.exe" };
                let r = std::process::Command::new("cmd.exe").args(["/c", "start", "", exe]).current_dir(&dir).spawn();
                if let Err(e) = r {
                    self.say(format!("⚠ Could not start {exe}: {e}"));
                }
            }
            Terminal => {
                let dir = self.tab().path.clone();
                let r = std::process::Command::new("wt.exe")
                    .arg("-d")
                    .arg(&dir)
                    .spawn()
                    .or_else(|_| std::process::Command::new("cmd.exe").args(["/c", "start", "cmd.exe"]).current_dir(&dir).spawn());
                if let Err(e) = r {
                    self.say(format!("⚠ Could not start terminal: {e}"));
                }
            }
            ThemeCycle | ThemeSystem | ThemeLight | ThemeDark => {
                self.store.theme = match a {
                    ThemeSystem => ThemeChoice::System,
                    ThemeLight => ThemeChoice::Light,
                    ThemeDark => ThemeChoice::Dark,
                    _ => self.store.theme.next(),
                };
                apply_theme(ctx, self.store.theme);
                self.dirty = true;
                self.say(format!("Theme: {}", self.store.theme.label()));
            }
            Quit => self.quit = true,
        }
    }

    /// Closes every tab of pane `pi` for which `close(index)` is true, except `anchor`
    /// (which is kept and becomes the active tab if the active one was closed).
    fn close_tabs_where(&mut self, pi: usize, anchor: usize, close: impl Fn(usize) -> bool) {
        let pane = &mut self.panes[pi];
        if anchor >= pane.tabs.len() {
            return;
        }
        let old_active = pane.active;
        let (mut kept, mut closed) = (vec![], vec![]);
        let (mut new_active, mut anchor_new) = (None, 0);
        for (i, t) in std::mem::take(&mut pane.tabs).into_iter().enumerate() {
            if i != anchor && close(i) {
                closed.push(t.path);
                continue;
            }
            if i == old_active {
                new_active = Some(kept.len());
            }
            if i == anchor {
                anchor_new = kept.len();
            }
            kept.push(t);
        }
        pane.tabs = kept;
        pane.active = new_active.unwrap_or(anchor_new);
        let n = closed.len();
        for path in closed.into_iter().rev() {
            self.closed_tabs.push((pi, path));
        }
        self.dirty = true;
        if n > 0 {
            self.say(format!("Closed {n} tab(s) — Ctrl+Shift+T reopens the last one"));
        }
    }

    fn start_inline(&mut self, pi: usize) {
        let tab = self.panes[pi].tab();
        let Some(e) = tab.current().filter(|e| !e.is_up && !e.deep && !crate::archive::in_archive(&e.path)) else { return };
        self.inline = Some(InlineRename {
            pi,
            dir: tab.path.clone(),
            path: e.path.clone(),
            text: e.name.clone(),
            fresh: true,
            seen_focus: false,
            commit: false,
            cancel: false,
        });
        self.rename_timer = None;
    }

    fn finish_inline(&mut self) {
        let Some(ir) = &self.inline else { return };
        if ir.cancel {
            self.inline = None;
        } else if ir.commit {
            let ir = self.inline.take().unwrap();
            let name = ir.text.trim().to_string();
            let unchanged = ir.path.file_name().is_some_and(|n| n.to_string_lossy() == name);
            if !name.is_empty() && !unchanged {
                self.do_rename(&ir.path, &name);
            }
        }
    }

    fn do_rename(&mut self, path: &Path, new_name: &str) {
        match validate_name(new_name) {
            Err(e) => return self.say(format!("⚠ {e}")),
            Ok(()) => {}
        }
        let target = path.with_file_name(new_name);
        if target != path && target.exists() && !target.to_string_lossy().eq_ignore_ascii_case(&path.to_string_lossy()) {
            return self.say(format!("⚠ \"{new_name}\" already exists"));
        }
        match std::fs::rename(path, &target) {
            Ok(()) => {
                self.say(format!("Renamed to {new_name}"));
                for p in &mut self.panes {
                    p.tab_mut().selected.clear();
                    p.tab_mut().reload(None);
                }
                self.tab_mut().reload(Some(new_name.to_string()));
            }
            Err(e) => self.say(format!("⚠ Rename failed: {e}")),
        }
    }

    fn do_create(&mut self, name: &str, folder: bool) {
        if let Err(e) = validate_name(name) {
            return self.say(format!("⚠ {e}"));
        }
        let target = self.tab().path.join(name);
        let r = if folder { std::fs::create_dir(&target) } else { std::fs::File::create_new(&target).map(|_| ()) };
        match r {
            Ok(()) => {
                self.say(format!("Created {name}"));
                self.reload_all();
                self.tab_mut().reload(Some(name.to_string()));
            }
            Err(e) => self.say(format!("⚠ {e}")),
        }
    }

    // ---------------------------------------------------------- keyboard

    fn handle_keys(&mut self, ctx: &Context) {
        if self.palette.is_some() || self.dialog.is_some() || ctx.egui_wants_keyboard_input() {
            return;
        }
        let pi = self.active;
        // Cut / Copy / Paste arrive as events, not key presses. On Windows
        // Shift+Delete is also reported as "Cut" -> permanent delete.
        let (copy, cut, paste, shift, back, fwd) = ctx.input(|i| {
            use egui::Event::*;
            (
                i.events.iter().any(|e| matches!(e, Copy)),
                i.events.iter().any(|e| matches!(e, Cut)),
                i.events.iter().any(|e| matches!(e, Paste(_))),
                i.modifiers.shift,
                i.pointer.button_pressed(egui::PointerButton::Extra1),
                i.pointer.button_pressed(egui::PointerButton::Extra2),
            )
        });
        if copy {
            return self.run(ctx, Action::ClipCopy);
        }
        if cut {
            return self.run(ctx, if shift { Action::DeletePermanent } else { Action::ClipCut });
        }
        if paste {
            return self.run(ctx, Action::ClipPaste);
        }
        if back {
            return self.run(ctx, Action::Back);
        }
        if fwd {
            return self.run(ctx, Action::Forward);
        }

        let hit = self.bindings.iter().find_map(|b| ctx.input_mut(|i| i.consume_key(b.mods, b.key)).then_some(b.action));
        if let Some(a) = hit {
            return self.run(ctx, a);
        }

        // Ctrl+1..8 jump to that tab, Ctrl+9 to the last one (like a browser).
        let digits = [Key::Num1, Key::Num2, Key::Num3, Key::Num4, Key::Num5, Key::Num6, Key::Num7, Key::Num8, Key::Num9];
        for (n, k) in digits.iter().enumerate() {
            if ctx.input_mut(|i| i.consume_key(Modifiers::CTRL, *k)) {
                let pane = &mut self.panes[pi];
                let idx = if n == 8 { pane.tabs.len() - 1 } else { n };
                if idx < pane.tabs.len() {
                    pane.active = idx;
                    self.dirty = true;
                }
                return;
            }
        }

        // Cursor movement
        let page = ((self.tab().view_h / ROW_H) as isize - 1).max(1);
        let moves: [(Key, isize); 6] = [
            (Key::ArrowUp, -1),
            (Key::ArrowDown, 1),
            (Key::PageUp, -page),
            (Key::PageDown, page),
            (Key::Home, isize::MIN / 2),
            (Key::End, isize::MAX / 2),
        ];
        for (k, d) in moves {
            for (m, extend) in [(Modifiers::SHIFT, true), (Modifiers::NONE, false)] {
                if ctx.input_mut(|i| i.consume_key(m, k)) {
                    self.tab_mut().move_cursor(d, extend);
                    return;
                }
            }
        }
        if ctx.input_mut(|i| i.consume_key(Modifiers::NONE, Key::Escape)) {
            if self.jobs.iter().any(|j| j.state == JobState::Running) {
                for j in self.jobs.iter_mut().filter(|j| j.state == JobState::Running) {
                    j.cancel.store(true, Ordering::Relaxed);
                    j.cancelling = true;
                }
                self.say("Cancelling…");
            } else if !self.tab().filter.is_empty() || self.tab().filter_open {
                let t = self.tab_mut();
                t.filter.clear();
                t.filter_open = false;
                t.rebuild_view(None);
            } else {
                self.tab_mut().selected.clear();
            }
            return;
        }

        // Type-ahead: just start typing to jump to a file.
        let typed: String = ctx.input(|i| {
            if i.modifiers.ctrl || i.modifiers.alt || i.modifiers.command {
                return String::new();
            }
            i.events
                .iter()
                .filter_map(|e| if let egui::Event::Text(t) = e { Some(t.as_str()) } else { None })
                .collect()
        });
        let typed: String = typed.chars().filter(|c| !c.is_control()).collect();
        if !typed.trim().is_empty() || (!typed.is_empty() && !self.typeahead.0.is_empty()) {
            if self.typeahead.1.elapsed() > Duration::from_millis(900) {
                self.typeahead.0.clear();
            }
            self.typeahead.0.push_str(&typed);
            self.typeahead.1 = Instant::now();
            let q = self.typeahead.0.clone();
            self.tab_mut().jump_to_prefix(&q);
            self.say(format!("Jump: {q}"));
        }
        let _ = pi;
    }

    // ----------------------------------------------------------- palette

    fn open_palette(&mut self, folders_only: bool) {
        let mut seen: HashSet<String> = HashSet::new();
        let mut folders: Vec<(PathBuf, &'static str, i32)> = vec![];
        let mut add = |p: PathBuf, tag: &'static str, bonus: i32, folders: &mut Vec<_>| {
            if seen.insert(p.to_string_lossy().to_lowercase()) {
                folders.push((p, tag, bonus));
            }
        };
        for f in &self.store.favorites {
            add(PathBuf::from(f), "★ favorite", 2000, &mut folders);
        }
        for p in self.panes.iter().flat_map(|p| p.tabs.iter().map(|t| t.path.clone())) {
            add(p, "open tab", 300, &mut folders);
        }
        for (label, p) in [
            ("home", dirs::home_dir()),
            ("desktop", dirs::desktop_dir()),
            ("downloads", dirs::download_dir()),
            ("documents", dirs::document_dir()),
            ("pictures", dirs::picture_dir()),
        ] {
            if let Some(p) = p {
                add(p, label, 100, &mut folders);
            }
        }
        for d in &self.drives {
            add(d.clone(), "drive", 100, &mut folders);
        }
        for (p, score) in self.store.frecent().into_iter().take(150) {
            if p.is_dir() {
                add(p, "frequent", (score as i32).min(250), &mut folders);
            }
        }
        self.palette = Some(Palette { query: String::new(), folders_only, sel: 0, fresh: true, folders });
    }

    fn palette_items(&self, pal: &Palette) -> Vec<Item> {
        let q = pal.query.trim();
        let mut scored: Vec<(i32, Item)> = vec![];
        if q.is_empty() {
            if !pal.folders_only {
                return PALETTE_ACTIONS.iter().map(|&a| Item::Action(a)).collect();
            }
            return pal.folders.iter().map(|(p, t, _)| Item::Folder(p.clone(), t)).collect();
        }
        if let Some(p) = parse_user_path(q, &self.tab().path) {
            if (q.contains('\\') || q.contains('/') || q.contains(':') || q.starts_with('~') || q.starts_with('%')) && p.exists() {
                scored.push((100_000, Item::Goto(p)));
            }
        }
        if !pal.folders_only {
            for &a in PALETTE_ACTIONS {
                if let Some(s) = actions::fuzzy(q, actions::name(a)) {
                    scored.push((s + 20, Item::Action(a)));
                }
            }
        }
        for (p, tag, bonus) in &pal.folders {
            let full = p.to_string_lossy();
            let best = actions::fuzzy(q, &full)
                .into_iter()
                .chain(actions::fuzzy(q, &display_name(p)).map(|s| s + 20))
                .max();
            if let Some(s) = best {
                scored.push((s + bonus / 4, Item::Folder(p.clone(), tag)));
            }
        }
        scored.sort_by(|a, b| b.0.cmp(&a.0));
        scored.into_iter().map(|(_, i)| i).collect()
    }

    fn exec_item(&mut self, ctx: &Context, item: &Item, mode: OpenMode) {
        match item {
            Item::Action(a) => self.run(ctx, *a),
            Item::Folder(p, _) | Item::Goto(p) => {
                let p = p.clone();
                if !p.is_dir() {
                    // Goto of a file: open its folder and focus the file.
                    if let (Some(parent), Some(name)) = (p.parent(), p.file_name()) {
                        let pi = self.active;
                        if self.navigate(pi, parent.to_path_buf()) {
                            self.tab_mut().reload(Some(name.to_string_lossy().into_owned()));
                        }
                    }
                    return;
                }
                match mode {
                    OpenMode::Here => {
                        let pi = self.active;
                        self.navigate(pi, p);
                    }
                    OpenMode::NewTab => {
                        let pi = self.active;
                        self.open_new_tab(pi, p);
                    }
                    OpenMode::OtherPane => {
                        let o = self.other();
                        self.navigate(o, p);
                        self.active = o;
                    }
                }
            }
        }
    }

    fn ui_palette(&mut self, ctx: &Context) {
        let Some(mut pal) = self.palette.take() else { return };
        let items = self.palette_items(&pal);
        let last = items.len().saturating_sub(1).min(11);
        pal.sel = pal.sel.min(last);

        let (esc, up, down, s_enter, a_enter, enter) = ctx.input_mut(|i| {
            (
                i.consume_key(Modifiers::NONE, Key::Escape),
                i.consume_key(Modifiers::NONE, Key::ArrowUp),
                i.consume_key(Modifiers::NONE, Key::ArrowDown),
                i.consume_key(Modifiers::SHIFT, Key::Enter),
                i.consume_key(Modifiers::ALT, Key::Enter),
                i.consume_key(Modifiers::NONE, Key::Enter),
            )
        });
        if down {
            pal.sel = (pal.sel + 1).min(last);
        }
        if up {
            pal.sel = pal.sel.saturating_sub(1);
        }
        let mut chosen: Option<(usize, OpenMode)> = None;
        if enter {
            chosen = Some((pal.sel, OpenMode::Here));
        } else if s_enter {
            chosen = Some((pal.sel, OpenMode::NewTab));
        } else if a_enter {
            chosen = Some((pal.sel, OpenMode::OtherPane));
        }
        let mut close = esc;

        egui::Window::new("palette")
            .title_bar(false)
            .resizable(false)
            .anchor(Align2::CENTER_TOP, [0.0, 70.0])
            .fixed_size([640.0, 0.0])
            .show(ctx, |ui| {
                let hint = if pal.folders_only {
                    "Jump to folder…  (Enter: here · Shift+Enter: new tab · Alt+Enter: other pane)"
                } else {
                    "Type a command, folder, or paste a path…"
                };
                let r = ui.add(
                    egui::TextEdit::singleline(&mut pal.query)
                        .hint_text(hint)
                        .desired_width(f32::INFINITY)
                        .font(egui::TextStyle::Heading),
                );
                if pal.fresh {
                    r.request_focus();
                    pal.fresh = false;
                }
                if r.changed() {
                    pal.sel = 0;
                }
                ui.add_space(4.0);
                if items.is_empty() {
                    ui.weak("No matches");
                }
                for (i, item) in items.iter().take(12).enumerate() {
                    let (rect, resp) = ui.allocate_exact_size(vec2(ui.available_width(), 24.0), Sense::click());
                    if i == pal.sel {
                        ui.painter().rect_filled(rect, 4.0, ACCENT.gamma_multiply(0.45));
                    } else if resp.hovered() {
                        ui.painter().rect_filled(rect, 4.0, tint(ui.visuals().dark_mode, 14));
                    }
                    let (left, right) = match item {
                        Item::Action(a) => (actions::name(*a).to_string(), actions::shortcut_label(*a, &self.bindings)),
                        Item::Folder(p, tag) => (p.display().to_string(), tag.to_string()),
                        Item::Goto(p) => (format!("Go to {}", p.display()), "path".to_string()),
                    };
                    let fid = FontId::proportional(14.5);
                    let tc = ui.visuals().text_color();
                    ui.painter().with_clip_rect(rect.shrink2(vec2(0.0, 0.0))).text(
                        rect.left_center() + vec2(8.0, 0.0),
                        Align2::LEFT_CENTER,
                        left,
                        fid.clone(),
                        tc,
                    );
                    ui.painter().text(rect.right_center() - vec2(8.0, 0.0), Align2::RIGHT_CENTER, right, FontId::proportional(12.5), ui.visuals().weak_text_color());
                    if resp.clicked() {
                        chosen = Some((i, OpenMode::Here));
                    }
                }
            });

        if ctx.input(|i| i.pointer.any_pressed()) {
            // click outside closes
            if let Some(pos) = ctx.input(|i| i.pointer.interact_pos()) {
                if ctx.layer_id_at(pos).is_none_or(|l| l.order == egui::Order::Background) {
                    close = true;
                }
            }
        }
        if let Some((i, mode)) = chosen {
            if let Some(item) = items.get(i) {
                self.exec_item(ctx, item, mode);
            }
            close = true;
        }
        if !close {
            self.palette = Some(pal);
        }
    }

    // ------------------------------------------------------------ dialogs

    fn ui_dialog(&mut self, ctx: &Context) {
        let Some(mut dlg) = self.dialog.take() else { return };
        // In the confirm dialogs (delete, copy, move) Enter is left to the focused button (OK by default), so
        // Tab -> Cancel -> Enter really cancels.
        let is_confirm = matches!(dlg, Dialog::Delete { .. } | Dialog::Transfer { .. });
        let (esc, enter) = ctx.input_mut(|i| (i.consume_key(Modifiers::NONE, Key::Escape), !is_confirm && i.consume_key(Modifiers::NONE, Key::Enter)));
        let mut ok = enter;
        let mut cancel = esc;

        let title = match &dlg {
            Dialog::NewFolder { .. } => "New folder",
            Dialog::NewFile { .. } => "New file",
            Dialog::Delete { permanent: true, .. } => "Delete permanently",
            Dialog::Delete { .. } => "Delete",
            Dialog::Transfer { kind: OpKind::Move, .. } => "Move",
            Dialog::Transfer { .. } => "Copy",
        };
        egui::Window::new(title)
            .collapsible(false)
            .resizable(false)
            .anchor(Align2::CENTER_CENTER, [0.0, -40.0])
            .show(ctx, |ui| {
                ui.set_min_width(420.0);
                match &mut dlg {
                    Dialog::NewFolder { text, fresh } => text_field(ui, text, fresh, None),
                    Dialog::NewFile { text, fresh } => {
                        let n = stem_len(text);
                        text_field(ui, text, fresh, Some(n))
                    }
                    Dialog::Transfer { kind, paths, dest } => {
                        let verb = if *kind == OpKind::Move { "Move" } else { "Copy" };
                        ui.label(if paths.len() == 1 {
                            format!("{verb} \"{}\" to:", display_name(&paths[0]))
                        } else {
                            format!("{verb} {} items to:", paths.len())
                        });
                        ui.label(egui::RichText::new(dest.display().to_string()).strong());
                        if paths.len() > 1 {
                            ui.add_space(2.0);
                            for p in paths.iter().take(6) {
                                ui.weak(display_name(p));
                            }
                            if paths.len() > 6 {
                                ui.weak(format!("… and {} more", paths.len() - 6));
                            }
                        }
                    }
                    Dialog::Delete { paths, permanent } => {
                        if *permanent {
                            ui.colored_label(ui.visuals().error_fg_color, "This cannot be undone.");
                        }
                        ui.label(if paths.len() == 1 {
                            format!("{} \"{}\"?", if *permanent { "Permanently delete" } else { "Move to Recycle Bin:" }, display_name(&paths[0]))
                        } else {
                            format!("{} {} items?", if *permanent { "Permanently delete" } else { "Move to Recycle Bin:" }, paths.len())
                        });
                        for p in paths.iter().take(6) {
                            ui.weak(display_name(p));
                        }
                        if paths.len() > 6 {
                            ui.weak(format!("… and {} more", paths.len() - 6));
                        }
                    }
                }
                ui.add_space(6.0);
                ui.horizontal(|ui| {
                    let mut ok_btn = egui::Button::new("  OK  ");
                    if is_confirm {
                        ok_btn = ok_btn.fill(ACCENT.gamma_multiply(0.75));
                    }
                    let ok_resp = ui.add(ok_btn);
                    if is_confirm && ctx.memory(|m| m.focused().is_none()) {
                        ok_resp.request_focus(); // OK is the default button
                    }
                    if ok_resp.clicked() {
                        ok = true;
                    }
                    if ui.button("Cancel").clicked() {
                        cancel = true;
                    }
                });
            });

        if cancel {
            return;
        }
        if ok {
            match dlg {
                Dialog::NewFolder { text, .. } => self.do_create(text.trim(), true),
                Dialog::NewFile { text, .. } => self.do_create(text.trim(), false),
                Dialog::Transfer { kind, paths, dest } => {
                    let ctx = ctx.clone();
                    self.start_op(&ctx, kind, paths, Some(dest));
                }
                Dialog::Delete { paths, permanent } => {
                    let ctx = ctx.clone();
                    self.start_op(&ctx, if permanent { OpKind::Delete } else { OpKind::Trash }, paths, None);
                }
            }
            return;
        }
        self.dialog = Some(dlg);
    }

    // -------------------------------------------------------------- panes

    fn ui_status(&mut self, ui: &mut Ui, evs: &mut Vec<Ev>) {
        let theme_label = format!("Theme: {}", self.store.theme.label());
        let theme_label = theme_label.as_str();
        let running: Option<(f32, String)> = {
            let r: Vec<&Job> = self.jobs.iter().filter(|j| j.state == JobState::Running).collect();
            r.last().map(|j| {
                let frac = if j.total > 0 { (j.done as f32 / j.total as f32).clamp(0.0, 1.0) } else { 0.0 };
                let more = if r.len() > 1 { format!("  (+{} more)", r.len() - 1) } else { String::new() };
                (frac, format!("{}{}", j.title, more))
            })
        };
        let mut toggle_jobs = false;
        ui.horizontal(|ui| {
            if let Some((frac, text)) = &running {
                ui.add(egui::ProgressBar::new(*frac).desired_width(240.0).text(text.as_str()));
                if chip(ui, "Details", false, None).on_hover_text("Show / hide the operations window").clicked() {
                    toggle_jobs = true;
                }
            } else {
                ui.label(&self.status);
            }
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                for (label, a) in [
                    ("Ctrl+P Palette", Action::Palette),
                    (theme_label, Action::ThemeCycle),
                    ("F8 Delete", Action::Delete),
                    ("F7 New folder", Action::NewFolder),
                    ("F6 Move", Action::Move),
                    ("F5 Copy", Action::Copy),
                    ("F2 Rename", Action::Rename),
                ] {
                    if chip(ui, label, false, None).clicked() {
                        evs.push(Ev::Run(a));
                    }
                }
            });
        });
        if toggle_jobs {
            self.jobs_open = !self.jobs_open;
        }
    }

    /// Operations list in its own native window: it has its own title bar, can be
    /// moved to another monitor, resized, minimized and maximized, and never blocks
    /// the main window.
    fn ui_jobs(&mut self, ctx: &Context) {
        if self.jobs.is_empty() || !self.jobs_open {
            return;
        }
        let mut size = vec2(440.0, 300.0);
        let saved = self.store.jobs_win.filter(|w| w.on_screen());
        if let Some(w) = saved {
            size = vec2(w.w, w.h);
        }
        let mut builder = egui::ViewportBuilder::default()
            .with_title("exyex — Operations")
            .with_inner_size(size)
            .with_min_inner_size([340.0, 150.0])
            .with_icon(egui::IconData { rgba: crate::icon_draw::render_icon(64), width: 64, height: 64 })
            .with_active(false); // do not steal focus from the file manager
        // First placement: bottom-right corner of the main window (only used when the window is created).
        if let Some(w) = saved {
            builder = builder.with_position(pos2(w.x, w.y));
        } else if let Some(r) = ctx.input(|i| i.viewport().outer_rect) {
            builder = builder.with_position(pos2(r.right() - size.x - 24.0, r.bottom() - size.y - 70.0));
        }
        let mut geom: Option<WinGeom> = None;
        let mut close = false;
        let mut dismiss: Vec<u64> = vec![];
        let jobs = &mut self.jobs;
        ctx.show_viewport_immediate(egui::ViewportId::from_hash_of("exyex-operations"), builder, |ui, class| {
            if ui.ctx().input(|i| i.viewport().close_requested()) {
                close = true;
                return;
            }
            geom = window_geom(ui.ctx());
            let mut body = |ui: &mut Ui| {
                let running = jobs.iter().filter(|j| j.state == JobState::Running).count();
                ui.label(egui::RichText::new(format!("{} operation(s), {} running", jobs.len(), running)).weak());
                ui.separator();
                egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| {
                    for (i, j) in jobs.iter_mut().enumerate() {
                        if i > 0 {
                            ui.separator();
                        }
                        job_row(ui, j, &mut dismiss);
                    }
                });
            };
            if class == egui::ViewportClass::EmbeddedWindow {
                // Backend without multi-window support: fall back to an in-app window.
                egui::Window::new("Operations").show(ui.ctx(), |ui| body(ui));
            } else {
                egui::CentralPanel::default().show(ui, |ui| body(ui));
            }
        });
        if let Some(g) = geom.filter(|g| Some(*g) != self.store.jobs_win && !g.maximized) {
            self.store.jobs_win = Some(g);
            self.dirty = true;
        }
        if close {
            self.jobs_open = false;
        }
        if !dismiss.is_empty() {
            self.jobs.retain(|j| !dismiss.contains(&j.id));
        }
    }

    fn ui_pane(&mut self, ui: &mut Ui, pi: usize, evs: &mut Vec<Ev>) {
        let ctx = ui.ctx().clone();
        let App { panes, icons, store, clipboard, drag, active, drives, inline, diff, .. } = self;
        let diff = &diff[pi];
        let is_active = *active == pi;
        let dark = ui.visuals().dark_mode;
        let dragging = drag.is_some();
        let drag_paths: Vec<PathBuf> = drag.as_ref().map(|d| d.paths.clone()).unwrap_or_default();
        let cut: Option<&Vec<PathBuf>> = clipboard.as_ref().filter(|c| c.1).map(|c| &c.0);
        let Pane { tabs, active: ai, path_edit, focus_filter, seen_tabs, .. } = &mut panes[pi];

        ui.spacing_mut().item_spacing = vec2(4.0, 3.0);

        // --- tab strip: scrolls sideways when there are many tabs; the list button
        // on the right jumps to any tab.
        let reveal = *seen_tabs != (*ai, tabs.len());
        *seen_tabs = (*ai, tabs.len());
        ui.horizontal(|ui| {
            let n = tabs.len();
            let strip_w = (ui.available_width() - 66.0).max(60.0);
            let strip_rect = Rect::from_min_size(ui.cursor().min, vec2(strip_w, 28.0));
            let over_strip = ui.rect_contains_pointer(strip_rect);
            egui::ScrollArea::horizontal()
                .id_salt(("tabstrip", pi))
                .max_width(strip_w)
                .scroll_bar_visibility(egui::scroll_area::ScrollBarVisibility::AlwaysHidden)
                .show(ui, |ui| {
                    if over_strip {
                        // the mouse wheel scrolls the strip sideways
                        let d = ui.input(|i| i.smooth_scroll_delta);
                        if d.y != 0.0 {
                            ui.scroll_with_delta(vec2(d.y, 0.0));
                        }
                    }
                    ui.horizontal(|ui| {
                        let mut rects: Vec<Rect> = Vec::with_capacity(n);
                        for (i, t) in tabs.iter().enumerate() {
                            let sel = i == *ai;
                            let mut label = t.title();
                            if label.chars().count() > 20 {
                                label = format!("{}…", label.chars().take(19).collect::<String>());
                            }
                            let (r, close) = tab_pill(ui, &label, sel, n > 1);
                            if sel && reveal {
                                r.scroll_to_me(Some(Align::Center));
                            }
                            rects.push(r.rect);
                            let r = r.on_hover_text(t.path.display().to_string());
                            if r.drag_started() {
                                evs.push(Ev::TabDrag(pi, i));
                            }
                            if close || (r.middle_clicked() && n > 1) {
                                evs.push(Ev::CloseTab(pi, i));
                            } else if r.clicked() {
                                evs.push(Ev::Tab(pi, i));
                            }
                            r.context_menu(|ui| {
                                tab_menu_item(ui, evs, pi, i, "Close", n > 1, TabCmd::Close);
                                tab_menu_item(ui, evs, pi, i, "Close other tabs", n > 1, TabCmd::Others);
                                tab_menu_item(ui, evs, pi, i, "Close tabs to the left", i > 0, TabCmd::Left);
                                tab_menu_item(ui, evs, pi, i, "Close tabs to the right", i + 1 < n, TabCmd::Right);
                                ui.separator();
                                tab_menu_item(ui, evs, pi, i, "Duplicate tab", true, TabCmd::Duplicate);
                                tab_menu_item(ui, evs, pi, i, "Open in other pane", true, TabCmd::OpenOther);
                                tab_menu_item(ui, evs, pi, i, "Move to other pane", n > 1, TabCmd::MoveOther);
                                ui.separator();
                                tab_menu_item(ui, evs, pi, i, "Copy path", true, TabCmd::CopyPath);
                            });
                        }
                        evs.push(Ev::TabRects(pi, rects));
                    });
                });
            if icon_btn(ui, Ico::Plus, true, "New tab (Ctrl+T)").clicked() {
                evs.push(Ev::NewTab(pi));
            }
            let all = icon_btn(ui, Ico::Down, true, "All tabs");
            egui::Popup::menu(&all).show(|ui| {
                ui.set_min_width(240.0);
                egui::ScrollArea::vertical().max_height(320.0).show(ui, |ui| {
                    for (i, t) in tabs.iter().enumerate() {
                        let label = format!("{}   {}", t.title(), t.path.display());
                        if ui.selectable_label(i == *ai, label).clicked() {
                            evs.push(Ev::Tab(pi, i));
                            ui.close();
                        }
                    }
                });
            });
        });

        let tab = &mut tabs[*ai];

        // --- drives + favorite
        ui.horizontal(|ui| {
            for d in drives.iter() {
                let cur = tab.path.starts_with(d);
                let label = d.to_string_lossy().trim_end_matches('\\').to_string();
                if chip(ui, &label, cur, None).clicked() {
                    evs.push(Ev::Go(pi, d.clone()));
                }
            }
            let fav = store.is_favorite(&tab.path);
            let (glyph, color) = if fav { ("★", Some(Color32::from_rgb(240, 180, 0))) } else { ("☆", None) };
            if chip(ui, glyph, false, color).on_hover_text("Favorite this folder (Ctrl+B) · jump with Ctrl+D").clicked() {
                evs.push(Ev::Go(pi, tab.path.clone()));
                evs.push(Ev::Run(Action::ToggleFavorite));
            }
        });

        // --- nav buttons + path bar
        ui.horizontal(|ui| {
            ui.spacing_mut().item_spacing.x = 1.0;
            if icon_btn(ui, Ico::Back, !tab.back.is_empty(), "Back (Alt+←)").clicked() {
                evs.push(Ev::Run(Action::Back));
            }
            if icon_btn(ui, Ico::Fwd, !tab.fwd.is_empty(), "Forward (Alt+→)").clicked() {
                evs.push(Ev::Run(Action::Forward));
            }
            if icon_btn(ui, Ico::Up, tab.path.parent().is_some(), "Up (Backspace)").clicked() {
                evs.push(Ev::Run(Action::Up));
            }
            if icon_btn(ui, Ico::Root, tab.path.parent().is_some(), "Root of drive (Ctrl+Backspace)").clicked() {
                evs.push(Ev::Run(Action::Root));
            }
            ui.add_space(4.0);
            egui::Frame::new()
                .fill(ui.visuals().extreme_bg_color)
                .stroke(Stroke::new(1.0, line_color(dark)))
                .corner_radius(7)
                .inner_margin(egui::Margin::symmetric(6, 1))
                .show(ui, |ui| {
                    ui.set_min_width(ui.available_width());
                    ui.set_min_height(24.0);
                    if let Some(pe) = path_edit.as_mut() {
                        path_edit_ui(ui, pe, &tab.path, pi, evs, store);
                    } else {
                        breadcrumbs(ui, &tab.path, pi, evs);
                    }
                });
        });
        // Path edit closes itself by emitting Go/GoTyped or on Esc/blur.
        if let Some(pe) = path_edit.as_ref() {
            if pe.text == "\u{0}close" {
                *path_edit = None;
            }
        }

        // --- search bar (Ctrl+F): live, with Smart / Fuzzy / Wildcard / Regex modes
        if tab.filter_open {
            let opts = tab.search;
            let err = tab.matcher.error().map(str::to_string);
            let edit_id = egui::Id::new(("search-edit", pi));
            // Keys are consumed *before* the text field sees them, and only while it has focus.
            let focused = *focus_filter || ctx.memory(|m| m.has_focus(edit_id));
            let (mut esc, mut enter, mut up, mut down, mut pgup, mut pgdn, mut tab_key) = (false, false, false, false, false, false, false);
            let mut mode_key: Option<SearchMode> = None;
            let mut case_key = false;
            let mut deep_key = false;
            let mut show_key = false;
            if focused {
                ctx.input_mut(|i| {
                    esc = i.consume_key(Modifiers::NONE, Key::Escape);
                    enter = i.consume_key(Modifiers::NONE, Key::Enter);
                    up = i.consume_key(Modifiers::NONE, Key::ArrowUp);
                    down = i.consume_key(Modifiers::NONE, Key::ArrowDown);
                    pgup = i.consume_key(Modifiers::NONE, Key::PageUp);
                    pgdn = i.consume_key(Modifiers::NONE, Key::PageDown);
                    tab_key = i.consume_key(Modifiers::NONE, Key::Tab);
                    for (k, m) in [(Key::Num1, SearchMode::Smart), (Key::Num2, SearchMode::Fuzzy), (Key::Num3, SearchMode::Wildcard), (Key::Num4, SearchMode::Regex)] {
                        if i.consume_key(Modifiers::ALT, k) {
                            mode_key = Some(m);
                        }
                    }
                    case_key = i.consume_key(Modifiers::ALT, Key::C);
                    deep_key = i.consume_key(Modifiers::ALT, Key::S);
                    show_key = i.consume_key(Modifiers::ALT, Key::Enter);
                });
            }
            ui.horizontal(|ui| {
                ui.spacing_mut().item_spacing.x = 4.0;
                let w = (ui.available_width() - 400.0 - if opts.deep { 170.0 } else { 0.0 }).max(120.0);
                let mut out = egui::TextEdit::singleline(&mut tab.filter).id(edit_id).hint_text(opts.mode.hint()).desired_width(w).show(ui);
                let r = out.response.clone();
                if err.is_some() {
                    ui.painter().rect_stroke(r.rect.expand(1.0), 4.0, Stroke::new(1.5, ui.visuals().error_fg_color), egui::StrokeKind::Outside);
                }
                if *focus_filter {
                    r.request_focus();
                    let end = tab.filter.chars().count();
                    out.state.cursor.set_char_range(Some(egui::text::CCursorRange::two(egui::text::CCursor::new(0), egui::text::CCursor::new(end))));
                    out.state.store(&ctx, edit_id);
                    *focus_filter = false;
                }
                if r.changed() {
                    tab.rebuild_view(None);
                    tab.set_cursor(0);
                    tab.want_scroll = Some(0.0);
                }
                // result navigation without leaving the box
                let page = ((tab.view_h / ROW_H) as isize - 1).max(1);
                if up {
                    tab.move_cursor(-1, false);
                }
                if down {
                    tab.move_cursor(1, false);
                }
                if pgup {
                    tab.move_cursor(-page, false);
                }
                if pgdn {
                    tab.move_cursor(page, false);
                }
                if show_key {
                    evs.push(Ev::Run(Action::ShowInFolder));
                }
                if enter {
                    evs.push(Ev::Run(Action::Open));
                }
                if tab_key {
                    r.surrender_focus(); // hand the keyboard to the list
                }
                if esc {
                    tab.filter.clear();
                    tab.filter_open = false;
                    tab.rebuild_view(None);
                }
                // mode chips + case toggle
                for m in SearchMode::ALL {
                    if chip(ui, m.label(), opts.mode == m, None).on_hover_text(m.tooltip()).clicked() {
                        evs.push(Ev::SetSearch(SearchOpts { mode: m, ..opts }));
                    }
                }
                if chip(ui, "Aa", opts.match_case, None).on_hover_text("Match case (Alt+C)").clicked() {
                    evs.push(Ev::SetSearch(SearchOpts { match_case: !opts.match_case, ..opts }));
                }
                if chip(ui, "Subfolders", opts.deep, None).on_hover_text("Search inside subfolders too (Alt+S)\nResults show the path relative to this folder.").clicked() || deep_key {
                    evs.push(Ev::SetSearch(SearchOpts { deep: !opts.deep, ..opts }));
                }
                if opts.deep {
                    let before = tab.exclude.clone();
                    let ex = egui::TextEdit::singleline(&mut tab.exclude)
                        .hint_text("exclude: node_modules, .git, *.tmp")
                        .desired_width(160.0)
                        .show(ui);
                    let r: egui::Response = (*ex.response).clone();
                    r.clone().on_hover_text("Names or wildcards to skip while searching subfolders,\nseparated by comma. Nothing is excluded by default.");
                    if r.changed() && tab.exclude != before {
                        tab.rebuild_view(None);
                        evs.push(Ev::SetExclude(tab.exclude.clone()));
                    }
                }
                if let Some(m) = mode_key {
                    evs.push(Ev::SetSearch(SearchOpts { mode: m, ..opts }));
                }
                if case_key {
                    evs.push(Ev::SetSearch(SearchOpts { match_case: !opts.match_case, ..opts }));
                }
                let found = tab.view.len().saturating_sub(tab.has_up() as usize * tab.matcher.is_all() as usize);
                if let Some(e) = &err {
                    ui.colored_label(ui.visuals().error_fg_color, "invalid").on_hover_text(e.as_str());
                } else if let Some((running, truncated)) = tab.deep_status() {
                    let more = if truncated { "+ (limit)" } else if running { " …" } else { "" };
                    ui.weak(format!("{found}{more}"));
                } else {
                    ui.weak(format!("{found} / {}", tab.entries.iter().filter(|e| !e.is_up && !e.deep).count()));
                }
                if chip(ui, "✕", false, None).on_hover_text("Close search (Esc)").clicked() {
                    tab.filter.clear();
                    tab.filter_open = false;
                    tab.rebuild_view(None);
                }
            });
        }

        // --- column header
        let (hdr, _) = ui.allocate_exact_size(vec2(ui.available_width(), ROW_H), Sense::hover());
        let (c_name, c_size, c_date) = columns(hdr);
        for (rect, key, label, align) in [
            (c_name, SortKey::Name, "Name", Align2::LEFT_CENTER),
            (c_size, SortKey::Size, "Size", Align2::RIGHT_CENTER),
            (c_date, SortKey::Modified, "Modified", Align2::LEFT_CENTER),
        ] {
            let r = ui.interact(rect, ui.scope_id().with(("hdr", pi, key as u8)), Sense::click());
            if r.hovered() {
                ui.painter().rect_filled(rect, 2.0, tint(dark, 10));
            }
            let arrow = if tab.sort == key { if tab.asc { " ↑" } else { " ↓" } } else { "" };
            let pos = if align == Align2::LEFT_CENTER { rect.left_center() + vec2(if key == SortKey::Name { 26.0 } else { 0.0 }, 0.0) } else { rect.right_center() };
            ui.painter().text(pos, align, format!("{label}{arrow}"), FontId::proportional(12.5), ui.visuals().weak_text_color());
            if r.clicked() {
                evs.push(Ev::Sort(pi, key));
            }
        }
        ui.painter().hline(hdr.x_range(), hdr.bottom(), Stroke::new(1.0, line_color(dark)));

        // --- file list
        let footer_h = 22.0;
        let list_h = (ui.available_height() - footer_h).max(40.0);
        let list_w = ui.available_width();
        ui.allocate_ui(vec2(list_w, list_h), |ui| {
            // Right-click on the empty space below the files. Registered before the
            // rows, so rows (added later) keep priority over it.
            let list_rect = Rect::from_min_size(ui.cursor().min, vec2(list_w, list_h));
            let bg = ui.interact(list_rect, ui.scope_id().with(("list-bg", pi)), Sense::click());
            let can_paste = clipboard.is_some();
            bg.context_menu(|ui| {
                for (label, a, enabled) in [
                    ("New folder", Action::NewFolder, true),
                    ("New file", Action::NewFile, true),
                    ("Paste", Action::ClipPaste, can_paste),
                    ("Select all", Action::SelectAll, true),
                    ("Refresh", Action::Refresh, true),
                    ("Copy folder path", Action::CopyFolderPath, true),
                    ("Open terminal here", Action::Terminal, true),
                    ("Open folder in Windows Explorer", Action::ExplorerHere, true),
                    ("Show hidden files", Action::ToggleHidden, true),
                    ("Sort by name", Action::SortName, true),
                    ("Sort by date", Action::SortDate, true),
                    ("Sort by size", Action::SortSize, true),
                ] {
                    if ui.add_enabled(enabled, egui::Button::new(label)).clicked() {
                        evs.push(Ev::Run(a));
                        ui.close();
                    }
                }
            });
            if let Some(err) = &tab.error {
                ui.colored_label(ui.visuals().error_fg_color, format!("⚠ {err}"));
                return;
            }
            if tab.view.iter().all(|&i| tab.entries[i].is_up) {
                ui.weak(if tab.filter.is_empty() { "Empty folder" } else { "Nothing matches the filter" });
            }
            ui.spacing_mut().item_spacing.y = 0.0;
            let mut sa = egui::ScrollArea::vertical().id_salt(("list", pi, *ai)).auto_shrink([false, false]);
            if let Some(y) = tab.want_scroll.take() {
                sa = sa.vertical_scroll_offset(y.max(0.0));
            }
            let n = tab.view.len();
            let out = sa.show_rows(ui, ROW_H, n, |ui, range| {
                for vi in range {
                    let e = &tab.entries[tab.view[vi]];
                    let (rect, resp) = ui.allocate_exact_size(vec2(ui.available_width(), ROW_H), Sense::click_and_drag());
                    if !ui.is_rect_visible(rect) {
                        continue;
                    }
                    let is_cursor = vi == tab.cursor;
                    let selected = tab.selected.contains(&e.path);
                    let drop_target = dragging && e.is_dir && ui.rect_contains_pointer(rect) && !drag_paths.contains(&e.path);
                    let p = ui.painter();
                    if selected {
                        p.rect_filled(rect, 0.0, selected_text(dark).gamma_multiply(0.13));
                    }
                    if drop_target {
                        p.rect_filled(rect, 3.0, ACCENT.gamma_multiply(0.5));
                        evs.push(Ev::DropHover(e.path.clone()));
                    } else if is_cursor {
                        let c = match (is_active, dark) {
                            (true, true) => ACCENT.gamma_multiply(0.55),
                            (true, false) => Color32::from_rgb(200, 221, 253),
                            (false, _) => tint(dark, 28),
                        };
                        p.rect_filled(rect, 0.0, c);
                    } else if resp.hovered() {
                        p.rect_filled(rect, 0.0, tint(dark, 10));
                    }
                    let (c_name, c_size, c_date) = columns(rect);
                    // icon
                    let icon_rect = Rect::from_center_size(pos2(rect.left() + 14.0, rect.center().y), vec2(16.0, 16.0));
                    if let Some(tex) = icons.get(&ctx, &e.ext, e.is_dir) {
                        egui::Image::new(egui::load::SizedTexture::new(tex.id(), vec2(16.0, 16.0))).paint_at(ui, icon_rect);
                    }
                    let mut col = ui.visuals().text_color();
                    if selected {
                        col = selected_text(dark);
                    }
                    if e.hidden || cut.is_some_and(|c| c.contains(&e.path)) {
                        col = col.gamma_multiply(0.55);
                    }
                    // Text on the accent-colored cursor row must stay readable.
                    let on_accent = dark && is_cursor && is_active && !drop_target;
                    if on_accent && !selected {
                        col = Color32::WHITE;
                    }
                    if !diff.is_empty() && !e.is_up {
                        if let Some(d) = diff.get(&e.name.to_lowercase()) {
                            let (c, tip) = d.style(dark);
                            ui.painter().rect_filled(Rect::from_min_size(rect.min, vec2(4.0, rect.height())), 0.0, c);
                            if !selected && !on_accent {
                                col = c;
                            }
                            let _ = tip;
                        }
                    }
                    let editing = inline.as_ref().is_some_and(|r| r.pi == pi && r.path == e.path);
                    if editing {
                        let edit_rect = Rect::from_min_max(pos2(c_name.left() + 22.0, rect.top() + 1.0), pos2(c_name.right() - 2.0, rect.bottom() - 1.0));
                        if let Some(ir) = inline.as_mut() {
                            inline_edit(ui, edit_rect, ir, e.is_dir);
                        }
                    } else {
                        let name_x = c_name.left() + 26.0;
                        let painter = ui.painter().with_clip_rect(c_name);
                        let galley = painter.layout_no_wrap(e.name.clone(), FontId::proportional(14.0), col);
                        let top = rect.center().y - galley.size().y / 2.0;
                        if !tab.filter.is_empty() {
                            let hl = if dark { Color32::from_rgba_unmultiplied(255, 200, 0, 80) } else { Color32::from_rgba_unmultiplied(255, 190, 0, 120) };
                            for (a, b) in tab.matcher.ranges(&e.name) {
                                let x0 = galley.pos_from_cursor(egui::text::CCursor::new(a)).min.x;
                                let x1 = galley.pos_from_cursor(egui::text::CCursor::new(b)).min.x;
                                painter.rect_filled(Rect::from_min_max(pos2(name_x + x0 - 1.0, top), pos2(name_x + x1 + 1.0, top + galley.size().y)), 2.0, hl);
                            }
                        }
                        painter.galley(pos2(name_x, top), galley, col);
                    }
                    let dim = if on_accent { Color32::from_gray(225) } else { ui.visuals().weak_text_color() };
                    let p = ui.painter();
                    p.with_clip_rect(c_size).text(
                        pos2(c_size.right(), rect.center().y),
                        Align2::RIGHT_CENTER,
                        if e.is_up { String::new() } else if e.is_dir { "<DIR>".to_string() } else { fmt_size(e.size) },
                        FontId::proportional(12.5),
                        dim,
                    );
                    p.with_clip_rect(c_date).text(pos2(c_date.left(), rect.center().y), Align2::LEFT_CENTER, if e.is_up { String::new() } else { fmt_time(e.modified) }, FontId::proportional(12.5), dim);

                    // interaction
                    let m = ui.input(|i| i.modifiers);
                    if resp.clicked() {
                        evs.push(Ev::Click { pi, vi, ctrl: m.ctrl, shift: m.shift });
                    }
                    if resp.double_clicked() {
                        evs.push(Ev::Open { pi, vi });
                    }
                    if resp.middle_clicked() {
                        evs.push(Ev::MiddleOpen { pi, vi });
                    }
                    if resp.secondary_clicked() {
                        evs.push(Ev::ContextAt { pi, vi });
                    }
                    if resp.drag_started() && !e.is_up {
                        let paths = if selected { tab.targets() } else { vec![e.path.clone()] };
                        evs.push(Ev::DragStart { pi, paths });
                    }
                    resp.context_menu(|ui| {
                        for (label, a) in [
                            ("Open", Action::Open),
                            ("Open in new tab", Action::OpenInNewTab),
                            ("Rename", Action::Rename),
                            ("Copy to other pane", Action::Copy),
                            ("Move to other pane", Action::Move),
                            ("Copy", Action::ClipCopy),
                            ("Cut", Action::ClipCut),
                            ("Paste", Action::ClipPaste),
                            ("Copy path", Action::CopyPath),
                            ("New folder", Action::NewFolder),
                            ("Open containing folder  (Alt+Enter)", Action::ShowInFolder),
                            ("Show in Windows Explorer", Action::Reveal),
                            ("Delete", Action::Delete),
                        ] {
                            if ui.button(label).clicked() {
                                evs.push(Ev::Run(a));
                                ui.close();
                            }
                        }
                    });
                }
            });
            tab.scroll_y = out.state.offset().y;
            tab.view_h = out.inner_rect.height();
        });

        // --- footer
        ui.horizontal(|ui| {
            if let Some(l) = &tab.loading {
                ui.spinner();
                ui.weak(format!("Loading {} … {:.0}s", display_name(&l.to), l.since.elapsed().as_secs_f32()));
            }
            ui.weak(tab.selection_summary());
            if let Some(e) = tab.current() {
                if !e.is_dir {
                    ui.weak(format!("· {}", e.name));
                }
            }
        });
    }
}

// ------------------------------------------------------------ pane widgets

/// Current outer position / inner size of the viewport (None while minimized).
fn window_geom(ctx: &Context) -> Option<WinGeom> {
    ctx.input(|i| {
        let vp = i.viewport();
        if vp.minimized == Some(true) {
            return None;
        }
        let (o, n) = (vp.outer_rect?, vp.inner_rect?);
        Some(WinGeom { x: o.min.x, y: o.min.y, w: n.width(), h: n.height(), maximized: vp.maximized == Some(true) })
    })
}

#[derive(Clone, Copy, PartialEq, Debug)]
enum Diff {
    OnlyHere,
    Newer,
    Older,
    Size,
}

impl Diff {
    fn style(self, dark: bool) -> (Color32, &'static str) {
        match (self, dark) {
            (Diff::OnlyHere, true) => (Color32::from_rgb(110, 205, 120), "only in this pane"),
            (Diff::OnlyHere, false) => (Color32::from_rgb(30, 135, 50), "only in this pane"),
            (Diff::Newer, true) => (Color32::from_rgb(255, 170, 60), "newer than in the other pane"),
            (Diff::Newer, false) => (Color32::from_rgb(200, 110, 0), "newer than in the other pane"),
            (Diff::Older, true) => (Color32::from_rgb(235, 110, 110), "older than in the other pane"),
            (Diff::Older, false) => (Color32::from_rgb(190, 50, 50), "older than in the other pane"),
            (Diff::Size, true) => (Color32::from_rgb(190, 140, 255), "different size"),
            (Diff::Size, false) => (Color32::from_rgb(120, 60, 200), "different size"),
        }
    }
}

/// How the items of `a` differ from the folder shown in `b` (by name; same items are left out).
fn diff_map(a: &Tab, b: &Tab) -> HashMap<String, Diff> {
    let other: HashMap<String, &Entry> = b.entries.iter().filter(|e| !e.is_up && !e.deep).map(|e| (e.name.to_lowercase(), e)).collect();
    let mut out = HashMap::new();
    for e in a.entries.iter().filter(|e| !e.is_up && !e.deep) {
        let key = e.name.to_lowercase();
        let d = match other.get(&key) {
            None => Some(Diff::OnlyHere),
            Some(o) if o.is_dir || e.is_dir => None,
            Some(o) => {
                let slack = Duration::from_secs(2);
                match (e.modified, o.modified) {
                    (Some(x), Some(y)) if x > y + slack => Some(Diff::Newer),
                    (Some(x), Some(y)) if y > x + slack => Some(Diff::Older),
                    _ if e.size != o.size => Some(Diff::Size),
                    _ => None,
                }
            }
        };
        if let Some(d) = d {
            out.insert(key, d);
        }
    }
    out
}

fn columns(r: Rect) -> (Rect, Rect, Rect) {
    let date = Rect::from_min_max(pos2(r.right() - 134.0, r.top()), pos2(r.right() - 6.0, r.bottom()));
    let size = Rect::from_min_max(pos2(date.left() - 84.0, r.top()), pos2(date.left() - 8.0, r.bottom()));
    let name = Rect::from_min_max(r.min, pos2(size.left() - 6.0, r.bottom()));
    (name, size, date)
}

#[derive(Clone, Copy, PartialEq)]
enum Ico {
    Back,
    Fwd,
    Up,
    Root,
    Plus,
    Close,
    Down,
}

fn paint_icon(p: &egui::Painter, c: Pos2, ico: Ico, col: Color32) {
    let st = Stroke::new(1.6, col);
    let segs: &[[(f32, f32); 2]] = match ico {
        Ico::Back => &[[(-5.0, 0.0), (5.0, 0.0)], [(-5.0, 0.0), (-1.0, -4.0)], [(-5.0, 0.0), (-1.0, 4.0)]],
        Ico::Fwd => &[[(-5.0, 0.0), (5.0, 0.0)], [(5.0, 0.0), (1.0, -4.0)], [(5.0, 0.0), (1.0, 4.0)]],
        Ico::Up => &[[(0.0, 5.0), (0.0, -5.0)], [(0.0, -5.0), (-4.0, -1.0)], [(0.0, -5.0), (4.0, -1.0)]],
        Ico::Root => &[[(-5.0, -5.0), (5.0, -5.0)], [(0.0, 5.0), (0.0, -3.0)], [(0.0, -3.0), (-3.5, 0.5)], [(0.0, -3.0), (3.5, 0.5)]],
        Ico::Plus => &[[(-4.5, 0.0), (4.5, 0.0)], [(0.0, -4.5), (0.0, 4.5)]],
        Ico::Close => &[[(-3.0, -3.0), (3.0, 3.0)], [(-3.0, 3.0), (3.0, -3.0)]],
        Ico::Down => &[[(-4.0, -2.0), (0.0, 2.0)], [(0.0, 2.0), (4.0, -2.0)]],
    };
    for [a, b] in segs {
        let (pa, pb) = (c + vec2(a.0, a.1), c + vec2(b.0, b.1));
        p.line_segment([pa, pb], st);
        p.circle_filled(pa, 0.8, col); // round caps
        p.circle_filled(pb, 0.8, col);
    }
}

/// Flat, rounded icon button with a vector-drawn glyph.
fn icon_btn(ui: &mut Ui, ico: Ico, enabled: bool, tip: &str) -> egui::Response {
    let dark = ui.visuals().dark_mode;
    let (rect, resp) = ui.allocate_exact_size(vec2(28.0, 26.0), if enabled { Sense::click() } else { Sense::hover() });
    if enabled && resp.hovered() {
        ui.painter().rect_filled(rect, 7.0, tint(dark, if resp.is_pointer_button_down_on() { 40 } else { 22 }));
    }
    let base = ui.visuals().text_color();
    paint_icon(ui.painter(), rect.center(), ico, if enabled { base } else { base.gamma_multiply(0.3) });
    if enabled { resp.on_hover_text(tip) } else { resp }
}

fn fmt_dur(secs: f64) -> String {
    let s = secs.round() as u64;
    if s >= 3600 { format!("{}:{:02}:{:02}", s / 3600, (s / 60) % 60, s % 60) } else { format!("{}:{:02}", s / 60, s % 60) }
}

fn job_row(ui: &mut Ui, j: &mut Job, dismiss: &mut Vec<u64>) {
    let state = j.state.clone();
    ui.horizontal(|ui| {
        ui.label(egui::RichText::new(&j.title).strong());
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| match &state {
            JobState::Running => {
                let label = if j.cancelling { "Cancelling…" } else { "Cancel" };
                if ui.add_enabled(!j.cancelling, egui::Button::new(label)).clicked() {
                    j.cancel.store(true, Ordering::Relaxed);
                    j.cancelling = true;
                }
            }
            JobState::Failed(_) => {
                if ui.button("Dismiss").clicked() {
                    dismiss.push(j.id);
                }
            }
            _ => {}
        });
    });
    if let Some(d) = &j.dest {
        ui.weak(format!("to {}", d.display()));
    }
    match &state {
        JobState::Running => {
            let indeterminate = j.kind == OpKind::Trash;
            let frac = if j.total > 0 && !indeterminate { (j.done as f32 / j.total as f32).clamp(0.0, 1.0) } else { 0.0 };
            ui.add(egui::ProgressBar::new(frac).desired_width(ui.available_width()).show_percentage().animate(indeterminate));
            let el = j.started.elapsed().as_secs_f64().max(0.001);
            let info = match j.kind {
                OpKind::Copy | OpKind::Move if j.total > 0 => {
                    let rate = j.done as f64 / el;
                    let left = if rate > 1.0 && j.total > j.done { (j.total - j.done) as f64 / rate } else { 0.0 };
                    let eta = if left > 0.5 && el > 1.5 { format!(" · {} left", fmt_dur(left)) } else { String::new() };
                    format!("{} of {} · {}/s{}", fmt_size(j.done), fmt_size(j.total), fmt_size(rate as u64), eta)
                }
                OpKind::Delete => format!("{} of {} items", j.done, j.total),
                _ => "Working…".to_string(),
            };
            ui.weak(info);
            if !j.current.is_empty() && !indeterminate {
                ui.add(egui::Label::new(egui::RichText::new(&j.current).weak()).truncate());
            }
        }
        JobState::Done => {
            ui.colored_label(Color32::from_rgb(70, 170, 90), "✓ Done");
        }
        JobState::Cancelled => {
            ui.weak("Cancelled");
        }
        JobState::Failed(e) => {
            ui.colored_label(ui.visuals().error_fg_color, format!("⚠ {e}"));
        }
    }
}

fn tab_menu_item(ui: &mut Ui, evs: &mut Vec<Ev>, pi: usize, i: usize, label: &str, enabled: bool, cmd: TabCmd) {
    if ui.add_enabled(enabled, egui::Button::new(label)).clicked() {
        evs.push(Ev::TabMenu(pi, i, cmd));
        ui.close();
    }
}

/// Pill-shaped tab; returns (response, close_clicked).
fn tab_pill(ui: &mut Ui, label: &str, selected: bool, closable: bool) -> (egui::Response, bool) {
    let dark = ui.visuals().dark_mode;
    let col = if selected { ui.visuals().text_color() } else { ui.visuals().weak_text_color() };
    let galley = ui.painter().layout_no_wrap(label.to_string(), FontId::proportional(13.5), col);
    let w = galley.size().x + 22.0 + if closable { 18.0 } else { 0.0 };
    let (rect, resp) = ui.allocate_exact_size(vec2(w, 26.0), Sense::click_and_drag());
    let hovered = ui.rect_contains_pointer(rect);
    if selected {
        ui.painter().rect_filled(rect, 7.0, tint(dark, 20));
        let bar = Rect::from_min_max(pos2(rect.left() + 9.0, rect.bottom() - 3.0), pos2(rect.right() - 9.0, rect.bottom() - 1.0));
        ui.painter().rect_filled(bar, 1.0, ACCENT);
    } else if hovered {
        ui.painter().rect_filled(rect, 7.0, tint(dark, 12));
    }
    ui.painter().galley(pos2(rect.left() + 11.0, rect.center().y - galley.size().y / 2.0), galley, col);
    let mut close = false;
    if closable && (selected || hovered) {
        let xr = Rect::from_center_size(pos2(rect.right() - 13.0, rect.center().y - 1.0), vec2(16.0, 16.0));
        let xresp = ui.interact(xr, resp.id.with("x"), Sense::click());
        if xresp.hovered() {
            ui.painter().rect_filled(xr, 4.0, tint(dark, 30));
        }
        paint_icon(ui.painter(), xr.center(), Ico::Close, col);
        close = xresp.on_hover_text("Close tab (Ctrl+W)").clicked();
    }
    (resp, close)
}

/// Small rounded chip (drive letters, favorite star).
fn chip(ui: &mut Ui, text: &str, selected: bool, color: Option<Color32>) -> egui::Response {
    let dark = ui.visuals().dark_mode;
    let col = color.unwrap_or(if selected { ui.visuals().text_color() } else { ui.visuals().weak_text_color() });
    let galley = ui.painter().layout_no_wrap(text.to_string(), FontId::proportional(12.5), col);
    let (rect, resp) = ui.allocate_exact_size(vec2(galley.size().x + 14.0, 20.0), Sense::click());
    if selected {
        ui.painter().rect_filled(rect, 10.0, ACCENT.gamma_multiply(if dark { 0.38 } else { 0.28 }));
    } else if resp.hovered() {
        ui.painter().rect_filled(rect, 10.0, tint(dark, 18));
    }
    ui.painter().galley(rect.center() - galley.size() / 2.0, galley, col);
    resp
}

fn breadcrumbs(ui: &mut Ui, path: &Path, pi: usize, evs: &mut Vec<Ev>) {
    let mut crumbs: Vec<(String, PathBuf)> = vec![];
    let mut acc = PathBuf::new();
    for c in path.components() {
        acc.push(c.as_os_str());
        let label = match c {
            std::path::Component::Prefix(p) => p.as_os_str().to_string_lossy().into_owned(),
            std::path::Component::RootDir => continue,
            c => c.as_os_str().to_string_lossy().into_owned(),
        };
        let target = if matches!(c, std::path::Component::Prefix(_)) { PathBuf::from(format!("{}\\", acc.display())) } else { acc.clone() };
        crumbs.push((label, target));
    }
    // Keep the tail that fits.
    let avail = ui.available_width() - 40.0;
    let mut used = 0.0;
    let mut start = crumbs.len();
    for (i, (l, _)) in crumbs.iter().enumerate().rev() {
        let w = l.chars().count() as f32 * 7.5 + 20.0;
        if used + w > avail && start < crumbs.len() {
            break;
        }
        used += w;
        start = i;
    }
    ui.spacing_mut().item_spacing.x = 0.0;
    ui.spacing_mut().button_padding = vec2(5.0, 1.0);
    if start > 0 {
        ui.weak("…");
    }
    for (i, (label, target)) in crumbs.iter().enumerate().skip(start) {
        if i > start {
            ui.weak("›");
        }
        let r = ui.add(egui::Button::new(label).frame(false));
        if r.clicked() {
            evs.push(Ev::Go(pi, target.clone()));
        }
    }
    let rest = ui.available_size_before_wrap();
    let r = ui.allocate_response(vec2(rest.x.max(20.0), 22.0), Sense::click());
    r.clone().on_hover_text("Click to edit path (Ctrl+L)");
    if r.clicked() || r.double_clicked() {
        evs.push(Ev::Run(Action::EditPath));
    }
}

fn suggestions(text: &str, orig: &str, base: &Path, store: &Store) -> Vec<Sugg> {
    const MAX: usize = 14;
    let mut seen: HashSet<String> = HashSet::new();
    let mut push = |out: &mut Vec<Sugg>, p: PathBuf, tag: &'static str| {
        if out.len() < MAX && seen.insert(p.to_string_lossy().to_lowercase()) {
            out.push(Sugg { path: p, tag });
        }
    };
    let q = if text == orig { "" } else { text.trim() };

    // Just opened (text untouched): favorites, then most frecent folders.
    if q.is_empty() {
        let mut out = vec![];
        for f in &store.favorites {
            let p = PathBuf::from(f);
            if p != base && p.is_dir() {
                push(&mut out, p, "★ favorite");
            }
        }
        for (p, _) in store.frecent().into_iter().take(40) {
            if p != base && p.is_dir() {
                push(&mut out, p, "recent");
            }
        }
        return out;
    }

    let subfolders = |dir_part: &str, leaf: &str| -> Vec<PathBuf> {
        let dir = if dir_part.is_empty() {
            base.to_path_buf()
        } else {
            match parse_user_path(dir_part, base) {
                Some(d) => d,
                None => return vec![],
            }
        };
        let Ok(rd) = read_dir(&dir) else { return vec![] };
        let l = leaf.to_lowercase();
        let mut v: Vec<(bool, PathBuf, String)> = rd
            .into_iter()
            .filter(|e| e.is_dir && (l.is_empty() || e.name.to_lowercase().contains(&l)))
            .map(|e| (e.name.to_lowercase().starts_with(&l), e.path, e.name))
            .collect();
        v.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| natural_cmp(&a.2, &b.2)));
        v.into_iter().take(8).map(|x| x.1).collect()
    };
    let (dir_part, leaf) = match q.rfind(['\\', '/']) {
        Some(i) => (&q[..=i], &q[i + 1..]),
        None => ("", q),
    };
    let pathlike = q.contains(['\\', '/', ':', '~', '%']);

    // favorites + recent folders matching anywhere in the path
    let mut ranked: Vec<(i32, PathBuf, &'static str)> = vec![];
    for f in &store.favorites {
        let best = actions::fuzzy(q, f).into_iter().chain(actions::fuzzy(q, &display_name(Path::new(f))).map(|s| s + 20)).max();
        if let Some(s) = best {
            ranked.push((s + 500, PathBuf::from(f), "★ favorite"));
        }
    }
    for (p, score) in store.frecent().into_iter() {
        let full = p.to_string_lossy();
        let best = actions::fuzzy(q, &full).into_iter().chain(actions::fuzzy(q, &display_name(&p)).map(|s| s + 20)).max();
        if let Some(s) = best {
            ranked.push((s + (score as i32).min(100) / 2, p, "recent"));
        }
    }
    ranked.sort_by(|a, b| b.0.cmp(&a.0));
    let hist: Vec<(PathBuf, &'static str)> = ranked.into_iter().filter(|r| r.1.is_dir()).take(8).map(|r| (r.1, r.2)).collect();

    let mut out = vec![];
    if pathlike {
        for p in subfolders(dir_part, leaf).into_iter().take(if hist.is_empty() { 8 } else { 6 }) {
            push(&mut out, p, "");
        }
        for (p, t) in hist {
            push(&mut out, p, t);
        }
    } else {
        for (p, t) in hist {
            push(&mut out, p, t);
        }
        for p in subfolders("", q) {
            push(&mut out, p, "");
        }
    }
    out
}

fn with_trailing_sep(p: &Path) -> String {
    format!("{}\\", p.display().to_string().trim_end_matches('\\'))
}

fn path_edit_ui(ui: &mut Ui, pe: &mut PathEdit, base: &Path, pi: usize, evs: &mut Vec<Ev>, store: &mut Store) {
    let ctx = ui.ctx().clone();
    // Entry to forget: (path, tag). Set by the × button or the Delete key.
    let mut remove: Option<(PathBuf, &'static str)> = None;
    let mut add_fav: Option<PathBuf> = None;
    // keys consumed before the widget sees them
    let (esc, enter, down, up, tab) = ctx.input_mut(|i| {
        (
            i.consume_key(Modifiers::NONE, Key::Escape),
            i.consume_key(Modifiers::NONE, Key::Enter),
            i.consume_key(Modifiers::NONE, Key::ArrowDown),
            i.consume_key(Modifiers::NONE, Key::ArrowUp),
            i.consume_key(Modifiers::NONE, Key::Tab),
        )
    });
    // Right arrow takes the highlighted suggestion into the field (only while one is highlighted,
    // so it still moves the caret during normal typing).
    let right = pe.sel.is_some() && ctx.input_mut(|i| i.consume_key(Modifiers::NONE, Key::ArrowRight));
    let accept = tab || right;
    if pe.sugg_for != pe.text {
        pe.sugg = suggestions(&pe.text, &pe.orig, base, store);
        pe.sugg_for = pe.text.clone();
        pe.sel = None;
    }
    if down && !pe.sugg.is_empty() {
        pe.sel = Some(pe.sel.map_or(0, |s| (s + 1).min(pe.sugg.len() - 1)));
    }
    if up {
        pe.sel = match pe.sel {
            Some(0) | None => None,
            Some(s) => Some(s - 1),
        };
    }
    if accept {
        if let Some(sg) = pe.sel.and_then(|s| pe.sugg.get(s)).or(pe.sugg.first()) {
            pe.text = with_trailing_sep(&sg.path);
            pe.fresh = true; // move caret to end
        }
    }
    // Delete on a highlighted favorite/recent entry removes it from the list
    // (only while an entry is selected with the arrow keys, so normal text
    // editing with Delete is unaffected).
    if let Some(sg) = pe.sel.and_then(|s| pe.sugg.get(s)).filter(|sg| !sg.tag.is_empty()) {
        if ctx.input_mut(|i| i.consume_key(Modifiers::NONE, Key::Delete)) {
            remove = Some((sg.path.clone(), sg.tag));
        }
    }
    let width = ui.available_width();
    let mut out = egui::TextEdit::singleline(&mut pe.text)
        .frame(egui::Frame::NONE)
        .desired_width(width)
        .font(FontId::proportional(14.0))
        .show(ui);
    if pe.fresh {
        out.response.request_focus();
        let end = pe.text.chars().count();
        let range = if accept {
            egui::text::CCursorRange::one(egui::text::CCursor::new(end))
        } else {
            egui::text::CCursorRange::two(egui::text::CCursor::new(0), egui::text::CCursor::new(end))
        };
        out.state.cursor.set_char_range(Some(range));
        out.state.store(&ctx, out.response.id);
        pe.fresh = false;
    }
    // suggestion popup
    let mut go: Option<PathBuf> = None;
    let mut keep_editing: Option<PathBuf> = None;
    if !pe.sugg.is_empty() {
        let pill = out.response.rect.expand2(vec2(6.0, 1.0));
        let area = egui::Area::new(egui::Id::new(("path-sugg", pi)))
            .order(egui::Order::Foreground)
            .fixed_pos(pill.left_bottom() + vec2(0.0, 3.0))
            .show(&ctx, |ui| {
                egui::Frame::popup(ui.style()).corner_radius(8).show(ui, |ui| {
                    ui.set_width(pill.width().clamp(260.0, 560.0));
                    ui.spacing_mut().item_spacing.y = 1.0;
                    let dark = ui.visuals().dark_mode;
                    for (i, sg) in pe.sugg.iter().enumerate() {
                        let (rect, resp) = ui.allocate_exact_size(vec2(ui.available_width(), 24.0), Sense::click());
                        if pe.sel == Some(i) {
                            ui.painter().rect_filled(rect, 5.0, ACCENT.gamma_multiply(if dark { 0.5 } else { 0.3 }));
                        } else if resp.hovered() {
                            ui.painter().rect_filled(rect, 5.0, tint(dark, 14));
                        }
                        let (label, right) = if sg.tag.is_empty() {
                            (display_name(&sg.path), "folder")
                        } else {
                            (sg.path.display().to_string(), sg.tag)
                        };
                        let tc = ui.visuals().text_color();
                        let weak = ui.visuals().weak_text_color();
                        let removable = !sg.tag.is_empty();
                        let can_star = sg.tag == "recent";
                        let tag_right = if removable { 28.0 + if can_star { 20.0 } else { 0.0 } } else { 8.0 };
                        let clip = Rect::from_min_max(rect.min, pos2(rect.right() - 84.0 - if removable { 20.0 } else { 0.0 } - if can_star { 20.0 } else { 0.0 }, rect.bottom()));
                        ui.painter().with_clip_rect(clip).text(rect.left_center() + vec2(8.0, 0.0), Align2::LEFT_CENTER, label, FontId::proportional(13.5), tc);
                        ui.painter().text(rect.right_center() - vec2(tag_right, 0.0), Align2::RIGHT_CENTER, right, FontId::proportional(11.5), weak);
                        let mut x_clicked = false;
                        if can_star {
                            let sr = Rect::from_center_size(pos2(rect.right() - 34.0, rect.center().y), vec2(18.0, 18.0));
                            let sresp = ui.interact(sr, resp.id.with("fav"), Sense::click());
                            if sresp.hovered() {
                                ui.painter().rect_filled(sr, 5.0, tint(dark, 34));
                            }
                            let col = if sresp.hovered() { Color32::from_rgb(255, 190, 0) } else if resp.hovered() || pe.sel == Some(i) { tc } else { weak };
                            ui.painter().text(sr.center(), Align2::CENTER_CENTER, "☆", FontId::proportional(14.0), col);
                            if sresp.on_hover_text("Add to favorites").clicked() {
                                add_fav = Some(sg.path.clone());
                                x_clicked = true;
                            }
                        }
                        if removable {
                            let xr = Rect::from_center_size(pos2(rect.right() - 14.0, rect.center().y), vec2(18.0, 18.0));
                            let xresp = ui.interact(xr, resp.id.with("forget"), Sense::click());
                            if xresp.hovered() {
                                ui.painter().rect_filled(xr, 5.0, tint(dark, 34));
                            }
                            let hint = if sg.tag == "recent" { "Remove from recent folders" } else { "Remove from favorites" };
                            paint_icon(ui.painter(), xr.center(), Ico::Close, if resp.hovered() || xresp.hovered() || pe.sel == Some(i) { tc } else { weak });
                            if xresp.on_hover_text(hint).clicked() {
                                remove = Some((sg.path.clone(), sg.tag));
                                x_clicked = true;
                            }
                        }
                        if resp.clicked() && !x_clicked {
                            if sg.tag.is_empty() {
                                keep_editing = Some(sg.path.clone());
                            } else {
                                go = Some(sg.path.clone());
                            }
                        }
                    }
                });
            });
        pe.popup_rect = area.response.rect;
    } else {
        pe.popup_rect = Rect::NOTHING;
    }
    if let Some(p) = add_fav {
        if !store.is_favorite(&p) {
            store.toggle_favorite(&p);
        }
        store.save();
        pe.sugg_for = "\u{1}".into();
        out.response.request_focus();
    }
    if let Some((p, tag)) = remove {
        if tag == "recent" {
            store.forget_visit(&p);
        } else {
            store.remove_favorite(&p);
        }
        store.save();
        pe.sugg_for = "\u{1}".into(); // force the list to be rebuilt
        out.response.request_focus(); // keep typing after removing an entry
    }
    if let Some(p) = keep_editing {
        pe.text = with_trailing_sep(&p);
        pe.fresh = true;
    }
    let in_popup = ctx.input(|i| i.pointer.hover_pos()).is_some_and(|p| pe.popup_rect.contains(p));
    if esc {
        pe.text = "\u{0}close".into();
    } else if let Some(p) = go {
        evs.push(Ev::GoTyped(pi, p.display().to_string()));
        pe.text = "\u{0}close".into();
    } else if enter {
        let target = pe.sel.and_then(|s| pe.sugg.get(s)).map(|sg| sg.path.display().to_string()).unwrap_or_else(|| pe.text.clone());
        evs.push(Ev::GoTyped(pi, target));
        pe.text = "\u{0}close".into();
    } else if out.response.lost_focus() && !pe.fresh {
        if in_popup {
            out.response.request_focus(); // clicking the popup must not close the editor
        } else {
            pe.text = "\u{0}close".into();
        }
    }
}

fn text_field(ui: &mut Ui, text: &mut String, fresh: &mut bool, select_chars: Option<usize>) {
    let ctx = ui.ctx().clone();
    let mut out = egui::TextEdit::singleline(text).desired_width(f32::INFINITY).show(ui);
    if *fresh {
        out.response.request_focus();
        let end = select_chars.unwrap_or_else(|| text.chars().count());
        out.state.cursor.set_char_range(Some(egui::text::CCursorRange::two(egui::text::CCursor::new(0), egui::text::CCursor::new(end))));
        out.state.store(&ctx, out.response.id);
        *fresh = false;
    }
}

/// Text field drawn inside a list row; Enter/blur commits, Esc cancels.
fn inline_edit(ui: &mut Ui, rect: Rect, ir: &mut InlineRename, is_dir: bool) {
    let ctx = ui.ctx().clone();
    let (esc, enter) = ctx.input_mut(|i| (i.consume_key(Modifiers::NONE, Key::Escape), i.consume_key(Modifiers::NONE, Key::Enter)));
    let mut out = ui
        .scope_builder(egui::UiBuilder::new().max_rect(rect), |ui| {
            egui::TextEdit::singleline(&mut ir.text)
                .font(FontId::proportional(14.0))
                .desired_width(rect.width())
                .margin(vec2(3.0, 1.0))
                .show(ui)
        })
        .inner;
    if ir.fresh {
        out.response.request_focus();
        let end = if is_dir { ir.text.chars().count() } else { stem_len(&ir.text) };
        out.state.cursor.set_char_range(Some(egui::text::CCursorRange::two(egui::text::CCursor::new(0), egui::text::CCursor::new(end))));
        out.state.store(&ctx, out.response.id);
        ir.fresh = false;
    }
    if out.response.has_focus() {
        ir.seen_focus = true;
    }
    if esc {
        ir.cancel = true;
    } else if enter || (ir.seen_focus && out.response.lost_focus()) {
        ir.commit = true;
    }
}

fn stem_len(name: &str) -> usize {
    match name.rfind('.') {
        Some(i) if i > 0 => name[..i].chars().count(),
        _ => name.chars().count(),
    }
}

fn validate_name(name: &str) -> Result<(), String> {
    if name.is_empty() {
        return Err("Name can't be empty".into());
    }
    if name.chars().any(|c| matches!(c, '\\' | '/' | ':' | '*' | '?' | '"' | '<' | '>' | '|')) {
        return Err("Name can't contain \\ / : * ? \" < > |".into());
    }
    if name.ends_with('.') || name.ends_with(' ') {
        return Err("Name can't end with a dot or space".into());
    }
    Ok(())
}

fn same_volume(a: &Path, b: &Path) -> bool {
    a.components().next() == b.components().next()
}

// ----------------------------------------------------------------- eframe

impl eframe::App for App {
    fn ui(&mut self, ui: &mut Ui, _frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        self.drop_hover = None;
        self.prev_active = self.active;
        self.tick(&ctx);
        if ctx.input(|i| i.pointer.button_pressed(egui::PointerButton::Primary)) {
            // handled below via pane rects
        }
        if ctx.input(|i| i.key_pressed(Key::Escape)) {
            self.drag = None;
            self.tab_drag = None;
        }
        self.handle_keys(&ctx);

        let mut evs: Vec<Ev> = vec![];
        egui::Panel::bottom("status").show(ui, |ui| self.ui_status(ui, &mut evs));
        if self.preview {
            let entry = self.panes[self.active].tab().current().cloned();
            let mut close = false;
            egui::Panel::right("quicklook").resizable(true).default_size(360.0).min_size(220.0).show(ui, |ui| {
                close = self.pv.ui(ui, entry.as_ref());
            });
            if close {
                self.preview = false;
            }
        }
        egui::CentralPanel::default().show(ui, |ui| {
            let full = ui.available_rect_before_wrap();
            let gap = 8.0;
            let w = (full.width() - gap) / 2.0;
            for pi in 0..2 {
                let rect = Rect::from_min_size(pos2(full.left() + pi as f32 * (w + gap), full.top()), vec2(w, full.height()));
                self.panes[pi].rect = rect;
                ui.scope_builder(egui::UiBuilder::new().max_rect(rect), |ui| {
                    ui.set_clip_rect(rect.intersect(ui.clip_rect()));
                    self.ui_pane(ui, pi, &mut evs);
                });
                let stroke = if self.active == pi { Stroke::new(1.5, ACCENT) } else { Stroke::new(1.0, line_color(ui.visuals().dark_mode)) };
                ui.painter().rect_stroke(rect.expand(2.0), 4.0, stroke, egui::StrokeKind::Outside);
            }
        });

        self.finish_inline();

        // Clicking anywhere in a pane focuses it (before click events apply).
        if ctx.input(|i| i.pointer.any_pressed()) {
            if let Some(pos) = ctx.input(|i| i.pointer.interact_pos()) {
                if let Some(pi) = (0..2).find(|&i| self.panes[i].rect.contains(pos)) {
                    self.active = pi;
                }
            }
        }
        for ev in evs {
            self.apply(&ctx, ev);
        }
        self.finish_drag(&ctx);
        self.ui_palette(&ctx);
        self.ui_dialog(&ctx);
        self.ui_jobs(&ctx);
        if self.quit {
            ctx.send_viewport_cmd(egui::ViewportCommand::Close);
        }
    }

    fn on_exit(&mut self) {
        self.save_state();
    }
}

impl App {
    fn apply(&mut self, ctx: &Context, ev: Ev) {
        if !matches!(ev, Ev::DropHover(_)) {
            self.rename_timer = None; // any other interaction cancels a pending rename
        }
        match ev {
            Ev::Click { pi, vi, ctrl, shift } => {
                // A plain click on the row that already has the cursor, in an
                // already-active pane, starts a rename unless a double-click follows.
                let slow_click = {
                    let t = self.panes[pi].tab();
                    !ctrl
                        && !shift
                        && self.prev_active == pi
                        && self.inline.is_none()
                        && vi == t.cursor
                        && t.view.get(vi).map(|&i| &t.entries[i]).is_some_and(|e| {
                            !e.is_up && (t.selected.is_empty() || (t.selected.len() == 1 && t.selected.contains(&e.path)))
                        })
                };
                if slow_click {
                    let t = self.panes[pi].tab();
                    let path = t.entries[t.view[vi]].path.clone();
                    self.rename_timer = Some((pi, t.path.clone(), path, Instant::now()));
                    ctx.request_repaint_after(Duration::from_millis(480));
                }
                self.active = pi;
                let t = self.panes[pi].tab_mut();
                if vi >= t.view.len() {
                    return;
                }
                if shift {
                    if !ctrl {
                        t.selected.clear();
                    }
                    let a = t.anchor;
                    t.select_range(a, vi);
                    t.cursor = vi;
                } else if ctrl {
                    t.toggle_at(vi);
                    t.cursor = vi;
                    t.anchor = vi;
                } else {
                    t.selected.clear();
                    t.cursor = vi;
                    t.anchor = vi;
                }
            }
            Ev::Open { pi, vi } => {
                self.active = pi;
                self.panes[pi].tab_mut().set_cursor(vi);
                self.run(ctx, Action::Open);
            }
            Ev::MiddleOpen { pi, vi } => {
                self.active = pi;
                self.panes[pi].tab_mut().set_cursor(vi);
                self.run(ctx, Action::OpenInNewTab);
            }
            Ev::ContextAt { pi, vi } => {
                self.active = pi;
                let t = self.panes[pi].tab_mut();
                if let Some(&i) = t.view.get(vi) {
                    if !t.selected.contains(&t.entries[i].path) {
                        t.selected.clear();
                    }
                    t.cursor = vi;
                }
            }
            Ev::Sort(pi, k) => self.panes[pi].tab_mut().set_sort(k),
            Ev::Tab(pi, i) => {
                self.active = pi;
                self.panes[pi].active = i;
                self.dirty = true;
            }
            Ev::CloseTab(pi, i) => {
                let pane = &mut self.panes[pi];
                if pane.tabs.len() > 1 {
                    let t = pane.tabs.remove(i);
                    self.closed_tabs.push((pi, t.path));
                    let pane = &mut self.panes[pi];
                    if pane.active > i || pane.active >= pane.tabs.len() {
                        pane.active = pane.active.saturating_sub(1).min(pane.tabs.len() - 1);
                    }
                    self.dirty = true;
                }
            }
            Ev::TabMenu(pi, i, cmd) => {
                self.active = pi;
                let Some(path) = self.panes[pi].tabs.get(i).map(|t| t.path.clone()) else { return };
                match cmd {
                    TabCmd::Close => self.apply(ctx, Ev::CloseTab(pi, i)),
                    TabCmd::Others => self.close_tabs_where(pi, i, |_| true),
                    TabCmd::Left => self.close_tabs_where(pi, i, |j| j < i),
                    TabCmd::Right => self.close_tabs_where(pi, i, |j| j > i),
                    TabCmd::Duplicate => {
                        self.panes[pi].active = i;
                        self.open_new_tab(pi, path);
                    }
                    TabCmd::OpenOther => {
                        let o = 1 - pi;
                        self.open_new_tab(o, path);
                        self.active = o;
                    }
                    TabCmd::MoveOther => {
                        if self.panes[pi].tabs.len() > 1 {
                            self.apply(ctx, Ev::CloseTab(pi, i));
                            self.closed_tabs.pop(); // moved, not closed
                            let o = 1 - pi;
                            self.open_new_tab(o, path);
                            self.active = o;
                        }
                    }
                    TabCmd::CopyPath => ctx.copy_text(path.display().to_string()),
                }
                self.dirty = true;
            }
            Ev::NewTab(pi) => {
                self.active = pi;
                self.run(ctx, Action::NewTab);
            }
            Ev::Go(pi, p) => {
                self.active = pi;
                self.navigate(pi, p);
            }
            Ev::GoTyped(pi, s) => {
                self.active = pi;
                let base = self.panes[pi].tab().path.clone();
                match parse_user_path(&s, &base) {
                    Some(p) if p.is_dir() || crate::archive::split(&p).is_some_and(|(z, i)| i.is_empty() || crate::archive::list(&z, &i).is_ok_and(|v| !v.is_empty())) => {
                        self.navigate(pi, p);
                    }
                    Some(p) if p.is_file() => {
                        if let (Some(parent), Some(name)) = (p.parent(), p.file_name()) {
                            if self.navigate(pi, parent.to_path_buf()) {
                                self.panes[pi].tab_mut().reload(Some(name.to_string_lossy().into_owned()));
                            }
                        }
                    }
                    Some(p) => self.say(format!("⚠ Path not found: {}", p.display())),
                    None => {}
                }
            }
            Ev::DragStart { pi, paths } => {
                self.active = pi;
                self.drag = Some(Drag { from: pi, paths });
            }
            Ev::SetSearch(o) => {
                self.store.search = o;
                for p in &mut self.panes {
                    for t in &mut p.tabs {
                        t.search = o;
                        if !t.filter.is_empty() {
                            t.rebuild_view(None);
                            t.set_cursor(0);
                        }
                    }
                }
                self.dirty = true;
            }
            Ev::SetExclude(s) => {
                self.store.exclude = s.clone();
                for p in &mut self.panes {
                    for t in &mut p.tabs {
                        t.exclude = s.clone();
                    }
                }
                self.dirty = true;
            }
            Ev::TabDrag(pi, i) => self.tab_drag = Some((pi, i)),
            Ev::TabRects(pi, r) => self.tab_rects[pi] = r,
            Ev::DropHover(p) => self.drop_hover = Some(p),
            Ev::Run(a) => self.run(ctx, a),
        }
        // Close path editors that asked for it.
        for p in &mut self.panes {
            if p.path_edit.as_ref().is_some_and(|e| e.text == "\u{0}close") {
                p.path_edit = None;
            }
        }
    }

    /// Dragging a tab onto the other pane opens that folder there too
    /// (hold Shift to move the tab instead of duplicating it).
    fn finish_tab_drag(&mut self, ctx: &Context) {
        let Some((from, idx)) = self.tab_drag else { return };
        let Some(src) = self.panes[from].tabs.get(idx) else {
            self.tab_drag = None;
            return;
        };
        let path = src.path.clone();
        let pos = ctx.input(|i| i.pointer.latest_pos());
        let target = pos.and_then(|p| self.panes.iter().position(|pn| pn.rect.contains(p))).filter(|&t| t != from);
        // Reordering inside the same tab strip: where would the tab be inserted?
        let slots = self.tab_rects[from].clone();
        let insert_at = match (pos, slots.first()) {
            (Some(p), Some(first)) if target.is_none() && p.y >= first.top() - 4.0 && p.y <= first.bottom() + 4.0 && self.panes[from].rect.contains(p) => {
                Some(slots.iter().filter(|r| r.center().x < p.x).count())
            }
            _ => None,
        };
        if let (Some(at), Some(first)) = (insert_at, slots.first()) {
            let x = if at < slots.len() { slots[at].left() - 2.0 } else { slots.last().map_or(first.left(), |r| r.right() + 2.0) };
            let painter = ctx.layer_painter(egui::LayerId::new(egui::Order::Tooltip, egui::Id::new("tab-insert-mark")));
            painter.line_segment([pos2(x, first.top()), pos2(x, first.bottom())], Stroke::new(2.5, ACCENT));
        }
        if let Some(pos) = pos {
            let painter = ctx.layer_painter(egui::LayerId::new(egui::Order::Tooltip, egui::Id::new("tab-drag-label")));
            let shift = ctx.input(|i| i.modifiers.shift);
            let label = format!("{}{}", display_name(&path), if target.is_some() { if shift { "  → move here" } else { "  → open here too" } } else { "" });
            let galley = painter.layout_no_wrap(label, FontId::proportional(13.0), Color32::WHITE);
            let r = Rect::from_min_size(pos + vec2(14.0, 14.0), galley.size() + vec2(12.0, 6.0));
            painter.rect_filled(r, 6.0, ACCENT.gamma_multiply(0.92));
            painter.galley(r.min + vec2(6.0, 3.0), galley, Color32::WHITE);
            if let Some(t) = target {
                painter.rect_stroke(self.panes[t].rect.expand(2.0), 4.0, Stroke::new(2.0, ACCENT), egui::StrokeKind::Outside);
            }
        }
        if ctx.input(|i| i.pointer.any_released()) {
            self.tab_drag = None;
            if let Some(at) = insert_at {
                let pane = &mut self.panes[from];
                if at != idx && at != idx + 1 && idx < pane.tabs.len() {
                    let t = pane.tabs.remove(idx);
                    let ins = if at > idx { at - 1 } else { at };
                    pane.tabs.insert(ins, t);
                    pane.active = if pane.active == idx {
                        ins
                    } else if idx < pane.active && ins >= pane.active {
                        pane.active - 1
                    } else if idx > pane.active && ins <= pane.active {
                        pane.active + 1
                    } else {
                        pane.active
                    };
                    self.dirty = true;
                }
            }
            if let Some(t) = target {
                let shift = ctx.input(|i| i.modifiers.shift);
                self.open_new_tab(t, path);
                self.active = t;
                if shift && self.panes[from].tabs.len() > 1 {
                    let pane = &mut self.panes[from];
                    pane.tabs.remove(idx);
                    if pane.active > idx || pane.active >= pane.tabs.len() {
                        pane.active = pane.active.saturating_sub(1).min(pane.tabs.len() - 1);
                    }
                }
                self.dirty = true;
            }
        }
    }

    fn finish_drag(&mut self, ctx: &Context) {
        self.finish_tab_drag(ctx);
        for p in &mut self.panes {
            if p.path_edit.as_ref().is_some_and(|e| e.text == "\u{0}close") {
                p.path_edit = None;
            }
        }
        let Some(d) = &self.drag else { return };
        let pos = ctx.input(|i| i.pointer.latest_pos());
        if let Some(pos) = pos {
            let painter = ctx.layer_painter(egui::LayerId::new(egui::Order::Tooltip, egui::Id::new("drag-label")));
            let label = if d.paths.len() == 1 { display_name(&d.paths[0]) } else { format!("{} items", d.paths.len()) };
            let galley = painter.layout_no_wrap(label, FontId::proportional(13.0), Color32::WHITE);
            let r = Rect::from_min_size(pos + vec2(14.0, 14.0), galley.size() + vec2(12.0, 6.0));
            painter.rect_filled(r, 4.0, ACCENT.gamma_multiply(0.9));
            painter.galley(r.min + vec2(6.0, 3.0), galley, Color32::WHITE);
            if let Some(pi) = self.panes.iter().position(|p| p.rect.contains(pos)) {
                painter.rect_stroke(self.panes[pi].rect.expand(2.0), 4.0, Stroke::new(2.0, ACCENT), egui::StrokeKind::Outside);
            }
        }
        if ctx.input(|i| i.pointer.any_released()) {
            let d = self.drag.take().unwrap();
            let Some(pos) = pos else { return };
            let Some(pi) = self.panes.iter().position(|p| p.rect.contains(pos)) else { return };
            let dest = self.drop_hover.take().unwrap_or_else(|| self.panes[pi].tab().path.clone());
            let same_dir = d.paths.iter().all(|p| p.parent() == Some(dest.as_path()));
            if same_dir || d.paths.iter().any(|p| p == &dest) {
                return;
            }
            let (ctrl, shift) = ctx.input(|i| (i.modifiers.ctrl, i.modifiers.shift));
            let mv = shift || (!ctrl && same_volume(&d.paths[0], &dest));
            let _ = d.from;
            self.start_op(ctx, if mv { OpKind::Move } else { OpKind::Copy }, d.paths, Some(dest));
        }
    }
}

#[allow(dead_code)]
fn _unused(_: Pos2) {}
