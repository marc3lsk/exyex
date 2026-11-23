//! File operations running on a worker thread with progress + cancellation.

use eframe::egui;
use std::{
    collections::HashMap,
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

/// What to do when the target of a copy / move already exists.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Policy {
    /// keep both: `name (2).ext`
    Rename,
    Overwrite,
    Skip,
    /// overwrite only when the source is newer
    Newer,
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
    spawn_with(kind, srcs, dest, HashMap::new(), ctx)
}

/// `policies` maps a source path to what to do when its target exists (default: keep both).
pub fn spawn_with(kind: OpKind, srcs: Vec<PathBuf>, dest: Option<PathBuf>, policies: HashMap<PathBuf, Policy>, ctx: egui::Context) -> Job {
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
        let r = run(kind, &srcs, d2.as_deref(), &policies, &tx, &c2, &ctx);
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
    /// merging into an existing folder: only replace files that are older than the source
    newer_only: bool,
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

/// Where `src` goes inside `dest` under `policy`; `None` = leave it alone.
fn target_for(dest: &Path, src: &Path, name: &std::ffi::OsStr, policy: Policy) -> Option<PathBuf> {
    let t = dest.join(name);
    if !t.exists() {
        return Some(t);
    }
    if t == src {
        return Some(unique_dest(dest, name));
    }
    match policy {
        Policy::Rename => Some(unique_dest(dest, name)),
        Policy::Overwrite => Some(t),
        Policy::Skip => None,
        Policy::Newer => {
            if t.is_dir() {
                return Some(t); // merged; files are compared one by one
            }
            let newer = || -> Option<bool> { Some(fs::metadata(src).ok()?.modified().ok()? > fs::metadata(&t).ok()?.modified().ok()?) };
            if newer().unwrap_or(true) { Some(t) } else { None }
        }
    }
}

fn run(
    kind: OpKind,
    srcs: &[PathBuf],
    dest: Option<&Path>,
    policies: &HashMap<PathBuf, Policy>,
    tx: &Sender<Msg>,
    cancel: &AtomicBool,
    ctx: &egui::Context,
) -> Result<(), String> {
    use crate::archive as ar;
    // Writes inside an archive work on its staging copy (the archive is rewritten when the user leaves it).
    let mut kind = kind;
    let mut srcs: Vec<PathBuf> = srcs.to_vec();
    let mut dest_buf: Option<PathBuf> = dest.map(Path::to_path_buf);
    if let Some(d) = dest_buf.as_deref().filter(|d| ar::in_archive(d)) {
        let _ = tx.send(Msg::Progress { done: 0, total: 1, current: "Preparing archive…".into() });
        dest_buf = Some(ar::real_for_write(d)?);
    }
    if matches!(kind, OpKind::Trash | OpKind::Delete | OpKind::Move) && srcs.iter().any(|s| ar::split_inner(s).is_some()) {
        let _ = tx.send(Msg::Progress { done: 0, total: 1, current: "Preparing archive…".into() });
        for s in &mut srcs {
            if ar::split_inner(s).is_some() {
                *s = ar::real_for_write(s)?;
            }
        }
        if kind == OpKind::Trash {
            kind = OpKind::Delete; // there is no recycle bin inside an archive
        }
    }
    let srcs = &srcs[..];
    let dest = dest_buf.as_deref();
    let mut errors: Vec<String> = vec![];
    match kind {
        OpKind::Trash => {
            let _ = tx.send(Msg::Progress { done: 0, total: 1, current: format!("{} item(s)", srcs.len()) });
            if srcs.iter().any(|s| crate::sftp::is_remote(s)) {
                return Err("Remote files can't go to the Recycle Bin – use permanent delete".into());
            }
            trash::delete_all(srcs).map_err(|e| e.to_string())?;
        }
        OpKind::Delete => {
            let mut pr = Progress { tx, ctx, done: 0, total: srcs.len() as u64, last: Instant::now(), newer_only: false };
            for s in srcs {
                if cancel.load(Ordering::Relaxed) {
                    return Err("Cancelled".into());
                }
                let r = if crate::sftp::is_remote(s) {
                    crate::sftp::remove_all(s)
                } else {
                    match fs::symlink_metadata(s) {
                        Ok(m) if m.is_dir() => fs::remove_dir_all(s),
                        Ok(_) => fs::remove_file(s),
                        Err(e) => Err(e),
                    }
                };
                if let Err(e) = r {
                    errors.push(format!("{}: {}", s.display(), e));
                }
                pr.advance(1, s);
            }
        }
        OpKind::Copy | OpKind::Move => {
            let dest = dest.ok_or("no destination")?;
            let src_size = |s: &PathBuf| {
                if crate::sftp::is_remote(s) {
                    return crate::sftp::size_of(s);
                }
                match crate::archive::split_inner(s) {
                    Some((z, i)) => crate::archive::size(&z, &i),
                    None => tree_size(s),
                }
            };
            let mut pr = Progress { tx, ctx, done: 0, total: srcs.iter().map(src_size).sum(), last: Instant::now(), newer_only: false };
            for s in srcs {
                if cancel.load(Ordering::Relaxed) {
                    return Err("Cancelled".into());
                }
                let Some(name) = s.file_name() else { continue };
                let policy = policies.get(s).copied().unwrap_or(Policy::Rename);
                pr.newer_only = policy == Policy::Newer;
                let (rs, rd) = (crate::sftp::is_remote(s), crate::sftp::is_remote(dest));
                if rs || rd {
                    if rs && rd {
                        errors.push(format!("{}: copying between two servers is not supported (copy to a local folder first)", s.display()));
                        continue;
                    }
                    let local_target = if rs { target_for(dest, s, name, policy) } else { None };
                    if rs && local_target.is_none() {
                        pr.advance(src_size(s), s); // skipped
                        continue;
                    }
                    let mut sink = |n: u64| pr.advance(n, s);
                    let r = if rs {
                        let target = local_target.unwrap();
                        crate::sftp::download(s, &target, cancel, &mut sink).and_then(|_| {
                            if kind == OpKind::Move { crate::sftp::remove_all(s).map_err(|e| format!("copied, but could not remove source: {e}")) } else { Ok(()) }
                        })
                    } else {
                        let target = crate::sftp::unique_remote(dest, name);
                        crate::sftp::upload(s, &target, cancel, &mut sink).and_then(|_| {
                            if kind == OpKind::Move { remove_any(s).map_err(|e| format!("copied, but could not remove source: {e}")) } else { Ok(()) }
                        })
                    };
                    if let Err(e) = r {
                        if e == "Cancelled" {
                            return Err(e);
                        }
                        errors.push(format!("{}: {}", s.display(), e));
                    }
                    continue;
                }
                if let Some((zip, inner)) = crate::archive::split_inner(s) {
                    // copying out of an archive = extracting
                    if kind == OpKind::Move {
                        errors.push(format!("{}: archives are read-only", s.display()));
                        continue;
                    }
                    let Some(target) = target_for(dest, s, name, policy) else {
                        pr.advance(src_size(s), s); // skipped
                        continue;
                    };
                    let mut sink = |n: u64| pr.advance(n, s);
                    if let Err(e) = crate::archive::extract(&zip, &inner, &target, cancel, &mut sink) {
                        errors.push(format!("{}: {}", s.display(), e));
                    }
                    continue;
                }
                if kind == OpKind::Move && s.parent() == Some(dest) {
                    continue; // already there
                }
                if dest.starts_with(s) {
                    errors.push(format!("{}: cannot place a folder inside itself", s.display()));
                    continue;
                }
                let size = tree_size(s);
                let Some(target) = target_for(dest, s, name, policy) else {
                    pr.advance(size, s); // skipped, the source stays
                    continue;
                };
                let r = if kind == OpKind::Move {
                    match fs::rename(s, &target) {
                        Ok(()) => {
                            pr.advance(size, s);
                            Ok(())
                        }
                        Err(_) => copy_tree(s, &target, &mut pr, cancel)
                            .and_then(|_| {
                                if pr.newer_only && s.is_dir() {
                                    return Ok(()); // older files were skipped and must stay in the source folder
                                }
                                remove_any(s).map_err(|e| format!("copied, but could not remove source: {e}"))
                            }),
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
        if pr.newer_only && dst.exists() {
            let newer = md.modified().ok().zip(fs::metadata(dst).and_then(|m| m.modified()).ok()).is_none_or(|(a, b)| a > b);
            if !newer {
                pr.advance(md.len().max(1), src);
                return Ok(());
            }
        }
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
        let mut pr = Progress { tx: &tx, ctx: &ctx, done: 0, total: 1, last: Instant::now(), newer_only: false };
        let cancel = AtomicBool::new(true);
        let r = copy_file(&src, &dst, 20 * 1024 * 1024, &mut pr, &cancel);
        assert_eq!(r, Err("Cancelled".to_string()));
        assert!(!dst.exists(), "partial file must be removed");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn conflict_policies_overwrite_skip_and_newer() {
        let dir = temp("policy");
        let (src, dest) = (dir.join("src"), dir.join("dest"));
        fs::create_dir_all(&src).unwrap();
        fs::create_dir_all(&dest).unwrap();
        let old = std::time::SystemTime::now() - Duration::from_secs(86_400);
        for n in ["over.txt", "skip.txt", "old.txt"] {
            fs::write(src.join(n), "new").unwrap();
            fs::write(dest.join(n), "existing").unwrap();
        }
        // old.txt: the existing target is newer than the source
        fs::File::options().write(true).open(src.join("old.txt")).unwrap().set_modified(old).unwrap();
        let srcs: Vec<PathBuf> = ["over.txt", "skip.txt", "old.txt"].iter().map(|n| src.join(n)).collect();
        let policies = HashMap::from([(srcs[0].clone(), Policy::Overwrite), (srcs[1].clone(), Policy::Skip), (srcs[2].clone(), Policy::Newer)]);
        let job = spawn_with(OpKind::Copy, srcs, Some(dest.clone()), policies, egui::Context::default());
        assert_eq!(wait(&job).0, Ok(()));
        assert_eq!(fs::read_to_string(dest.join("over.txt")).unwrap(), "new");
        assert_eq!(fs::read_to_string(dest.join("skip.txt")).unwrap(), "existing");
        assert_eq!(fs::read_to_string(dest.join("old.txt")).unwrap(), "existing");
        assert!(!dest.join("over (2).txt").exists(), "no extra copies");
        // a skipped move leaves the source in place
        let job = spawn_with(OpKind::Move, vec![src.join("skip.txt")], Some(dest.clone()), HashMap::from([(src.join("skip.txt"), Policy::Skip)]), egui::Context::default());
        assert_eq!(wait(&job).0, Ok(()));
        assert!(src.join("skip.txt").exists());
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

    #[test]
    fn copy_into_move_out_of_and_delete_inside_an_archive() {
        let dir = temp("zipops");
        let zp = dir.join("z.zip");
        {
            let mut w = zip::ZipWriter::new(fs::File::create(&zp).unwrap());
            let o = zip::write::SimpleFileOptions::default();
            w.start_file("old.txt", o).unwrap();
            w.write_all(b"old").unwrap();
            w.start_file("gone.txt", o).unwrap();
            w.write_all(b"bye").unwrap();
            w.finish().unwrap();
        }
        let new = dir.join("new.txt");
        fs::write(&new, "new").unwrap();
        let ctx = egui::Context::default;
        // copy a file into the archive
        assert_eq!(wait(&spawn(OpKind::Copy, vec![new.clone()], Some(zp.clone()), ctx())).0, Ok(()));
        assert!(crate::archive::is_dirty(&zp));
        // delete (recycle bin request becomes a permanent delete inside the archive)
        assert_eq!(wait(&spawn(OpKind::Trash, vec![zp.join("gone.txt")], None, ctx())).0, Ok(()));
        // move a file out of the archive
        let out = dir.join("out");
        fs::create_dir_all(&out).unwrap();
        assert_eq!(wait(&spawn(OpKind::Move, vec![zp.join("old.txt")], Some(out.clone()), ctx())).0, Ok(()));
        assert_eq!(fs::read_to_string(out.join("old.txt")).unwrap(), "old");
        crate::archive::save(&zp).unwrap();
        let names: Vec<_> = crate::archive::list(&zp, "").unwrap().into_iter().map(|e| e.name).collect();
        assert_eq!(names, vec!["new.txt".to_string()]);
        let _ = fs::remove_dir_all(&dir);
    }
}
