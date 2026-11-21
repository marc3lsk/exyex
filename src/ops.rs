//! File operations running on a worker thread with progress + cancellation.

use eframe::egui;
use std::{
    fs,
    io::{Read, Write},
    time::{Duration, Instant},
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
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

#[derive(Clone, PartialEq)]
pub enum JobState {
    Running,
    Done,
    Cancelled,
    Failed(String),
}

pub struct Job {
    pub id: u64,
    pub kind: OpKind,
    /// e.g. "Copying 5 items"
    pub title: String,
    pub rx: Receiver<Msg>,
    pub cancel: Arc<AtomicBool>,
    pub cancelling: bool,
    pub done: u64,
    pub total: u64,
    pub current: String,
    pub dest: Option<PathBuf>,
    pub started: Instant,
    pub finished: Option<Instant>,
    pub state: JobState,
}

static NEXT_ID: AtomicU64 = AtomicU64::new(1);

pub fn spawn(kind: OpKind, srcs: Vec<PathBuf>, dest: Option<PathBuf>, ctx: egui::Context) -> Job {
    let (tx, rx) = channel();
    let cancel = Arc::new(AtomicBool::new(false));
    let c2 = cancel.clone();
    let d2 = dest.clone();
    let what = match srcs.as_slice() {
        [one] => one.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
        many => format!("{} items", many.len()),
    };
    let title = format!("{} {}", kind.verb(), what);
    std::thread::spawn(move || {
        let r = run(kind, &srcs, d2.as_deref(), &tx, &c2, &ctx);
        let _ = tx.send(Msg::Done(r));
        ctx.request_repaint();
    });
    Job {
        id: NEXT_ID.fetch_add(1, Ordering::Relaxed),
        kind,
        title,
        rx,
        cancel,
        cancelling: false,
        done: 0,
        total: 0,
        current: String::new(),
        dest,
        started: Instant::now(),
        finished: None,
        state: JobState::Running,
    }
}

struct Progress<'a> {
    tx: &'a Sender<Msg>,
    ctx: &'a egui::Context,
    done: u64,
    total: u64,
    last: Instant,
}

impl Progress<'_> {
    fn send(&mut self, current: &Path) {
        self.last = Instant::now();
        let _ = self.tx.send(Msg::Progress {
            done: self.done,
            total: self.total,
            current: current.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
        });
        self.ctx.request_repaint();
    }

    fn advance(&mut self, n: u64, current: &Path) {
        self.done += n;
        self.send(current);
    }

    /// Progress inside a big file: reported at most every 100 ms.
    fn advance_chunk(&mut self, n: u64, current: &Path) {
        self.done += n;
        if self.last.elapsed() >= Duration::from_millis(100) {
            self.send(current);
        }
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
            let mut pr = Progress { tx, ctx, done: 0, total: srcs.len() as u64, last: Instant::now() };
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
            let mut pr = Progress { tx, ctx, done: 0, total: srcs.iter().map(|s| tree_size(s)).sum(), last: Instant::now() };
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
    if cancel.load(Ordering::Relaxed) {
        return Err("Cancelled".into());
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
        copy_file(src, dst, md.len(), pr, cancel)?;
    }
    Ok(())
}

/// Small files use the OS copy; big ones are copied in 1 MiB chunks so the
/// progress window moves and Cancel reacts quickly.
fn copy_file(src: &Path, dst: &Path, len: u64, pr: &mut Progress, cancel: &AtomicBool) -> Result<(), String> {
    const BIG: u64 = 16 * 1024 * 1024;
    if len < BIG {
        fs::copy(src, dst).map_err(|e| e.to_string())?;
        pr.advance(len.max(1), src);
        return Ok(());
    }
    let mut input = fs::File::open(src).map_err(|e| e.to_string())?;
    let mut output = fs::File::create(dst).map_err(|e| e.to_string())?;
    let mut buf = vec![0u8; 1 << 20];
    loop {
        if cancel.load(Ordering::Relaxed) {
            drop(output);
            let _ = fs::remove_file(dst);
            return Err("Cancelled".into());
        }
        let n = match input.read(&mut buf) {
            Ok(0) => break,
            Ok(n) => n,
            Err(e) => {
                drop(output);
                let _ = fs::remove_file(dst);
                return Err(e.to_string());
            }
        };
        if let Err(e) = output.write_all(&buf[..n]) {
            drop(output);
            let _ = fs::remove_file(dst);
            return Err(e.to_string());
        }
        pr.advance_chunk(n as u64, src);
    }
    if let Ok(t) = input.metadata().and_then(|m| m.modified()) {
        let _ = output.set_modified(t); // keep the original modification time
    }
    pr.send(src);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    fn temp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("exyex-test-{}-{}", std::process::id(), name));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    fn wait(job: &Job) -> (Result<(), String>, u64) {
        let mut last_done = 0;
        loop {
            match job.rx.recv_timeout(Duration::from_secs(60)).expect("job timed out") {
                Msg::Progress { done, .. } => last_done = done,
                Msg::Done(r) => return (r, last_done),
            }
        }
    }

    #[test]
    fn big_file_is_copied_in_chunks_with_progress_and_mtime() {
        let dir = temp("big");
        let src = dir.join("big.bin");
        let data: Vec<u8> = (0..20 * 1024 * 1024u32).map(|i| (i % 251) as u8).collect();
        fs::write(&src, &data).unwrap();
        let old = std::time::SystemTime::now() - Duration::from_secs(86_400 * 30);
        fs::File::options().write(true).open(&src).unwrap().set_modified(old).unwrap();
        let dest = dir.join("out");
        fs::create_dir_all(&dest).unwrap();

        let job = spawn(OpKind::Copy, vec![src.clone()], Some(dest.clone()), egui::Context::default());
        let (r, last_done) = wait(&job);
        assert_eq!(r, Ok(()));
        assert_eq!(last_done, data.len() as u64, "progress should reach the full size");
        let copied = dest.join("big.bin");
        assert_eq!(fs::read(&copied).unwrap(), data);
        let m = fs::metadata(&copied).unwrap().modified().unwrap();
        let diff = m.duration_since(old).unwrap_or_else(|e| e.duration());
        assert!(diff < Duration::from_secs(3), "modification time should be preserved");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn cancelled_chunked_copy_leaves_no_partial_file() {
        let dir = temp("cancel");
        let src = dir.join("big.bin");
        fs::write(&src, vec![7u8; 20 * 1024 * 1024]).unwrap();
        let dst = dir.join("copy.bin");
        let (tx, _rx) = channel();
        let ctx = egui::Context::default();
        let mut pr = Progress { tx: &tx, ctx: &ctx, done: 0, total: 1, last: Instant::now() };
        let cancel = AtomicBool::new(true);
        let r = copy_file(&src, &dst, 20 * 1024 * 1024, &mut pr, &cancel);
        assert_eq!(r, Err("Cancelled".to_string()));
        assert!(!dst.exists(), "partial file must be removed");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn name_conflicts_get_a_number_and_folders_copy_recursively() {
        let dir = temp("tree");
        let src = dir.join("a");
        fs::create_dir_all(src.join("sub")).unwrap();
        fs::write(src.join("sub").join("f.txt"), "hi").unwrap();
        let dest = dir.join("dest");
        fs::create_dir_all(dest.join("a")).unwrap(); // conflict
        let job = spawn(OpKind::Copy, vec![src.clone()], Some(dest.clone()), egui::Context::default());
        assert_eq!(wait(&job).0, Ok(()));
        assert_eq!(fs::read_to_string(dest.join("a (2)").join("sub").join("f.txt")).unwrap(), "hi");
        assert_eq!(job.title, "Copying a");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn delete_job_reports_progress_and_removes() {
        let dir = temp("del");
        let f = dir.join("x.txt");
        fs::write(&f, "x").unwrap();
        let job = spawn(OpKind::Delete, vec![f.clone()], None, egui::Context::default());
        assert_eq!(wait(&job).0, Ok(()));
        assert!(!f.exists());
        let _ = fs::remove_dir_all(&dir);
    }
}
