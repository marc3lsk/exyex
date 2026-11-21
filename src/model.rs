//! Directory listing, sorting, filtering and per-tab navigation state.

use crate::search::{Matcher, SearchOpts, compile};
use serde::{Deserialize, Serialize};
use std::{
    cmp::Ordering,
    collections::HashSet,
    path::{Component, Path, PathBuf},
    time::SystemTime,
};

pub const ROW_H: f32 = 22.0;

#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
pub enum SortKey {
    Name,
    Ext,
    Size,
    Modified,
}

#[derive(Clone)]
pub struct Entry {
    pub name: String,
    pub path: PathBuf,
    pub is_dir: bool,
    pub size: u64,
    pub modified: Option<SystemTime>,
    pub hidden: bool,
    /// The virtual ".." row that goes to the parent folder.
    pub is_up: bool,
    /// A result of the subfolder search (`name` is then the relative path).
    pub deep: bool,
    pub ext: String,
}

pub fn read_dir(p: &Path) -> std::io::Result<Vec<Entry>> {
    let mut out = Vec::new();
    for de in std::fs::read_dir(p)? {
        let Ok(de) = de else { continue };
        let name = de.file_name().to_string_lossy().into_owned();
        let path = de.path();
        let md = de.metadata().ok();
        let mut is_dir = md.as_ref().is_some_and(|m| m.is_dir());
        if md.as_ref().is_some_and(|m| m.file_type().is_symlink()) {
            is_dir = path.is_dir();
        }
        #[cfg(windows)]
        let hidden = md.as_ref().is_some_and(|m| {
            use std::os::windows::fs::MetadataExt;
            m.file_attributes() & 0x6 != 0 // HIDDEN | SYSTEM
        });
        #[cfg(not(windows))]
        let hidden = name.starts_with('.');
        let ext = if is_dir {
            String::new()
        } else {
            path.extension()
                .map(|e| e.to_string_lossy().to_lowercase())
                .unwrap_or_default()
        };
        out.push(Entry {
            size: if is_dir { 0 } else { md.as_ref().map_or(0, |m| m.len()) },
            modified: md.as_ref().and_then(|m| m.modified().ok()),
            name,
            path,
            is_dir,
            hidden,
            is_up: false,
            deep: false,
            ext,
        });
    }
    Ok(out)
}

/// Appends the virtual ".." entry (if the folder has a parent).
pub fn with_up(mut v: Vec<Entry>, path: &Path) -> Vec<Entry> {
    if let Some(parent) = path.parent() {
        v.push(Entry {
            name: "..".into(),
            path: parent.to_path_buf(),
            is_dir: true,
            size: 0,
            modified: None,
            hidden: false,
            is_up: true,
            deep: false,
            ext: String::new(),
        });
    }
    v
}

pub fn dir_mtime(p: &Path) -> Option<SystemTime> {
    std::fs::metadata(p).ok()?.modified().ok()
}

/// Case-insensitive "natural" comparison: `file2` < `file10`.
pub fn natural_cmp(a: &str, b: &str) -> Ordering {
    let mut ai = a.chars().peekable();
    let mut bi = b.chars().peekable();
    loop {
        match (ai.peek().copied(), bi.peek().copied()) {
            (None, None) => return Ordering::Equal,
            (None, _) => return Ordering::Less,
            (_, None) => return Ordering::Greater,
            (Some(x), Some(y)) if x.is_ascii_digit() && y.is_ascii_digit() => {
                let mut na = String::new();
                while let Some(c) = ai.peek().copied().filter(char::is_ascii_digit) {
                    na.push(c);
                    ai.next();
                }
                let mut nb = String::new();
                while let Some(c) = bi.peek().copied().filter(char::is_ascii_digit) {
                    nb.push(c);
                    bi.next();
                }
                let (ta, tb) = (na.trim_start_matches('0'), nb.trim_start_matches('0'));
                let o = ta.len().cmp(&tb.len()).then_with(|| ta.cmp(tb));
                if o != Ordering::Equal {
                    return o;
                }
            }
            (Some(x), Some(y)) => {
                let o = x.to_lowercase().cmp(y.to_lowercase());
                if o != Ordering::Equal {
                    return o;
                }
                ai.next();
                bi.next();
            }
        }
    }
}

// ---------------------------------------------------------------- filtering

// ------------------------------------------------------------ path helpers

/// Lexically resolves `.` and `..` without touching the disk.
pub fn lexical_normalize(p: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for c in p.components() {
        match c {
            Component::ParentDir => {
                if !matches!(out.components().next_back(), Some(Component::RootDir | Component::Prefix(_)) | None) {
                    out.pop();
                }
            }
            Component::CurDir => {}
            c => out.push(c.as_os_str()),
        }
    }
    out
}

fn expand_env(s: &str) -> String {
    let mut out = String::new();
    let mut rest = s;
    while let Some(i) = rest.find('%') {
        out.push_str(&rest[..i]);
        let after = &rest[i + 1..];
        if let Some(j) = after.find('%') {
            let name = &after[..j];
            match std::env::var(name) {
                Ok(v) if !name.is_empty() => out.push_str(&v),
                _ => {
                    out.push('%');
                    out.push_str(name);
                    out.push('%');
                }
            }
            rest = &after[j + 1..];
        } else {
            out.push('%');
            rest = after;
        }
    }
    out.push_str(rest);
    out
}

/// Turns whatever the user typed or pasted into a path: strips quotes,
/// expands `%VARS%`, `~`, `file:///` URLs, and converts `/` to `\`.
pub fn parse_user_path(input: &str, base: &Path) -> Option<PathBuf> {
    let mut s = input.trim().trim_matches(|c| c == '"' || c == '\'').trim().to_string();
    if s.is_empty() {
        return None;
    }
    if let Some(r) = s.strip_prefix("file:///") {
        s = r.replace("%20", " ");
    }
    s = expand_env(&s);
    if s == "~" || s.starts_with("~/") || s.starts_with("~\\") {
        let home = dirs::home_dir()?;
        s = format!("{}{}", home.display(), &s[1..]);
    }
    if cfg!(windows) {
        s = s.replace('/', "\\");
        if s.len() == 2 && s.ends_with(':') {
            s.push('\\');
        }
    }
    let p = PathBuf::from(&s);
    let p = if p.is_absolute() || p.has_root() { p } else { base.join(p) };
    Some(lexical_normalize(&p))
}

pub fn display_name(p: &Path) -> String {
    p.file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_else(|| p.to_string_lossy().trim_end_matches('\\').to_string())
}

// -------------------------------------------------------------------- tabs

pub struct Tab {
    pub path: PathBuf,
    pub back: Vec<PathBuf>,
    pub fwd: Vec<PathBuf>,
    pub entries: Vec<Entry>,
    pub view: Vec<usize>,
    pub cursor: usize,
    pub anchor: usize,
    pub selected: HashSet<PathBuf>,
    pub filter: String,
    pub filter_open: bool,
    pub search: SearchOpts,
    pub matcher: Matcher,
    pub exclude: String,
    pub deep_res: Vec<Entry>,
    pub deep_job: Option<crate::deep::DeepJob>,
    deep_key: String,
    pub sort: SortKey,
    pub asc: bool,
    pub show_hidden: bool,
    pub error: Option<String>,
    pub scroll_y: f32,
    pub view_h: f32,
    pub want_scroll: Option<f32>,
    pub mtime: Option<SystemTime>,
    pub last_poll: std::time::Instant,
}

impl Tab {
    pub fn new(path: PathBuf, show_hidden: bool) -> Self {
        let mut t = Tab {
            path,
            back: vec![],
            fwd: vec![],
            entries: vec![],
            view: vec![],
            cursor: 0,
            anchor: 0,
            selected: HashSet::new(),
            filter: String::new(),
            filter_open: false,
            search: SearchOpts::default(),
            matcher: Matcher::default(),
            exclude: String::new(),
            deep_res: vec![],
            deep_job: None,
            deep_key: String::new(),
            sort: SortKey::Name,
            asc: true,
            show_hidden,
            error: None,
            scroll_y: 0.0,
            view_h: 0.0,
            want_scroll: None,
            mtime: None,
            last_poll: std::time::Instant::now(),
        };
        t.reload(None);
        t
    }

    pub fn title(&self) -> String {
        display_name(&self.path)
    }

    pub fn has_up(&self) -> bool {
        self.entries.iter().any(|e| e.is_up)
    }

    pub fn current(&self) -> Option<&Entry> {
        self.view.get(self.cursor).map(|&i| &self.entries[i])
    }

    /// Selected entries, or the entry under the cursor if nothing is selected.
    pub fn targets(&self) -> Vec<PathBuf> {
        if self.selected.is_empty() {
            self.current().filter(|e| !e.is_up).map(|e| vec![e.path.clone()]).unwrap_or_default()
        } else {
            self.view
                .iter()
                .map(|&i| &self.entries[i])
                .filter(|e| !e.is_up && self.selected.contains(&e.path))
                .map(|e| e.path.clone())
                .collect()
        }
    }

    /// Re-reads the directory. `focus` selects the cursor entry by name;
    /// `None` keeps the cursor on whatever it was on.
    pub fn reload(&mut self, focus: Option<String>) {
        let keep = focus.or_else(|| self.current().map(|e| e.name.clone()));
        match read_dir(&self.path) {
            Ok(v) => {
                self.entries = with_up(v, &self.path);
                self.error = None;
            }
            Err(e) => {
                self.entries = with_up(vec![], &self.path);
                self.error = Some(e.to_string());
            }
        }
        self.mtime = dir_mtime(&self.path);
        if !self.deep_res.is_empty() {
            self.entries.extend(self.deep_res.iter().cloned());
        }
        let names: HashSet<&PathBuf> = self.entries.iter().map(|e| &e.path).collect();
        self.selected.retain(|p| names.contains(p));
        self.rebuild_view(keep);
    }

    pub fn rebuild_view(&mut self, keep: Option<String>) {
        let keep = keep.or_else(|| self.current().map(|e| e.name.clone()));
        let (key, asc) = (self.sort, self.asc);
        self.matcher = compile(&self.filter, self.search);
        self.sync_deep();
        let deep = self.deep_active();
        let ranked = self.matcher.ranked();
        let mut scored: Vec<(i32, usize)> = (0..self.entries.len())
            .filter(|&i| !self.entries[i].is_up && self.entries[i].deep == deep && (self.show_hidden || !self.entries[i].hidden))
            .filter_map(|i| self.matcher.score(&self.entries[i].name).map(|sc| (sc, i)))
            .collect();
        let entries = &self.entries;
        if ranked {
            // fuzzy: best match first (ties: shorter / natural name order)
            scored.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| natural_cmp(&entries[a.1].name, &entries[b.1].name)));
        } else {
            scored.sort_by(|&(_, a), &(_, b)| {
                let (x, y) = (&entries[a], &entries[b]);
                y.is_dir.cmp(&x.is_dir).then_with(|| {
                    let by_name = natural_cmp(&x.name, &y.name);
                    let o = match key {
                        SortKey::Name => by_name,
                        SortKey::Ext => x.ext.cmp(&y.ext).then(by_name),
                        SortKey::Size => {
                            if x.is_dir {
                                by_name
                            } else {
                                x.size.cmp(&y.size).then(by_name)
                            }
                        }
                        SortKey::Modified => x.modified.cmp(&y.modified).then(by_name),
                    };
                    if asc { o } else { o.reverse() }
                })
            });
        }
        let mut idx: Vec<usize> = scored.into_iter().map(|(_, i)| i).collect();
        // the ".." row only shows while not searching
        if self.matcher.is_all() {
            if let Some(up) = self.entries.iter().position(|e| e.is_up) {
                idx.insert(0, up);
            }
        }
        self.view = idx;
        self.cursor = keep
            .and_then(|n| self.view.iter().position(|&i| self.entries[i].name == n))
            .unwrap_or(self.cursor)
            .min(self.view.len().saturating_sub(1));
        self.anchor = self.cursor;
        self.ensure_visible();
    }

    /// Subfolder search is on and there is something to look for.
    pub fn deep_active(&self) -> bool {
        self.search.deep && !self.filter.is_empty()
    }

    /// (running, truncated) while a subfolder search is active.
    pub fn deep_status(&self) -> Option<(bool, bool)> {
        self.deep_job.as_ref().filter(|_| self.deep_active()).map(|j| (j.running, j.truncated))
    }

    /// Starts / restarts / stops the background walk when the query changed.
    fn sync_deep(&mut self) {
        let key = if self.deep_active() {
            format!("{}|{:?}|{}|{}", self.filter, self.search, self.exclude, self.show_hidden)
        } else {
            String::new()
        };
        if key == self.deep_key {
            return;
        }
        self.deep_key = key;
        self.deep_job = None; // drop cancels the old walk
        self.deep_res.clear();
        self.entries.retain(|e| !e.deep);
        if self.deep_active() {
            self.deep_job = Some(crate::deep::spawn(
                self.path.clone(),
                self.filter.clone(),
                self.search,
                crate::deep::parse_exclude(&self.exclude),
                self.show_hidden,
            ));
        }
    }

    /// Takes the results found so far. Returns true while the walk is still running.
    pub fn poll_deep(&mut self) -> bool {
        use crate::deep::DeepMsg;
        let Some(job) = self.deep_job.as_mut() else { return false };
        let mut got = Vec::new();
        while let Ok(m) = job.rx.try_recv() {
            match m {
                DeepMsg::Batch(v) => got.extend(v),
                DeepMsg::Done { truncated } => {
                    job.running = false;
                    job.truncated = truncated;
                }
            }
        }
        let running = job.running;
        if !got.is_empty() {
            self.deep_res.extend(got.iter().cloned());
            self.entries.extend(got);
            self.rebuild_view(None);
        }
        running
    }

    pub fn set_sort(&mut self, key: SortKey) {
        if self.sort == key {
            self.asc = !self.asc;
        } else {
            self.sort = key;
            // newest / biggest first by default
            self.asc = !matches!(key, SortKey::Modified | SortKey::Size);
        }
        self.rebuild_view(None);
    }

    pub fn ensure_visible(&mut self) {
        let top = self.cursor as f32 * ROW_H;
        let bot = top + ROW_H;
        if top < self.scroll_y {
            self.want_scroll = Some(top);
        } else if self.view_h > 0.0 && bot > self.scroll_y + self.view_h {
            self.want_scroll = Some(bot - self.view_h);
        }
    }

    // --- navigation

    fn commit(&mut self, to: PathBuf, entries: Vec<Entry>, focus: Option<String>) {
        self.path = to;
        self.entries = entries;
        self.error = None;
        self.selected.clear();
        self.filter.clear();
        self.filter_open = false;
        self.cursor = 0;
        self.scroll_y = 0.0;
        self.want_scroll = Some(0.0);
        self.mtime = dir_mtime(&self.path);
        self.rebuild_view(focus.or(Some(String::new())));
        self.ensure_visible();
    }

    fn load(to: &Path) -> Result<Vec<Entry>, String> {
        read_dir(to).map(|v| with_up(v, to)).map_err(|e| format!("{}: {}", to.display(), e))
    }

    pub fn go(&mut self, to: PathBuf) -> Result<(), String> {
        self.go_focus(to, None)
    }

    /// Like `go`, but puts the cursor on `focus` (a name in the target folder).
    pub fn go_focus(&mut self, to: PathBuf, focus: Option<String>) -> Result<(), String> {
        if to == self.path {
            if focus.is_some() {
                self.filter.clear();
                self.filter_open = false;
            }
            self.reload(focus);
            return Ok(());
        }
        let entries = Self::load(&to)?;
        let focus = focus.or_else(|| {
            if self.path.parent() == Some(to.as_path()) {
                self.path.file_name().map(|n| n.to_string_lossy().into_owned())
            } else {
                None
            }
        });
        let old = std::mem::replace(&mut self.path, PathBuf::new());
        self.back.push(old);
        self.fwd.clear();
        self.commit(to, entries, focus);
        Ok(())
    }

    pub fn up(&mut self) -> Result<(), String> {
        match self.path.parent().map(Path::to_path_buf) {
            Some(p) => self.go(p),
            None => Ok(()),
        }
    }

    pub fn go_back(&mut self) -> Result<(), String> {
        let Some(to) = self.back.pop() else { return Ok(()) };
        match Self::load(&to) {
            Ok(entries) => {
                let old = std::mem::replace(&mut self.path, PathBuf::new());
                self.fwd.push(old);
                self.commit(to, entries, None);
                Ok(())
            }
            Err(e) => {
                self.back.push(to);
                Err(e)
            }
        }
    }

    pub fn go_forward(&mut self) -> Result<(), String> {
        let Some(to) = self.fwd.pop() else { return Ok(()) };
        match Self::load(&to) {
            Ok(entries) => {
                let old = std::mem::replace(&mut self.path, PathBuf::new());
                self.back.push(old);
                self.commit(to, entries, None);
                Ok(())
            }
            Err(e) => {
                self.fwd.push(to);
                Err(e)
            }
        }
    }

    // --- cursor / selection

    pub fn set_cursor(&mut self, i: usize) {
        if self.view.is_empty() {
            return;
        }
        self.cursor = i.min(self.view.len() - 1);
        self.ensure_visible();
    }

    pub fn move_cursor(&mut self, delta: isize, extend: bool) {
        if self.view.is_empty() {
            return;
        }
        let old = self.cursor;
        let n = (self.cursor as isize + delta).clamp(0, self.view.len() as isize - 1) as usize;
        self.set_cursor(n);
        if extend {
            self.select_range(old, n);
        } else {
            self.anchor = n;
        }
    }

    fn path_at(&self, vi: usize) -> PathBuf {
        self.entries[self.view[vi]].path.clone()
    }

    pub fn select_range(&mut self, a: usize, b: usize) {
        let (lo, hi) = (a.min(b), a.max(b));
        for vi in lo..=hi.min(self.view.len().saturating_sub(1)) {
            if self.entries[self.view[vi]].is_up {
                continue;
            }
            let p = self.path_at(vi);
            self.selected.insert(p);
        }
    }

    pub fn toggle_at(&mut self, vi: usize) {
        if vi >= self.view.len() || self.entries[self.view[vi]].is_up {
            return;
        }
        let p = self.path_at(vi);
        if !self.selected.remove(&p) {
            self.selected.insert(p);
        }
    }

    pub fn select_all(&mut self) {
        self.selected = self.view.iter().filter(|&&i| !self.entries[i].is_up).map(|&i| self.entries[i].path.clone()).collect();
    }

    pub fn invert(&mut self) {
        let all: HashSet<PathBuf> = self.view.iter().filter(|&&i| !self.entries[i].is_up).map(|&i| self.entries[i].path.clone()).collect();
        self.selected = all.difference(&self.selected).cloned().collect();
    }

    /// Type-ahead: jump to the next entry starting with `prefix`
    /// (falls back to "contains").
    pub fn jump_to_prefix(&mut self, prefix: &str) {
        let p = prefix.to_lowercase();
        let find = |pred: &dyn Fn(&str) -> bool, from: usize| {
            (0..self.view.len())
                .map(|k| (from + k) % self.view.len())
                .find(|&vi| pred(&self.entries[self.view[vi]].name.to_lowercase()))
        };
        if self.view.is_empty() {
            return;
        }
        let hit = find(&|n| n.starts_with(&p), self.cursor).or_else(|| find(&|n| n.contains(&p), self.cursor));
        if let Some(vi) = hit {
            self.set_cursor(vi);
            self.anchor = vi;
        }
    }

    pub fn selection_summary(&self) -> String {
        let total_files = self.view.iter().filter(|&&i| !self.entries[i].is_dir).count();
        let total_dirs = self.view.iter().filter(|&&i| self.entries[i].is_dir && !self.entries[i].is_up).count();
        if self.selected.is_empty() {
            format!("{} folders, {} files", total_dirs, total_files)
        } else {
            let size: u64 = self
                .view
                .iter()
                .map(|&i| &self.entries[i])
                .filter(|e| self.selected.contains(&e.path))
                .map(|e| e.size)
                .sum();
            format!("{} selected · {}", self.selected.len(), fmt_size(size))
        }
    }
}

// ---------------------------------------------------------------- formatting

pub fn fmt_size(n: u64) -> String {
    const U: [&str; 5] = ["B", "KB", "MB", "GB", "TB"];
    if n < 1024 {
        return format!("{} B", n);
    }
    let mut v = n as f64;
    let mut i = 0;
    while v >= 1024.0 && i < U.len() - 1 {
        v /= 1024.0;
        i += 1;
    }
    format!("{:.1} {}", v, U[i])
}

pub fn fmt_time(t: Option<SystemTime>) -> String {
    match t {
        Some(t) => chrono::DateTime::<chrono::Local>::from(t).format("%Y-%m-%d %H:%M").to_string(),
        None => String::new(),
    }
}
