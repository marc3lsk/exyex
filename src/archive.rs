//! Read-only zip support: an archive behaves like a folder. A path such as
//! `C:\x\a.zip\docs\readme.txt` is "virtual": the part up to `a.zip` exists on
//! disk, the rest lives inside the archive.

use crate::model::Entry;
use std::{
    collections::BTreeMap,
    fs::File,
    io::{self, Read},
    path::{Path, PathBuf},
    sync::atomic::{AtomicBool, Ordering},
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

/// Lists one level of an archive folder.
pub fn list(zip: &Path, inner: &str) -> io::Result<Vec<Entry>> {
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
            Entry { name, path, is_dir, size, modified, hidden: false, is_up: false, deep: false, ext }
        })
        .collect())
}

/// Total uncompressed size of a file or folder inside an archive.
pub fn size(zip: &Path, inner: &str) -> u64 {
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
    let mut ar = open(zip).map_err(|e| e.to_string())?;
    let dir_prefix = format!("{inner}/");
    let mut found = false;
    for i in 0..ar.len() {
        if cancel.load(Ordering::Relaxed) {
            return Err("Cancelled".into());
        }
        let mut f = ar.by_index(i).map_err(|e| e.to_string())?;
        let name = norm(f.name());
        let rel = if name == inner {
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
    }
    if found { Ok(()) } else { Err(format!("{inner}: not found in archive")) }
}

/// Reads at most `max` bytes of a file inside an archive.
pub fn read_head(zip: &Path, inner: &str, max: usize) -> io::Result<Vec<u8>> {
    let mut ar = open(zip)?;
    let mut f = ar.by_name(inner).map_err(|e| io::Error::new(io::ErrorKind::NotFound, e.to_string()))?;
    let mut buf = Vec::new();
    f.by_ref().take(max as u64).read_to_end(&mut buf)?;
    Ok(buf)
}

/// Extracts one archive file to a temp folder (for "open") and returns its path.
pub fn extract_temp(zip: &Path, inner: &str) -> Result<PathBuf, String> {
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
}
