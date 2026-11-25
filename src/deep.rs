//! Recursive ("search in subfolders") walker. Runs on a background thread and
//! streams matching entries back in batches; the name of each result is its
//! path relative to the searched folder, e.g. `src\ui\README.md`.
//!
//! With `opts.content` the query is matched against the *lines* of every file
//! instead of its name (a small worker pool scans files in parallel).

use crate::model::{Entry, read_dir};
use crate::search::{Matcher, SearchOpts, compile, glob_match};
use std::{
    collections::VecDeque,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicUsize, Ordering},
        mpsc::{Receiver, Sender, channel, sync_channel},
    },
    time::{Duration, Instant},
};

/// Hard cap so a too-broad query cannot flood the list.
pub const MAX_RESULTS: usize = 50_000;
/// Larger files are not scanned for content.
const MAX_SCAN_BYTES: u64 = 8 * 1024 * 1024;
const SCAN_THREADS: usize = 4;

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

/// Scans one file; sends it (with a snippet) when some line matches.
fn scan_file(mut e: Entry, matcher: &Matcher, tx: &Sender<DeepMsg>, hits: &AtomicUsize, cancel: &AtomicBool, halt: &AtomicBool) {
    if e.size == 0 || e.size > MAX_SCAN_BYTES {
        return;
    }
    let Ok(bytes) = std::fs::read(&e.path) else { return };
    if bytes[..bytes.len().min(4096)].contains(&0) {
        return; // binary
    }
    let text = String::from_utf8_lossy(&bytes);
    let (mut count, mut first) = (0usize, None::<(usize, String)>);
    for (n, line) in text.lines().enumerate() {
        if n % 4096 == 0 && (cancel.load(Ordering::Relaxed) || halt.load(Ordering::Relaxed)) {
            return;
        }
        let long;
        let l = if line.len() > 2000 {
            long = line.chars().take(2000).collect::<String>();
            long.as_str()
        } else {
            line
        };
        if matcher.score(l).is_some() {
            count += 1;
            if first.is_none() {
                first = Some((n + 1, l.trim().chars().take(140).collect()));
            }
        }
    }
    if let Some((n, line)) = first {
        e.snippet = format!("{n}: {line}{}", if count > 1 { format!("   (+{} more)", count - 1) } else { String::new() });
        hits.fetch_add(1, Ordering::Relaxed);
        let _ = tx.send(DeepMsg::Batch(vec![e]));
    }
}

fn walk(root: PathBuf, query: String, opts: SearchOpts, exclude: Vec<String>, show_hidden: bool, cancel: Arc<AtomicBool>, tx: Sender<DeepMsg>) {
    let matcher = Arc::new(compile(&query, opts));
    // content mode needs a text pattern; with only `:filters` it just lists files
    let content = opts.content && !matcher.name_all();
    let hits = Arc::new(AtomicUsize::new(0));
    let halt = Arc::new(AtomicBool::new(false));

    let (ftx, frx) = sync_channel::<Entry>(512);
    let frx = Arc::new(Mutex::new(frx));
    let mut workers = vec![];
    if content {
        for _ in 0..SCAN_THREADS {
            let (frx, matcher, tx, hits, cancel, halt) = (frx.clone(), matcher.clone(), tx.clone(), hits.clone(), cancel.clone(), halt.clone());
            workers.push(std::thread::spawn(move || {
                loop {
                    let next = frx.lock().map(|r| r.recv());
                    let Ok(Ok(e)) = next else { break };
                    if cancel.load(Ordering::Relaxed) || halt.load(Ordering::Relaxed) {
                        break;
                    }
                    scan_file(e, &matcher, &tx, &hits, &cancel, &halt);
                }
            }));
        }
    }

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
            if !matcher.filters_ok(&e) {
                continue;
            }
            if content {
                if !e.is_dir {
                    e.name = rel;
                    e.deep = true;
                    if ftx.send(e).is_err() {
                        break 'outer;
                    }
                }
                if hits.load(Ordering::Relaxed) >= MAX_RESULTS {
                    truncated = true;
                    halt.store(true, Ordering::Relaxed);
                    break 'outer;
                }
                continue;
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
    drop(ftx);
    for w in workers {
        let _ = w.join();
    }
    if cancel.load(Ordering::Relaxed) {
        return;
    }
    if !batch.is_empty() && tx.send(DeepMsg::Batch(batch)).is_err() {
        return;
    }
    let _ = tx.send(DeepMsg::Done { truncated });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run_with(root: &std::path::Path, q: &str, exclude: &str, opts: SearchOpts) -> Vec<(String, String)> {
        let job = spawn(root.to_path_buf(), q.into(), opts, parse_exclude(exclude), true);
        let mut out = vec![];
        while let Ok(m) = job.rx.recv() {
            match m {
                DeepMsg::Batch(v) => out.extend(v.into_iter().map(|e| (e.name, e.snippet))),
                DeepMsg::Done { .. } => break,
            }
        }
        out.sort();
        out
    }

    fn run(root: &std::path::Path, q: &str, exclude: &str) -> Vec<String> {
        run_with(root, q, exclude, SearchOpts::default()).into_iter().map(|x| x.0).collect()
    }

    #[test]
    fn finds_nested_and_honours_exclude() {
        let root = std::env::temp_dir().join(format!("mavyex-deep-{}", std::process::id()));
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

    #[test]
    fn content_search_reports_first_line_and_skips_binary() {
        let root = std::env::temp_dir().join(format!("mavyex-grep-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(root.join("sub")).unwrap();
        std::fs::write(root.join("one.txt"), "alpha\nthe needle here\nneedle again\n").unwrap();
        std::fs::write(root.join("sub").join("two.rs"), "fn main() {}\n// NEEDLE\n").unwrap();
        std::fs::write(root.join("sub").join("bin.dat"), b"needle\0\0\0").unwrap();
        std::fs::write(root.join("none.txt"), "nothing").unwrap();

        let o = SearchOpts { content: true, ..Default::default() };
        let r = run_with(&root, "needle", "", o);
        assert_eq!(r.len(), 2, "{r:?}");
        assert_eq!(r[0], ("one.txt".to_string(), "2: the needle here   (+1 more)".to_string()));
        assert_eq!(r[1].0, "sub\\two.rs");
        assert!(r[1].1.starts_with("2: // NEEDLE"));
        // filters narrow the files that are scanned
        let r = run_with(&root, ":ext=rs needle", "", o);
        assert_eq!(r.iter().map(|x| x.0.as_str()).collect::<Vec<_>>(), vec!["sub\\two.rs"]);
        let _ = std::fs::remove_dir_all(&root);
    }
}
