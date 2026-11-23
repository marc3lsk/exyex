//! Zip support: an archive behaves like a folder. A path such as
//! `C:\x\a.zip\docs\readme.txt` is "virtual": the part up to `a.zip` exists on
//! disk, the rest lives inside the archive.
//!
//! Browsing reads the zip directly. The first *write* (copy in, delete, rename, F4 edit ...) extracts the
//! whole archive into a staging folder (an "edit session"); from then on listing / reading use that folder.
//! When the user leaves the archive, the app asks whether to write the staging folder back (`save`).

use crate::model::Entry;
use std::{
    collections::{BTreeMap, HashMap},
    fs::File,
    io::{self, Read},
    path::{Path, PathBuf},
    sync::{
        Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::SystemTime,
};
use zip::ZipArchive;

const EXTS: [&str; 2] = ["zip", "jar"];

/// Extension says it is an archive we can browse.
pub fn is_archive_name(p: &Path) -> bool {
    p.extension().is_some_and(|e| EXTS.iter().any(|x| e.eq_ignore_ascii_case(x)))
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

fn norm(name: &str) -> String {
    name.replace('\\', "/")
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
    std::env::temp_dir().join("exyex-archive-edit").join(format!("{:x}-{name}", h.finish()))
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
    let tmp = zip.with_extension("exyex-tmp");
    let r = (|| -> Result<(), String> {
        let mut w = zip::ZipWriter::new(File::create(&tmp).map_err(|e| e.to_string())?);
        for (name, path, is_dir) in &items {
            let mut o = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
            if let Some(t) = std::fs::metadata(path).ok().and_then(|m| m.modified().ok()).and_then(zip_time) {
                o = o.last_modified_time(t);
            }
            if *is_dir {
                w.add_directory(name.clone(), o).map_err(|e| e.to_string())?;
            } else {
                w.start_file(name.clone(), o).map_err(|e| e.to_string())?;
                io::copy(&mut File::open(path).map_err(|e| format!("{name}: {e}"))?, &mut w).map_err(|e| e.to_string())?;
            }
        }
        w.finish().map_err(|e| e.to_string())?;
        std::fs::rename(&tmp, zip).map_err(|e| format!("could not replace the archive (is it open elsewhere?): {e}"))
    })();
    if r.is_err() {
        let _ = std::fs::remove_file(&tmp);
        return r;
    }
    discard(zip);
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
    let mut ar = open(zip)?;
    let prefix = if inner.is_empty() { String::new() } else { format!("{inner}/") };
    // name -> (is_dir, size, modified)
    let mut map: BTreeMap<String, (bool, u64, Option<SystemTime>)> = BTreeMap::new();
    for i in 0..ar.len() {
        let Ok(f) = ar.by_index_raw(i) else { continue };
        let name = norm(f.name());
        let Some(rest) = name.strip_prefix(&prefix) else { continue };
        if rest.is_empty() {
            continue;
        }
        match rest.split_once('/') {
            Some((dir, _)) => {
                map.entry(dir.to_string()).or_insert((true, 0, None));
            }
            None => {
                map.insert(rest.to_string(), (f.is_dir(), f.size(), to_systime(f.last_modified())));
            }
        }
    }
    Ok(map
        .into_iter()
        .map(|(name, (is_dir, size, modified))| {
            let path = virtual_path(zip, &format!("{prefix}{name}"));
            let ext = if is_dir { String::new() } else { path.extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default() };
            Entry { name, path, is_dir, size, modified, hidden: false, is_up: false, deep: false, snippet: String::new(), ext }
        })
        .collect())
}

/// Total uncompressed size of a file or folder inside an archive.
pub fn size(zip: &Path, inner: &str) -> u64 {
    if let Some(dir) = active(zip) {
        return tree_bytes(&stage_path(&dir, inner));
    }
    let Ok(mut ar) = open(zip) else { return 0 };
    let dir_prefix = format!("{inner}/");
    let mut total = 0;
    for i in 0..ar.len() {
        if let Ok(f) = ar.by_index_raw(i) {
            let n = norm(f.name());
            if !f.is_dir() && (n == inner || n.starts_with(&dir_prefix)) {
                total += f.size();
            }
        }
    }
    total
}

/// Extracts a file or a whole folder (`inner`) so that it ends up at `target`.
/// `progress` is called with the number of bytes written.
pub fn extract(zip: &Path, inner: &str, target: &Path, cancel: &AtomicBool, progress: &mut dyn FnMut(u64)) -> Result<(), String> {
    if let Some(dir) = active(zip) {
        return copy_staged(&stage_path(&dir, inner), target, cancel, progress);
    }
    let mut ar = open(zip).map_err(|e| e.to_string())?;
    // an empty `inner` extracts everything
    let dir_prefix = if inner.is_empty() { String::new() } else { format!("{inner}/") };
    let mut found = false;
    for i in 0..ar.len() {
        if cancel.load(Ordering::Relaxed) {
            return Err("Cancelled".into());
        }
        let mut f = ar.by_index(i).map_err(|e| e.to_string())?;
        let name = norm(f.name());
        let rel = if name == inner && !inner.is_empty() {
            String::new()
        } else if let Some(r) = name.strip_prefix(&dir_prefix) {
            r.to_string()
        } else {
            continue;
        };
        if rel.split('/').any(|p| p == "..") {
            continue; // never write outside the target
        }
        found = true;
        let mut out = target.to_path_buf();
        for part in rel.split('/').filter(|s| !s.is_empty()) {
            out.push(part);
        }
        if f.is_dir() {
            std::fs::create_dir_all(&out).map_err(|e| e.to_string())?;
            continue;
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
            let n = f.read(&mut buf).map_err(|e| e.to_string())?;
            if n == 0 {
                break;
            }
            io::Write::write_all(&mut w, &buf[..n]).map_err(|e| e.to_string())?;
            progress(n as u64);
        }
        if let Some(t) = to_systime(f.last_modified()) {
            let _ = w.set_modified(t); // keep the dates of the archived files
        }
    }
    if found { Ok(()) } else { Err(format!("{inner}: not found in archive")) }
}

/// Reads at most `max` bytes of a file inside an archive.
pub fn read_head(zip: &Path, inner: &str, max: usize) -> io::Result<Vec<u8>> {
    if let Some(dir) = active(zip) {
        let mut buf = Vec::new();
        File::open(stage_path(&dir, inner))?.take(max as u64).read_to_end(&mut buf)?;
        return Ok(buf);
    }
    let mut ar = open(zip)?;
    let mut f = ar.by_name(inner).map_err(|e| io::Error::new(io::ErrorKind::NotFound, e.to_string()))?;
    let mut buf = Vec::new();
    f.by_ref().take(max as u64).read_to_end(&mut buf)?;
    Ok(buf)
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
    let dir = std::env::temp_dir().join("exyex-archive").join(format!("{:x}", h.finish()));
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
        let root = std::env::temp_dir().join(format!("exyex-zip-{}", std::process::id()));
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
    fn edit_session_writes_changes_back() {
        let root = std::env::temp_dir().join(format!("exyex-zipedit-{}", std::process::id()));
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
        assert!(!root.join("e.exyex-tmp").exists());
        // an untouched session is not dirty, and discarding drops the working copy
        ensure(&zp).unwrap();
        assert!(!is_dirty(&zp));
        discard(&zp);
        assert!(active(&zp).is_none());
        let _ = std::fs::remove_dir_all(&root);
    }
}
