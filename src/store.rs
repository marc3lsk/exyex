//! Persistent state: favorites, folder visit history (frecency), session.

use crate::model::SortKey;
use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    path::PathBuf,
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Serialize, Deserialize, Default, Clone)]
pub struct Visit {
    pub count: u32,
    pub last: u64,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct TabSession {
    pub path: String,
    pub sort: SortKey,
    pub asc: bool,
}

#[derive(Serialize, Deserialize, Clone, Default)]
pub struct PaneSession {
    pub tabs: Vec<TabSession>,
    pub active: usize,
}

#[derive(Serialize, Deserialize, Default, Clone, Copy, PartialEq, Eq, Debug)]
pub enum ThemeChoice {
    #[default]
    System,
    Light,
    Dark,
}

impl ThemeChoice {
    pub fn next(self) -> Self {
        match self {
            ThemeChoice::System => ThemeChoice::Light,
            ThemeChoice::Light => ThemeChoice::Dark,
            ThemeChoice::Dark => ThemeChoice::System,
        }
    }
    pub fn label(self) -> &'static str {
        match self {
            ThemeChoice::System => "System",
            ThemeChoice::Light => "Light",
            ThemeChoice::Dark => "Dark",
        }
    }
}

#[derive(Serialize, Deserialize, Default, Clone, Copy, PartialEq, Eq, Debug)]
pub enum ViewMode {
    #[default]
    Details,
    Compact,
    Grid,
}

impl ViewMode {
    pub fn next(self) -> Self {
        match self {
            ViewMode::Details => ViewMode::Compact,
            ViewMode::Compact => ViewMode::Grid,
            ViewMode::Grid => ViewMode::Details,
        }
    }
    pub fn label(self) -> &'static str {
        match self {
            ViewMode::Details => "Details",
            ViewMode::Compact => "Compact",
            ViewMode::Grid => "Grid (thumbnails)",
        }
    }
}

/// Which detail columns are shown (the name column is always there).
#[derive(Serialize, Deserialize, Clone, Copy, PartialEq, Eq, Debug)]
#[serde(default)]
pub struct Cols {
    pub ext: bool,
    pub size: bool,
    pub date: bool,
}

impl Default for Cols {
    fn default() -> Self {
        Cols { ext: false, size: true, date: true }
    }
}

/// Remembered window placement (outer position, inner size).
#[derive(Serialize, Deserialize, Clone, Copy, PartialEq, Debug)]
pub struct WinGeom {
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
    #[serde(default)]
    pub maximized: bool,
}

impl WinGeom {
    /// The window's title bar must still be reachable (monitors can disappear).
    #[cfg(windows)]
    pub fn on_screen(&self) -> bool {
        use windows::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN};
        // SAFETY: plain metric queries
        let (vx, vy, vw, vh) = unsafe {
            (
                GetSystemMetrics(SM_XVIRTUALSCREEN) as f32,
                GetSystemMetrics(SM_YVIRTUALSCREEN) as f32,
                GetSystemMetrics(SM_CXVIRTUALSCREEN) as f32,
                GetSystemMetrics(SM_CYVIRTUALSCREEN) as f32,
            )
        };
        let (cx, cy) = (self.x + self.w / 2.0, self.y + 16.0);
        self.w >= 200.0 && self.h >= 120.0 && cx >= vx && cx <= vx + vw && cy >= vy && cy <= vy + vh
    }
    #[cfg(not(windows))]
    pub fn on_screen(&self) -> bool {
        true
    }
}

#[derive(Serialize, Deserialize, Default)]
pub struct Store {
    /// SFTP connection aliases: `user@host:port` -> short name.
    #[serde(default)]
    pub aliases: HashMap<String, String>,
    /// F4 editor command (empty = Notepad); see `editor.rs`.
    #[serde(default)]
    pub editor: String,
    /// Per-extension editor rules, one `ext1,ext2 = command` per line.
    #[serde(default)]
    pub editor_rules: String,
    /// Theme of the text-compare window (`None` = same as the main window).
    #[serde(default)]
    pub diff_dark: Option<bool>,
    #[serde(default)]
    pub view: ViewMode,
    #[serde(default)]
    pub cols: Cols,
    #[serde(default)]
    pub window: Option<WinGeom>,
    #[serde(default)]
    pub jobs_win: Option<WinGeom>,
    #[serde(default)]
    pub theme: ThemeChoice,
    #[serde(default)]
    pub search: crate::search::SearchOpts,
    /// Manual exclude list for subfolder search (names / globs, `,`-separated).
    #[serde(default)]
    pub exclude: String,
    pub favorites: Vec<String>,
    pub visits: HashMap<String, Visit>,
    pub panes: Vec<PaneSession>,
    pub active_pane: usize,
    pub show_hidden: bool,
}

fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_secs())
}

/// `%APPDATA%\mavyex`; a settings folder left by the former name (`exyex`) is moved there once.
pub fn config_dir() -> Option<PathBuf> {
    let base = dirs::config_dir()?;
    let (new, old) = (base.join("mavyex"), base.join("exyex"));
    if !new.exists() && old.is_dir() {
        let _ = std::fs::rename(&old, &new);
    }
    Some(new)
}

fn file() -> Option<PathBuf> {
    Some(config_dir()?.join("state.json"))
}

impl Store {
    pub fn load() -> Store {
        file()
            .and_then(|f| std::fs::read_to_string(f).ok())
            .and_then(|s| serde_json::from_str::<Store>(&s).ok())
            .map(|mut st| {
                st.merge_case_duplicates();
                st
            })
            .unwrap_or_default()
    }

    /// Folds visit entries that differ only by letter case (c:/x vs C:/x) into one.
    fn merge_case_duplicates(&mut self) {
        let mut merged: HashMap<String, (String, Visit)> = HashMap::new();
        for (k, v) in self.visits.drain() {
            let e = merged.entry(k.to_lowercase()).or_insert_with(|| (k.clone(), Visit::default()));
            if v.last >= e.1.last {
                e.0 = k; // keep the spelling used most recently
            }
            e.1.count += v.count;
            e.1.last = e.1.last.max(v.last);
        }
        self.visits = merged.into_values().collect();
    }

    pub fn save(&self) {
        let Some(f) = file() else { return };
        if let Some(d) = f.parent() {
            let _ = std::fs::create_dir_all(d);
        }
        if let Ok(s) = serde_json::to_string_pretty(self) {
            let tmp = f.with_extension("tmp");
            if std::fs::write(&tmp, s).is_ok() {
                let _ = std::fs::rename(&tmp, &f);
            }
        }
    }

    pub fn record_visit(&mut self, p: &std::path::Path) {
        let key = p.to_string_lossy().trim_end_matches('\\').to_string();
        let key = if key.ends_with(':') { format!("{key}\\") } else { key };
        // Windows paths are case-insensitive: reuse an existing entry that differs only by case
        let existing = self.visits.keys().find(|k| k.eq_ignore_ascii_case(&key)).cloned();
        let v = self.visits.entry(existing.unwrap_or(key)).or_default();
        v.count += 1;
        v.last = now();
    }

    /// Folders ranked by how often they were opened (ties: most recent first).
    pub fn frecent(&self) -> Vec<(PathBuf, f64)> {
        let mut v: Vec<(&String, &Visit)> = self.visits.iter().collect();
        // most opened first; ties: most recently opened
        v.sort_by(|a, b| b.1.count.cmp(&a.1.count).then(b.1.last.cmp(&a.1.last)));
        v.into_iter().map(|(k, v)| (PathBuf::from(k), v.count as f64)).collect()
    }

    /// Removes a folder from the favorites list (returns whether it was there).
    pub fn remove_favorite(&mut self, p: &std::path::Path) -> bool {
        let s = p.to_string_lossy();
        let before = self.favorites.len();
        self.favorites.retain(|f| !f.eq_ignore_ascii_case(&s));
        self.favorites.len() != before
    }

    /// Forgets a folder's visit history so it no longer shows up as "recent".
    pub fn forget_visit(&mut self, p: &std::path::Path) -> bool {
        let s = p.to_string_lossy();
        let keys: Vec<String> = self.visits.keys().filter(|k| k.eq_ignore_ascii_case(&s)).cloned().collect();
        for k in &keys {
            self.visits.remove(k);
        }
        !keys.is_empty()
    }

    pub fn is_favorite(&self, p: &std::path::Path) -> bool {
        let s = p.to_string_lossy();
        self.favorites.iter().any(|f| f.eq_ignore_ascii_case(&s))
    }

    pub fn toggle_favorite(&mut self, p: &std::path::Path) -> bool {
        let s = p.to_string_lossy().into_owned();
        if let Some(i) = self.favorites.iter().position(|f| f.eq_ignore_ascii_case(&s)) {
            self.favorites.remove(i);
            false
        } else {
            self.favorites.push(s);
            true
        }
    }
}
