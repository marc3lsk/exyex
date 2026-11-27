//! Archive support (zip/jar, tar, tar.gz/tgz, gz): an archive behaves like a folder. A path such as
//! `C:\x\a.zip\docs\readme.txt` is "virtual": the part up to `a.zip` exists on
//! disk, the rest lives inside the archive. A plain `.gz` holds one compressed file; it is shown as a
//! folder containing that file (named after the archive without `.gz`).
//!
//! Browsing reads the archive directly (tar/gz have no central directory, so their listing is built
//! by one pass over the stream and cached by mtime/size). The first *write* (copy in, delete, rename,
//! F4 edit ...) extracts the whole archive into a staging folder (an "edit session"); from then on
//! listing / reading use that folder. When the user leaves the archive, the app asks whether to write
//! the staging folder back (`save`).

use crate::model::Entry;
use std::{
    collections::{BTreeMap, HashMap},
    fs::File,
    io::{self, Read, Write},
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::SystemTime,
};
use zip::ZipArchive;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Kind {
    Zip,
    Tar,
    TarGz,
    Gz,
}

fn kind(p: &Path) -> Option<Kind> {
    let n = p.file_name()?.to_string_lossy().to_lowercase();
    if n.ends_with(".zip") || n.ends_with(".jar") {
        Some(Kind::Zip)
    } else if n.ends_with(".tar.gz") || n.ends_with(".tgz") {
        Some(Kind::TarGz)
    } else if n.ends_with(".tar") {
        Some(Kind::Tar)
    } else if n.ends_with(".gz") {
        Some(Kind::Gz)
    } else {
        None
    }
}

const GZ_ONE: &str = "A .gz file holds exactly one file – use .tar.gz (or .tgz) for folders and several files";

/// Extension says it is an archive we can browse (and write).
pub fn is_archive_name(p: &Path) -> bool {
    kind(p).is_some()
}

/// The name says single-file gzip (`.gz`, but not `.tar.gz`).
pub fn is_plain_gz(p: &Path) -> bool {
    kind(p) == Some(Kind::Gz)
}

/// Splits a virtual path into (archive file, path inside, using `/`).
/// `inner` is empty for the archive file itself.
pub fn split(p: &Path) -> Option<(PathBuf, String)> {
    let mut acc = PathBuf::new();
    let mut comps = p.components().peekable();
    while let Some(c) = comps.next() {
        acc.push(c.as_os_str());
        if is_archive_name(&acc) && acc.is_file() {
            let inner: Vec<String> = comps.map(|c| c.as_os_str().to_string_lossy().into_owned()).collect();
            return Some((acc, inner.join("/")));
        }
    }
    None
}

/// Path inside an archive (not the archive file itself).
pub fn split_inner(p: &Path) -> Option<(PathBuf, String)> {
    split(p).filter(|(_, i)| !i.is_empty())
}

pub fn in_archive(p: &Path) -> bool {
    split(p).is_some()
}

fn open(zip: &Path) -> io::Result<ZipArchive<File>> {
    ZipArchive::new(File::open(zip)?).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e.to_string()))
}

fn to_systime(d: Option<zip::DateTime>) -> Option<SystemTime> {
    use chrono::TimeZone;
    let d = d?;
    chrono::Local
        .with_ymd_and_hms(d.year() as i32, d.month() as u32, d.day() as u32, d.hour() as u32, d.minute() as u32, d.second() as u32)
        .single()
        .map(SystemTime::from)
}

fn virtual_path(zip: &Path, inner: &str) -> PathBuf {
    let mut p = zip.to_path_buf();
    for part in inner.split('/').filter(|s| !s.is_empty()) {
        p.push(part);
    }
    p
}

/// Normalizes a stored entry name: `/` separators, no `./`, no leading or trailing `/`.
/// Returns the name and whether it was marked as a folder (trailing `/`); `None` for empty names
/// and names with `..` (never shown or written outside the target).
fn clean(name: &str) -> Option<(String, bool)> {
    let n = name.replace('\\', "/");
    let is_dir = n.ends_with('/');
    let parts: Vec<&str> = n.split('/').filter(|p| !p.is_empty() && *p != ".").collect();
    if parts.is_empty() || parts.contains(&"..") {
        return None;
    }
    Some((parts.join("/"), is_dir))
}

/// Name of the single file inside a `.gz`: the archive name without `.gz`.
fn gz_name(arc: &Path) -> String {
    let n = arc.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let cut = n.len().saturating_sub(3);
    let stem = if n.is_char_boundary(cut) { &n[..cut] } else { "" };
    if stem.is_empty() { "data".into() } else { stem.into() }
}

/// Uncompressed size from the gzip trailer (ISIZE, modulo 4 GiB).
fn gz_size(p: &Path) -> Option<u64> {
    use std::io::{Seek, SeekFrom};
    let mut f = File::open(p).ok()?;
    if f.metadata().ok()?.len() < 18 {
        return None;
    }
    f.seek(SeekFrom::End(-4)).ok()?;
    let mut b = [0u8; 4];
    f.read_exact(&mut b).ok()?;
    Some(u32::from_le_bytes(b) as u64)
}

struct Meta<'a> {
    name: &'a str,
    is_dir: bool,
    size: u64,
    modified: Option<SystemTime>,
}

type Visit<'a> = dyn FnMut(&Meta, &mut dyn Read) -> Result<bool, String> + 'a;

/// Visits the entries of an archive in stored order (with a reader for the content);
/// the callback returns `false` to stop.
fn each(arc: &Path, f: &mut Visit) -> Result<(), String> {
    let es = |e: io::Error| e.to_string();
    match kind(arc).ok_or("not an archive")? {
        Kind::Zip => {
            let mut ar = open(arc).map_err(es)?;
            for i in 0..ar.len() {
                let mut z = ar.by_index(i).map_err(|e| e.to_string())?;
                let Some((name, d)) = clean(z.name()) else { continue };
                let m = Meta { name: &name, is_dir: d || z.is_dir(), size: z.size(), modified: to_systime(z.last_modified()) };
                if !f(&m, &mut z)? {
                    break;
                }
            }
        }
        Kind::Tar => tar_each(io::BufReader::new(File::open(arc).map_err(es)?), f)?,
        Kind::TarGz => tar_each(flate2::read::MultiGzDecoder::new(io::BufReader::new(File::open(arc).map_err(es)?)), f)?,
        Kind::Gz => {
            let name = gz_name(arc);
            let modified = std::fs::metadata(arc).and_then(|m| m.modified()).ok();
            let mut r = flate2::read::MultiGzDecoder::new(io::BufReader::new(File::open(arc).map_err(es)?));
            f(&Meta { name: &name, is_dir: false, size: gz_size(arc).unwrap_or(0), modified }, &mut r)?;
        }
    }
    Ok(())
}

fn tar_each<R: Read>(r: R, f: &mut Visit) -> Result<(), String> {
    let mut ar = tar::Archive::new(r);
    for e in ar.entries().map_err(|e| e.to_string())? {
        let mut e = e.map_err(|e| e.to_string())?;
        let t = e.header().entry_type();
        if !(t.is_file() || t.is_dir()) {
            continue; // links, devices, sparse files ...
        }
        let raw = String::from_utf8_lossy(&e.path_bytes()).into_owned();
        let Some((name, d)) = clean(&raw) else { continue };
        let modified = e.header().mtime().ok().map(|s| SystemTime::UNIX_EPOCH + std::time::Duration::from_secs(s));
        let m = Meta { name: &name, is_dir: d || t.is_dir(), size: e.size(), modified };
        if !f(&m, &mut e)? {
            break;
        }
    }
    Ok(())
}

#[derive(Clone)]
struct Item {
    name: String,
    is_dir: bool,
    size: u64,
    modified: Option<SystemTime>,
}

type Stamp = (Option<SystemTime>, u64);

/// Listings of archive files, keyed by path and invalidated by (mtime, size).
static INDEX: std::sync::LazyLock<Mutex<HashMap<String, (Stamp, Arc<Vec<Item>>)>>> = std::sync::LazyLock::new(Default::default);

fn index(arc: &Path) -> io::Result<Arc<Vec<Item>>> {
    let md = std::fs::metadata(arc)?;
    let stamp = (md.modified().ok(), md.len());
    if let Some((s, v)) = INDEX.lock().ok().and_then(|m| m.get(&key(arc)).cloned()) {
        if s == stamp {
            return Ok(v);
        }
    }
    let v = Arc::new(build_index(arc)?);
    if let Ok(mut m) = INDEX.lock() {
        if m.len() > 32 {
            m.clear();
        }
        m.insert(key(arc), (stamp, v.clone()));
    }
    Ok(v)
}

fn build_index(arc: &Path) -> io::Result<Vec<Item>> {
    let mut out = vec![];
    if kind(arc) == Some(Kind::Zip) {
        // the central directory is enough (also lists encrypted entries)
        let mut ar = open(arc)?;
        for i in 0..ar.len() {
            let Ok(z) = ar.by_index_raw(i) else { continue };
            let Some((name, d)) = clean(z.name()) else { continue };
            out.push(Item { name, is_dir: d || z.is_dir(), size: z.size(), modified: to_systime(z.last_modified()) });
        }
        return Ok(out);
    }
    each(arc, &mut |m, _| {
        out.push(Item { name: m.name.to_string(), is_dir: m.is_dir, size: m.size, modified: m.modified });
        Ok(true)
    })
    .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
    Ok(out)
}

// ------------------------------------------------------------------ edit sessions

struct Session {
    zip: PathBuf,
    /// Staging folder holding the extracted archive.
    dir: PathBuf,
    dirty: bool,
    /// Files opened in an editor: (staging file, mtime, size) when the editor was started.
    edits: Vec<(PathBuf, Option<SystemTime>, u64)>,
}

static SESSIONS: std::sync::LazyLock<Mutex<HashMap<String, Session>>> = std::sync::LazyLock::new(Default::default);

fn key(zip: &Path) -> String {
    zip.to_string_lossy().to_lowercase()
}

fn staging_dir(zip: &Path) -> PathBuf {
    use std::hash::{Hash, Hasher};
    let mut h = std::collections::hash_map::DefaultHasher::new();
    key(zip).hash(&mut h);
    std::process::id().hash(&mut h);
    let name = zip.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    std::env::temp_dir().join("mavyex-archive-edit").join(format!("{:x}-{name}", h.finish()))
}

/// Staging folder of the archive, if it is being edited.
pub fn active(zip: &Path) -> Option<PathBuf> {
    SESSIONS.lock().ok()?.get(&key(zip)).map(|s| s.dir.clone())
}

fn stage_path(dir: &Path, inner: &str) -> PathBuf {
    let mut p = dir.to_path_buf();
    for part in inner.split('/').filter(|s| !s.is_empty()) {
        p.push(part);
    }
    p
}

/// Starts an edit session (extracts the archive) unless one is running; returns the staging folder.
pub fn ensure(zip: &Path) -> Result<PathBuf, String> {
    if let Some(d) = active(zip) {
        return Ok(d);
    }
    let dir = staging_dir(zip);
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    if let Err(e) = extract(zip, "", &dir, &AtomicBool::new(false), &mut |_| {}) {
        let _ = std::fs::remove_dir_all(&dir);
        return Err(e);
    }
    SESSIONS.lock().map_err(|e| e.to_string())?.insert(key(zip), Session { zip: zip.to_path_buf(), dir: dir.clone(), dirty: false, edits: vec![] });
    Ok(dir)
}

/// Real (staging) path for a virtual path inside an archive, for a write. Starts the edit session
/// and marks the archive as changed.
pub fn real_for_write(p: &Path) -> Result<PathBuf, String> {
    let (zip, inner) = split(p).ok_or("not inside an archive")?;
    let dir = ensure(&zip)?;
    if let Ok(mut m) = SESSIONS.lock() {
        if let Some(s) = m.get_mut(&key(&zip)) {
            s.dirty = true;
        }
    }
    Ok(stage_path(&dir, &inner))
}

/// Real (staging) path of a file for editing in an external editor; remembers its state so that
/// a later change marks the archive as modified.
pub fn real_for_edit(p: &Path) -> Result<PathBuf, String> {
    let (zip, inner) = split_inner(p).ok_or("not inside an archive")?;
    let dir = ensure(&zip)?;
    let real = stage_path(&dir, &inner);
    let md = std::fs::metadata(&real).map_err(|e| e.to_string())?;
    if let Ok(mut m) = SESSIONS.lock() {
        if let Some(s) = m.get_mut(&key(&zip)) {
            s.edits.retain(|e| e.0 != real);
            s.edits.push((real.clone(), md.modified().ok(), md.len()));
        }
    }
    Ok(real)
}

/// The archive has unsaved changes (an operation ran, or an edited file changed on disk).
pub fn is_dirty(zip: &Path) -> bool {
    let Ok(m) = SESSIONS.lock() else { return false };
    m.get(&key(zip)).is_some_and(|s| {
        s.dirty
            || s.edits.iter().any(|(f, mt, len)| match std::fs::metadata(f) {
                Ok(md) => md.modified().ok() != *mt || md.len() != *len,
                Err(_) => true,
            })
    })
}

/// Archives with a running edit session (lower-case paths).
pub fn sessions() -> Vec<PathBuf> {
    let Ok(m) = SESSIONS.lock() else { return vec![] };
    m.values().map(|s| s.zip.clone()).collect()
}

/// Forgets the session and deletes the staging folder (changes are lost).
pub fn discard(zip: &Path) {
    let removed = SESSIONS.lock().ok().and_then(|mut m| m.remove(&key(zip)));
    if let Some(s) = removed {
        let _ = std::fs::remove_dir_all(&s.dir);
    }
}

/// `p` is the archive itself or something inside it (case-insensitive, no disk access).
pub fn contains(zip: &Path, p: &Path) -> bool {
    let (z, p) = (key(zip), p.to_string_lossy().to_lowercase());
    p == z || p.strip_prefix(&z).is_some_and(|r| r.starts_with('\\') || r.starts_with('/'))
}

fn zip_time(t: SystemTime) -> Option<zip::DateTime> {
    use chrono::{Datelike, Timelike};
    let d: chrono::DateTime<chrono::Local> = t.into();
    zip::DateTime::from_date_and_time(u16::try_from(d.year()).ok()?, d.month() as u8, d.day() as u8, d.hour() as u8, d.minute() as u8, d.second() as u8).ok()
}

fn walk(dir: &Path, rel: &str, out: &mut Vec<(String, PathBuf, bool)>) -> io::Result<()> {
    let mut items: Vec<_> = std::fs::read_dir(dir)?.flatten().collect();
    items.sort_by_key(|e| e.file_name());
    for e in items {
        let name = format!("{rel}{}", e.file_name().to_string_lossy());
        let path = e.path();
        if e.file_type()?.is_dir() {
            out.push((format!("{name}/"), path.clone(), true));
            walk(&path, &format!("{name}/"), out)?;
        } else {
            out.push((name, path, false));
        }
    }
    Ok(())
}

/// Writes the staging folder back into the archive file (atomically) and ends the session.
pub fn save(zip: &Path) -> Result<(), String> {
    let dir = active(zip).ok_or("no changes to save")?;
    let mut items = vec![];
    walk(&dir, "", &mut items).map_err(|e| e.to_string())?;
    // jar convention: META-INF/ (with the manifest) first
    items.sort_by_key(|(n, _, _)| !n.starts_with("META-INF/"));
    write_archive(zip, &items, &AtomicBool::new(false), &mut |_, _| {})?;
    discard(zip);
    Ok(())
}

/// Total bytes of `srcs` (files and folders) – used as the progress total when zipping.
pub fn tree_total(srcs: &[PathBuf]) -> u64 {
    srcs.iter().map(|s| tree_bytes(s).max(1)).sum()
}

/// Compresses `srcs` (files / folders, each stored under its own name) into a new archive at
/// `zip_path`; the format follows its extension (zip/jar, tar, tar.gz/tgz, gz = one file only).
/// Written to a temp file first; removed again on error or cancel.
pub fn create(srcs: &[PathBuf], zip_path: &Path, cancel: &AtomicBool, progress: &mut dyn FnMut(u64, &Path)) -> Result<(), String> {
    let mut items = vec![];
    for s in srcs {
        let Some(name) = s.file_name() else { continue };
        let name = name.to_string_lossy().into_owned();
        if std::fs::metadata(s).map_err(|e| format!("{}: {e}", s.display()))?.is_dir() {
            items.push((format!("{name}/"), s.clone(), true));
            walk(s, &format!("{name}/"), &mut items).map_err(|e| format!("{}: {e}", s.display()))?;
        } else {
            items.push((name, s.clone(), false));
        }
    }
    write_archive(zip_path, &items, cancel, progress)
}

/// Reads a file, reporting progress and stopping on cancel.
struct Pump<'a> {
    f: File,
    path: &'a Path,
    cancel: &'a AtomicBool,
    progress: &'a mut dyn FnMut(u64, &Path),
}

impl Read for Pump<'_> {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if self.cancel.load(Ordering::Relaxed) {
            return Err(io::Error::other("Cancelled"));
        }
        let n = self.f.read(buf)?;
        (self.progress)(n as u64, self.path);
        Ok(n)
    }
}

/// Writes `items` (name with `/` and trailing `/` for folders, source path, is folder) as a new
/// archive at `out` (format by extension), via a temp file that replaces `out` at the end.
fn write_archive(out: &Path, items: &[(String, PathBuf, bool)], cancel: &AtomicBool, progress: &mut dyn FnMut(u64, &Path)) -> Result<(), String> {
    let tmp = out.with_extension("mavyex-tmp");
    let r = (|| -> Result<(), String> {
        let k = kind(out).unwrap_or(Kind::Zip);
        if k == Kind::Gz && !matches!(items, [(_, _, false)]) {
            return Err(GZ_ONE.into());
        }
        let f = File::create(&tmp).map_err(|e| e.to_string())?;
        match k {
            Kind::Zip => {
                let mut w = zip::ZipWriter::new(f);
                for (name, path, is_dir) in items {
                    if cancel.load(Ordering::Relaxed) {
                        return Err("Cancelled".into());
                    }
                    let md = std::fs::metadata(path).map_err(|e| format!("{}: {e}", path.display()))?;
                    let mut o = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated).large_file(md.len() > u32::MAX as u64);
                    if let Some(t) = md.modified().ok().and_then(zip_time) {
                        o = o.last_modified_time(t);
                    }
                    if *is_dir {
                        w.add_directory(name.clone(), o).map_err(|e| e.to_string())?;
                        progress(1, path);
                    } else {
                        w.start_file(name.clone(), o).map_err(|e| e.to_string())?;
                        let f = File::open(path).map_err(|e| format!("{}: {e}", path.display()))?;
                        io::copy(&mut Pump { f, path, cancel, progress: &mut *progress }, &mut w).map_err(|e| e.to_string())?;
                        if md.len() == 0 {
                            progress(1, path);
                        }
                    }
                }
                w.finish().map_err(|e| e.to_string())?;
            }
            Kind::Tar => {
                let mut b = tar::Builder::new(io::BufWriter::new(f));
                tar_items(&mut b, items, cancel, progress)?;
                b.into_inner().map_err(|e| e.to_string())?.flush().map_err(|e| e.to_string())?;
            }
            Kind::TarGz => {
                let mut b = tar::Builder::new(flate2::write::GzEncoder::new(io::BufWriter::new(f), flate2::Compression::default()));
                tar_items(&mut b, items, cancel, progress)?;
                b.into_inner().and_then(|g| g.finish()).and_then(|mut w| w.flush()).map_err(|e| e.to_string())?;
            }
            Kind::Gz => {
                let (name, path, _) = &items[0];
                let md = std::fs::metadata(path).map_err(|e| format!("{}: {e}", path.display()))?;
                let mut gb = flate2::GzBuilder::new().filename(name.as_str());
                if let Some(s) = md.modified().ok().and_then(|t| t.duration_since(SystemTime::UNIX_EPOCH).ok()) {
                    gb = gb.mtime(s.as_secs() as u32);
                }
                let mut w = gb.write(io::BufWriter::new(f), flate2::Compression::default());
                let src = File::open(path).map_err(|e| format!("{}: {e}", path.display()))?;
                io::copy(&mut Pump { f: src, path, cancel, progress: &mut *progress }, &mut w).map_err(|e| e.to_string())?;
                if md.len() == 0 {
                    progress(1, path);
                }
                w.finish().and_then(|mut w| w.flush()).map_err(|e| e.to_string())?;
            }
        }
        Ok(())
    })();
    // replaces an existing file (the caller has already asked the user)
    let r = r.and_then(|()| std::fs::rename(&tmp, out).map_err(|e| format!("could not replace the archive (is it open elsewhere?): {e}")));
    if r.is_err() {
        let _ = std::fs::remove_file(&tmp);
    }
    r
}

fn tar_items<W: Write>(b: &mut tar::Builder<W>, items: &[(String, PathBuf, bool)], cancel: &AtomicBool, progress: &mut dyn FnMut(u64, &Path)) -> Result<(), String> {
    for (name, path, is_dir) in items {
        if cancel.load(Ordering::Relaxed) {
            return Err("Cancelled".into());
        }
        let name = name.trim_end_matches('/');
        if *is_dir {
            b.append_dir(name, path).map_err(|e| format!("{}: {e}", path.display()))?;
            progress(1, path);
            continue;
        }
        let md = std::fs::metadata(path).map_err(|e| format!("{}: {e}", path.display()))?;
        let mut h = tar::Header::new_gnu();
        h.set_metadata(&md);
        let f = File::open(path).map_err(|e| format!("{}: {e}", path.display()))?;
        b.append_data(&mut h, name, Pump { f, path, cancel, progress: &mut *progress }).map_err(|e| e.to_string())?;
        if md.len() == 0 {
            progress(1, path);
        }
    }
    Ok(())
}

fn list_dir(zip: &Path, dir: &Path, inner: &str) -> io::Result<Vec<Entry>> {
    let prefix = if inner.is_empty() { String::new() } else { format!("{inner}/") };
    let mut out = vec![];
    for de in std::fs::read_dir(stage_path(dir, inner))?.flatten() {
        let Ok(md) = de.metadata() else { continue };
        let name = de.file_name().to_string_lossy().into_owned();
        let is_dir = md.is_dir();
        let path = virtual_path(zip, &format!("{prefix}{name}"));
        let ext = if is_dir { String::new() } else { path.extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default() };
        out.push(Entry { name, path, is_dir, size: if is_dir { 0 } else { md.len() }, modified: md.modified().ok(), hidden: false, is_up: false, deep: false, snippet: String::new(), ext });
    }
    Ok(out)
}

fn tree_bytes(p: &Path) -> u64 {
    match std::fs::metadata(p) {
        Ok(m) if m.is_dir() => std::fs::read_dir(p).map(|rd| rd.flatten().map(|e| tree_bytes(&e.path())).sum()).unwrap_or(0),
        Ok(m) => m.len(),
        Err(_) => 0,
    }
}

fn copy_staged(src: &Path, dst: &Path, cancel: &AtomicBool, progress: &mut dyn FnMut(u64)) -> Result<(), String> {
    if cancel.load(Ordering::Relaxed) {
        return Err("Cancelled".into());
    }
    if std::fs::metadata(src).map_err(|e| e.to_string())?.is_dir() {
        std::fs::create_dir_all(dst).map_err(|e| e.to_string())?;
        for e in std::fs::read_dir(src).map_err(|e| e.to_string())?.flatten() {
            copy_staged(&e.path(), &dst.join(e.file_name()), cancel, progress)?;
        }
    } else {
        let n = std::fs::copy(src, dst).map_err(|e| e.to_string())?;
        progress(n);
    }
    Ok(())
}

/// Lists one level of an archive folder.
pub fn list(zip: &Path, inner: &str) -> io::Result<Vec<Entry>> {
    if let Some(dir) = active(zip) {
        return list_dir(zip, &dir, inner);
    }
    let idx = index(zip)?;
    let prefix = if inner.is_empty() { String::new() } else { format!("{inner}/") };
    // name -> (is_dir, size, modified)
    let mut map: BTreeMap<&str, (bool, u64, Option<SystemTime>)> = BTreeMap::new();
    for it in idx.iter() {
        let Some(rest) = it.name.strip_prefix(&prefix) else { continue };
        if rest.is_empty() {
            continue;
        }
        match rest.split_once('/') {
            Some((dir, _)) => {
                map.entry(dir).or_insert((true, 0, None));
            }
            None => {
                map.insert(rest, (it.is_dir, it.size, it.modified));
            }
        }
    }
    Ok(map
        .into_iter()
        .map(|(name, (is_dir, size, modified))| {
            let path = virtual_path(zip, &format!("{prefix}{name}"));
            let ext = if is_dir { String::new() } else { path.extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default() };
            Entry { name: name.to_string(), path, is_dir, size, modified, hidden: false, is_up: false, deep: false, snippet: String::new(), ext }
        })
        .collect())
}

/// Total uncompressed size of a file or folder inside an archive.
pub fn size(zip: &Path, inner: &str) -> u64 {
    if let Some(dir) = active(zip) {
        return tree_bytes(&stage_path(&dir, inner));
    }
    let Ok(idx) = index(zip) else { return 0 };
    let dir_prefix = format!("{inner}/");
    idx.iter().filter(|it| !it.is_dir && (inner.is_empty() || it.name == inner || it.name.starts_with(&dir_prefix))).map(|it| it.size).sum()
}

/// Extracts a file or a whole folder (`inner`) so that it ends up at `target`.
/// `progress` is called with the number of bytes written.
pub fn extract(zip: &Path, inner: &str, target: &Path, cancel: &AtomicBool, progress: &mut dyn FnMut(u64)) -> Result<(), String> {
    if let Some(dir) = active(zip) {
        return copy_staged(&stage_path(&dir, inner), target, cancel, progress);
    }
    // an empty `inner` extracts everything
    let dir_prefix = if inner.is_empty() { String::new() } else { format!("{inner}/") };
    let mut found = false;
    each(zip, &mut |m, r| {
        if cancel.load(Ordering::Relaxed) {
            return Err("Cancelled".into());
        }
        let rel = if m.name == inner && !inner.is_empty() {
            ""
        } else if let Some(r) = m.name.strip_prefix(&dir_prefix) {
            r
        } else {
            return Ok(true);
        };
        found = true;
        let mut out = target.to_path_buf();
        for part in rel.split('/').filter(|s| !s.is_empty()) {
            out.push(part);
        }
        if m.is_dir {
            std::fs::create_dir_all(&out).map_err(|e| e.to_string())?;
            return Ok(true);
        }
        if let Some(parent) = out.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let mut w = File::create(&out).map_err(|e| e.to_string())?;
        let mut buf = vec![0u8; 256 * 1024];
        loop {
            if cancel.load(Ordering::Relaxed) {
                drop(w);
                let _ = std::fs::remove_file(&out);
                return Err("Cancelled".into());
            }
            let n = r.read(&mut buf).map_err(|e| e.to_string())?;
            if n == 0 {
                break;
            }
            w.write_all(&buf[..n]).map_err(|e| e.to_string())?;
            progress(n as u64);
        }
        if let Some(t) = m.modified {
            let _ = w.set_modified(t); // keep the dates of the archived files
        }
        Ok(!rel.is_empty()) // the one requested file is done: no need to read the rest of a tar stream
    })?;
    if found { Ok(()) } else { Err(format!("{inner}: not found in archive")) }
}

/// Reads at most `max` bytes of a file inside an archive.
pub fn read_head(zip: &Path, inner: &str, max: usize) -> io::Result<Vec<u8>> {
    let mut buf = Vec::new();
    if let Some(dir) = active(zip) {
        File::open(stage_path(&dir, inner))?.take(max as u64).read_to_end(&mut buf)?;
        return Ok(buf);
    }
    if kind(zip) == Some(Kind::Zip) {
        if let Ok(mut f) = open(zip)?.by_name(inner) {
            f.by_ref().take(max as u64).read_to_end(&mut buf)?;
            return Ok(buf);
        }
    }
    let mut found = false;
    each(zip, &mut |m, r| {
        if m.is_dir || m.name != inner {
            return Ok(true);
        }
        found = true;
        r.take(max as u64).read_to_end(&mut buf).map_err(|e| e.to_string())?;
        Ok(false)
    })
    .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
    if found { Ok(buf) } else { Err(io::Error::new(io::ErrorKind::NotFound, format!("{inner}: not found in archive"))) }
}

/// Extracts one archive file to a temp folder (for "open") and returns its path.
pub fn extract_temp(zip: &Path, inner: &str) -> Result<PathBuf, String> {
    if let Some(dir) = active(zip) {
        return Ok(stage_path(&dir, inner)); // being edited: open the working copy itself
    }
    use std::hash::{Hash, Hasher};
    let mut h = std::collections::hash_map::DefaultHasher::new();
    zip.hash(&mut h);
    std::fs::metadata(zip).and_then(|m| m.modified()).ok().hash(&mut h);
    let dir = std::env::temp_dir().join("mavyex-archive").join(format!("{:x}", h.finish()));
    let name = inner.rsplit('/').next().unwrap_or(inner);
    let target = dir.join(name);
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    if !target.exists() {
        extract(zip, inner, &target, &AtomicBool::new(false), &mut |_| {})?;
    }
    Ok(target)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn browse_and_extract_zip() {
        let root = std::env::temp_dir().join(format!("mavyex-zip-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).unwrap();
        let zp = root.join("t.zip");
        {
            let mut w = zip::ZipWriter::new(File::create(&zp).unwrap());
            let o = zip::write::SimpleFileOptions::default();
            w.start_file("a.txt", o).unwrap();
            w.write_all(b"hello").unwrap();
            w.start_file("d/b.txt", o).unwrap();
            w.write_all(b"world!").unwrap();
            w.finish().unwrap();
        }
        let (z, inner) = split(&zp.join("d")).unwrap();
        assert_eq!((z.as_path(), inner.as_str()), (zp.as_path(), "d"));
        assert!(split_inner(&zp).is_none());
        let top: Vec<_> = list(&zp, "").unwrap().into_iter().map(|e| (e.name, e.is_dir)).collect();
        assert_eq!(top, vec![("a.txt".to_string(), false), ("d".to_string(), true)]);
        assert_eq!(list(&zp, "d").unwrap()[0].size, 6);
        assert_eq!(size(&zp, "d"), 6);
        let out = root.join("out");
        extract(&zp, "d", &out, &AtomicBool::new(false), &mut |_| {}).unwrap();
        assert_eq!(std::fs::read_to_string(out.join("b.txt")).unwrap(), "world!");
        assert_eq!(read_head(&zp, "a.txt", 3).unwrap(), b"hel");
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn create_zip_from_files_and_folders() {
        let root = std::env::temp_dir().join(format!("mavyex-zipcreate-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(root.join("dir/sub")).unwrap();
        std::fs::write(root.join("dir/sub/x.txt"), "xx").unwrap();
        std::fs::write(root.join("f.txt"), "hello").unwrap();
        let zp = root.join("out.zip");
        let srcs = vec![root.join("dir"), root.join("f.txt")];
        let mut got = 0;
        create(&srcs, &zp, &AtomicBool::new(false), &mut |n, _| got += n).unwrap();
        assert!(got >= 7);
        let top: Vec<_> = list(&zp, "").unwrap().into_iter().map(|e| e.name).collect();
        assert_eq!(top, vec!["dir".to_string(), "f.txt".to_string()]);
        assert_eq!(list(&zp, "dir/sub").unwrap()[0].size, 2);
        create(&srcs[1..], &zp, &AtomicBool::new(false), &mut |_, _| {}).unwrap(); // replaces
        assert_eq!(list(&zp, "").unwrap().len(), 1);
        assert!(!zp.with_extension("mavyex-tmp").exists());
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn tar_gz_and_gz_round_trip() {
        let root = std::env::temp_dir().join(format!("mavyex-gz-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(root.join("dir/sub")).unwrap();
        std::fs::write(root.join("dir/sub/x.txt"), "xx").unwrap();
        std::fs::write(root.join("f.txt"), "hello").unwrap();
        let srcs = vec![root.join("dir"), root.join("f.txt")];
        for name in ["out.tar.gz", "out.tgz", "out.tar"] {
            let tp = root.join(name);
            create(&srcs, &tp, &AtomicBool::new(false), &mut |_, _| {}).unwrap();
            assert!(split(&tp.join("dir")).is_some(), "{name}");
            let top: Vec<_> = list(&tp, "").unwrap().into_iter().map(|e| (e.name, e.is_dir)).collect();
            assert_eq!(top, vec![("dir".to_string(), true), ("f.txt".to_string(), false)], "{name}");
            assert_eq!(list(&tp, "dir/sub").unwrap()[0].size, 2);
            assert_eq!(size(&tp, "dir"), 2);
            assert_eq!(read_head(&tp, "f.txt", 4).unwrap(), b"hell");
            let out = root.join(format!("x-{name}"));
            extract(&tp, "dir", &out, &AtomicBool::new(false), &mut |_| {}).unwrap();
            assert_eq!(std::fs::read_to_string(out.join("sub/x.txt")).unwrap(), "xx");
        }
        // edit session on a tar.gz
        let tp = root.join("out.tar.gz");
        std::fs::write(real_for_write(&tp.join("new.txt")).unwrap(), "added").unwrap();
        save(&tp).unwrap();
        assert_eq!(read_head(&tp, "new.txt", 100).unwrap(), b"added");
        assert_eq!(list(&tp, "").unwrap().len(), 3);
        // plain gz: one file, named after the archive
        let gp = root.join("f.txt.gz");
        create(&srcs[1..], &gp, &AtomicBool::new(false), &mut |_, _| {}).unwrap();
        let e = list(&gp, "").unwrap();
        assert_eq!((e[0].name.as_str(), e[0].size, e.len()), ("f.txt", 5, 1));
        assert_eq!(read_head(&gp, "f.txt", 100).unwrap(), b"hello");
        extract(&gp, "f.txt", &root.join("plain.txt"), &AtomicBool::new(false), &mut |_| {}).unwrap();
        assert_eq!(std::fs::read_to_string(root.join("plain.txt")).unwrap(), "hello");
        assert!(create(&srcs, &root.join("bad.gz"), &AtomicBool::new(false), &mut |_, _| {}).is_err());
        assert!(!root.join("bad.gz").exists() && !root.join("bad.mavyex-tmp").exists());
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn edit_session_writes_changes_back() {
        let root = std::env::temp_dir().join(format!("mavyex-zipedit-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).unwrap();
        let zp = root.join("e.zip");
        {
            let mut w = zip::ZipWriter::new(File::create(&zp).unwrap());
            let o = zip::write::SimpleFileOptions::default();
            w.start_file("a.txt", o).unwrap();
            w.write_all(b"hello").unwrap();
            w.start_file("d/b.txt", o).unwrap();
            w.write_all(b"world").unwrap();
            w.finish().unwrap();
        }
        assert!(active(&zp).is_none() && !is_dirty(&zp));
        // write: new file in a subfolder, rename, delete
        std::fs::write(real_for_write(&zp.join("d").join("new.txt")).unwrap(), "added").unwrap();
        std::fs::rename(real_for_write(&zp.join("a.txt")).unwrap(), real_for_write(&zp.join("renamed.txt")).unwrap()).unwrap();
        std::fs::remove_file(real_for_write(&zp.join("d").join("b.txt")).unwrap()).unwrap();
        assert!(is_dirty(&zp));
        // while editing, listing and reading come from the working copy
        let names: Vec<_> = list(&zp, "").unwrap().into_iter().map(|e| e.name).collect();
        assert!(names.contains(&"renamed.txt".to_string()) && !names.contains(&"a.txt".to_string()));
        assert_eq!(read_head(&zp, "d/new.txt", 100).unwrap(), b"added");
        assert!(contains(&zp, &zp.join("d")) && !contains(&zp, &root.join("e.zip.bak")));
        save(&zp).unwrap();
        assert!(active(&zp).is_none());
        let mut inner: Vec<_> = list(&zp, "d").unwrap().into_iter().map(|e| e.name).collect();
        inner.sort();
        assert_eq!(inner, vec!["new.txt".to_string()]);
        assert_eq!(read_head(&zp, "renamed.txt", 100).unwrap(), b"hello");
        assert!(!root.join("e.mavyex-tmp").exists());
        // an untouched session is not dirty, and discarding drops the working copy
        ensure(&zp).unwrap();
        assert!(!is_dirty(&zp));
        discard(&zp);
        assert!(active(&zp).is_none());
        let _ = std::fs::remove_dir_all(&root);
    }
}
