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

#[derive(Serialize, Deserialize, Default)]
pub struct Store {
    #[serde(default)]
    pub theme: ThemeChoice,
    pub favorites: Vec<String>,
    pub visits: HashMap<String, Visit>,
    pub panes: Vec<PaneSession>,
    pub active_pane: usize,
    pub show_hidden: bool,
}

fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_secs())
}

fn file() -> Option<PathBuf> {
    Some(dirs::config_dir()?.join("exyex").join("state.json"))
}

impl Store {
    pub fn load() -> Store {
        file()
            .and_then(|f| std::fs::read_to_string(f).ok())
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default()
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
        let v = self.visits.entry(p.to_string_lossy().into_owned()).or_default();
        v.count += 1;
        v.last = now();
        if self.visits.len() > 800 {
            let mut all: Vec<(String, f64)> = self.visits.iter().map(|(k, v)| (k.clone(), score(v))).collect();
            all.sort_by(|a, b| a.1.total_cmp(&b.1));
            for (k, _) in all.into_iter().take(200) {
                self.visits.remove(&k);
            }
        }
    }

    /// Folders ranked by frecency (frequency weighted by recency).
    pub fn frecent(&self) -> Vec<(PathBuf, f64)> {
        let mut v: Vec<(PathBuf, f64)> = self.visits.iter().map(|(k, v)| (PathBuf::from(k), score(v))).collect();
        v.sort_by(|a, b| b.1.total_cmp(&a.1));
        v
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

fn score(v: &Visit) -> f64 {
    let age_days = now().saturating_sub(v.last) as f64 / 86400.0;
    (v.count as f64).sqrt() * 10.0 / (1.0 + age_days)
}
