//! Recursive ("search in subfolders") walker. Runs on a background thread and
//! streams matching entries back in batches; the name of each result is its
//! path relative to the searched folder, e.g. `src\ui\README.md`.

use crate::model::{Entry, read_dir};
use crate::search::{SearchOpts, compile, glob_match};
use std::{
    collections::VecDeque,
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{Receiver, Sender, channel},
    },
    time::{Duration, Instant},
};

/// Hard cap so a too-broad query cannot flood the list.
pub const MAX_RESULTS: usize = 50_000;

pub enum DeepMsg {
    Batch(Vec<Entry>),
    Done { truncated: bool },
}

pub struct DeepJob {
    pub rx: Receiver<DeepMsg>,
    pub cancel: Arc<AtomicBool>,
    pub running: bool,
    pub truncated: bool,
}

impl Drop for DeepJob {
    fn drop(&mut self) {
        self.cancel.store(true, Ordering::Relaxed);
    }
}

/// Splits the manual exclude list: names / globs separated by `,` or `;`.
pub fn parse_exclude(s: &str) -> Vec<String> {
    s.split([',', ';']).map(|p| p.trim().to_string()).filter(|p| !p.is_empty()).collect()
}

#[cfg(windows)]
fn is_reparse(p: &std::path::Path) -> bool {
    use std::os::windows::fs::MetadataExt;
    std::fs::symlink_metadata(p).is_ok_and(|m| m.file_attributes() & 0x400 != 0)
}
#[cfg(not(windows))]
fn is_reparse(p: &std::path::Path) -> bool {
    std::fs::symlink_metadata(p).is_ok_and(|m| m.file_type().is_symlink())
}

pub fn spawn(root: PathBuf, query: String, opts: SearchOpts, exclude: Vec<String>, show_hidden: bool) -> DeepJob {
    let (tx, rx) = channel();
    let cancel = Arc::new(AtomicBool::new(false));
    let c = cancel.clone();
    std::thread::spawn(move || walk(root, query, opts, exclude, show_hidden, c, tx));
    DeepJob { rx, cancel, running: true, truncated: false }
}

fn walk(root: PathBuf, query: String, opts: SearchOpts, exclude: Vec<String>, show_hidden: bool, cancel: Arc<AtomicBool>, tx: Sender<DeepMsg>) {
    let matcher = compile(&query, opts);
    let mut queue: VecDeque<(PathBuf, String)> = VecDeque::new();
    queue.push_back((root, String::new()));
    let mut batch: Vec<Entry> = Vec::new();
    let mut total = 0usize;
    let mut last = Instant::now();
    let mut truncated = false;
    'outer: while let Some((dir, prefix)) = queue.pop_front() {
        let Ok(items) = read_dir(&dir) else { continue };
        for mut e in items {
            if cancel.load(Ordering::Relaxed) {
                return;
            }
            if !show_hidden && e.hidden {
                continue;
            }
            if exclude.iter().any(|x| glob_match(x, &e.name, false)) {
                continue;
            }
            let rel = format!("{prefix}{}", e.name);
            if e.is_dir && !is_reparse(&e.path) {
                queue.push_back((e.path.clone(), format!("{rel}\\")));
            }
            if matcher.score(&rel).is_some() {
                e.name = rel;
                e.deep = true;
                batch.push(e);
                total += 1;
                if total >= MAX_RESULTS {
                    truncated = true;
                    break 'outer;
                }
            }
            if !batch.is_empty() && (batch.len() >= 500 || last.elapsed() >= Duration::from_millis(80)) {
                if tx.send(DeepMsg::Batch(std::mem::take(&mut batch))).is_err() {
                    return;
                }
                last = Instant::now();
            }
        }
    }
    if !batch.is_empty() && tx.send(DeepMsg::Batch(batch)).is_err() {
        return;
    }
    let _ = tx.send(DeepMsg::Done { truncated });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run(root: &std::path::Path, q: &str, exclude: &str) -> Vec<String> {
        let job = spawn(root.to_path_buf(), q.into(), SearchOpts::default(), parse_exclude(exclude), true);
        let mut out = vec![];
        while let Ok(m) = job.rx.recv() {
            match m {
                DeepMsg::Batch(v) => out.extend(v.into_iter().map(|e| e.name)),
                DeepMsg::Done { .. } => break,
            }
        }
        out.sort();
        out
    }

    #[test]
    fn finds_nested_and_honours_exclude() {
        let root = std::env::temp_dir().join(format!("exyex-deep-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(root.join("a").join("b")).unwrap();
        std::fs::create_dir_all(root.join("node_modules").join("x")).unwrap();
        std::fs::write(root.join("a").join("b").join("note.txt"), "x").unwrap();
        std::fs::write(root.join("node_modules").join("x").join("note.txt"), "x").unwrap();
        std::fs::write(root.join("note.md"), "x").unwrap();

        assert_eq!(run(&root, "note", ""), vec!["a\\b\\note.txt", "node_modules\\x\\note.txt", "note.md"]);
        assert_eq!(run(&root, "note", "node_modules"), vec!["a\\b\\note.txt", "note.md"]);
        assert_eq!(run(&root, "note", "node_*, *.md"), vec!["a\\b\\note.txt"]);
        let _ = std::fs::remove_dir_all(&root);
    }
}
