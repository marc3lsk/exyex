//! File operations running on a worker thread with progress + cancellation.

use eframe::egui;
use std::{
    fs,
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{Receiver, Sender, channel},
    },
};

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum OpKind {
    Copy,
    Move,
    Trash,
    Delete,
}

impl OpKind {
    pub fn verb(self) -> &'static str {
        match self {
            OpKind::Copy => "Copying",
            OpKind::Move => "Moving",
            OpKind::Trash => "Recycling",
            OpKind::Delete => "Deleting",
        }
    }
}

pub enum Msg {
    Progress { done: u64, total: u64, current: String },
    Done(Result<(), String>),
}

pub struct Job {
    pub kind: OpKind,
    pub rx: Receiver<Msg>,
    pub cancel: Arc<AtomicBool>,
    pub done: u64,
    pub total: u64,
    pub current: String,
    pub dest: Option<PathBuf>,
}

pub fn spawn(kind: OpKind, srcs: Vec<PathBuf>, dest: Option<PathBuf>, ctx: egui::Context) -> Job {
    let (tx, rx) = channel();
    let cancel = Arc::new(AtomicBool::new(false));
    let c2 = cancel.clone();
    let d2 = dest.clone();
    std::thread::spawn(move || {
        let r = run(kind, &srcs, d2.as_deref(), &tx, &c2, &ctx);
        let _ = tx.send(Msg::Done(r));
        ctx.request_repaint();
    });
    Job { kind, rx, cancel, done: 0, total: 0, current: String::new(), dest }
}

struct Progress<'a> {
    tx: &'a Sender<Msg>,
    ctx: &'a egui::Context,
    done: u64,
    total: u64,
}

impl Progress<'_> {
    fn advance(&mut self, n: u64, current: &Path) {
        self.done += n;
        let _ = self.tx.send(Msg::Progress {
            done: self.done,
            total: self.total,
            current: current.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
        });
        self.ctx.request_repaint();
    }
}

fn tree_size(p: &Path) -> u64 {
    match fs::symlink_metadata(p) {
        Ok(m) if m.is_dir() => fs::read_dir(p)
            .map(|rd| rd.flatten().map(|e| tree_size(&e.path())).sum::<u64>())
            .unwrap_or(0)
            .max(1),
        Ok(m) => m.len().max(1),
        Err(_) => 1,
    }
}

/// `name.ext` -> `name (2).ext` until it does not exist.
pub fn unique_dest(dir: &Path, name: &std::ffi::OsStr) -> PathBuf {
    let first = dir.join(name);
    if !first.exists() {
        return first;
    }
    let as_path = Path::new(name);
    let stem = as_path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
    let ext = as_path.extension().map(|e| format!(".{}", e.to_string_lossy())).unwrap_or_default();
    (2..)
        .map(|n| dir.join(format!("{stem} ({n}){ext}")))
        .find(|p| !p.exists())
        .unwrap()
}

fn run(
    kind: OpKind,
    srcs: &[PathBuf],
    dest: Option<&Path>,
    tx: &Sender<Msg>,
    cancel: &AtomicBool,
    ctx: &egui::Context,
) -> Result<(), String> {
    let mut errors: Vec<String> = vec![];
    match kind {
        OpKind::Trash => {
            let _ = tx.send(Msg::Progress { done: 0, total: 1, current: format!("{} item(s)", srcs.len()) });
            trash::delete_all(srcs).map_err(|e| e.to_string())?;
        }
        OpKind::Delete => {
            let mut pr = Progress { tx, ctx, done: 0, total: srcs.len() as u64 };
            for s in srcs {
                if cancel.load(Ordering::Relaxed) {
                    return Err("Cancelled".into());
                }
                let r = match fs::symlink_metadata(s) {
                    Ok(m) if m.is_dir() => fs::remove_dir_all(s),
                    Ok(_) => fs::remove_file(s),
                    Err(e) => Err(e),
                };
                if let Err(e) = r {
                    errors.push(format!("{}: {}", s.display(), e));
                }
                pr.advance(1, s);
            }
        }
        OpKind::Copy | OpKind::Move => {
            let dest = dest.ok_or("no destination")?;
            let mut pr = Progress { tx, ctx, done: 0, total: srcs.iter().map(|s| tree_size(s)).sum() };
            for s in srcs {
                if cancel.load(Ordering::Relaxed) {
                    return Err("Cancelled".into());
                }
                let Some(name) = s.file_name() else { continue };
                if kind == OpKind::Move && s.parent() == Some(dest) {
                    continue; // already there
                }
                if dest.starts_with(s) {
                    errors.push(format!("{}: cannot place a folder inside itself", s.display()));
                    continue;
                }
                let target = unique_dest(dest, name);
                let size = tree_size(s);
                let r = if kind == OpKind::Move {
                    match fs::rename(s, &target) {
                        Ok(()) => {
                            pr.advance(size, s);
                            Ok(())
                        }
                        Err(_) => copy_tree(s, &target, &mut pr, cancel)
                            .and_then(|_| remove_any(s).map_err(|e| format!("copied, but could not remove source: {e}"))),
                    }
                } else {
                    copy_tree(s, &target, &mut pr, cancel)
                };
                if let Err(e) = r {
                    errors.push(format!("{}: {}", s.display(), e));
                }
            }
        }
    }
    if errors.is_empty() {
        Ok(())
    } else {
        let n = errors.len();
        let mut msg = errors.into_iter().take(3).collect::<Vec<_>>().join("; ");
        if n > 3 {
            msg.push_str(&format!(" (+{} more)", n - 3));
        }
        Err(msg)
    }
}

fn remove_any(p: &Path) -> std::io::Result<()> {
    if fs::symlink_metadata(p)?.is_dir() { fs::remove_dir_all(p) } else { fs::remove_file(p) }
}

fn copy_tree(src: &Path, dst: &Path, pr: &mut Progress, cancel: &AtomicBool) -> Result<(), String> {
    if cancel.load(Ordering::Relaxed) {
        return Err("Cancelled".into());
    }
    let md = fs::symlink_metadata(src).map_err(|e| e.to_string())?;
    if md.is_dir() {
        fs::create_dir_all(dst).map_err(|e| e.to_string())?;
        for e in fs::read_dir(src).map_err(|e| e.to_string())?.flatten() {
            copy_tree(&e.path(), &dst.join(e.file_name()), pr, cancel)?;
        }
        pr.advance(1, src);
    } else {
        fs::copy(src, dst).map_err(|e| e.to_string())?;
        pr.advance(md.len().max(1), src);
    }
    Ok(())
}
