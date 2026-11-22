//! SFTP browsing and transfers over SSH (pure-Rust `russh`), authenticated
//! through the running **Pageant** agent (no passwords or key files are
//! handled by exyex).
//!
//! Remote locations are "virtual" paths: `sftp:\user@host[:port]\~\docs\x.txt`
//! (`~` = the remote home folder). Everything that works on a `Path` (tabs,
//! history, path bar, favorites, recents) therefore works unchanged; only the
//! places that touch the file system go through this module. The public API is
//! blocking (it drives a small private tokio runtime) and must be called from
//! worker threads or short UI actions, never from inside another runtime.

use crate::model::Entry;
use russh::{
    client::{self, Handle},
    keys::{
        Algorithm, HashAlg, PublicKeyOrCertificate,
        agent::{AgentIdentity, client::AgentClient},
    },
};
use russh_sftp::{
    client::SftpSession,
    protocol::{FileAttributes, OpenFlags},
};
use std::{
    cell::RefCell,
    collections::HashMap,
    ffi::OsStr,
    future::Future,
    io,
    path::{Path, PathBuf},
    sync::{
        Arc, LazyLock, Mutex, RwLock,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use tokio::io::{AsyncReadExt, AsyncWriteExt};

const PREFIX: &str = "sftp:";
const CHUNK: usize = 256 * 1024;

// ------------------------------------------------------------------ paths

/// A parsed `sftp:\user@host[:port]\rest...` location.
#[derive(Clone, Debug, PartialEq)]
pub struct Loc {
    pub user: String,
    pub host: String,
    pub port: u16,
    pub rest: Vec<String>,
}

impl Loc {
    fn key(&self) -> String {
        format!("{}@{}:{}", self.user, self.host, self.port)
    }
}

fn parts(p: &Path) -> Option<Vec<String>> {
    let s = p.to_string_lossy().replace('/', "\\");
    if s.len() < PREFIX.len() || !s.is_char_boundary(PREFIX.len()) || !s[..PREFIX.len()].eq_ignore_ascii_case(PREFIX) {
        return None;
    }
    Some(s[PREFIX.len()..].split('\\').filter(|x| !x.is_empty()).map(str::to_string).collect())
}

pub fn is_remote(p: &Path) -> bool {
    parts(p).is_some()
}

/// `sftp:` itself – the list of known connections.
pub fn is_root(p: &Path) -> bool {
    parts(p).is_some_and(|v| v.is_empty())
}

pub fn parse(p: &Path) -> Option<Loc> {
    let mut v = parts(p)?;
    if v.is_empty() {
        return None;
    }
    let first = v.remove(0);
    let (user, hostport) = first.split_once('@')?;
    let (host, port) = match hostport.rsplit_once(':') {
        Some((h, p)) if !p.is_empty() && p.chars().all(|c| c.is_ascii_digit()) => (h, p.parse().ok()?),
        _ => (hostport, 22),
    };
    Some(Loc { user: user.to_string(), host: host.to_string(), port, rest: v })
}

/// `sftp://user@host:2222/home/me` (also `ssh://`) → virtual path.
pub fn from_url(s: &str) -> Option<PathBuf> {
    let lower = s.to_lowercase();
    let rest = if lower.starts_with("sftp://") {
        &s[7..]
    } else if lower.starts_with("ssh://") {
        &s[6..]
    } else {
        return None;
    };
    let (auth, path) = rest.split_once('/').unwrap_or((rest, ""));
    let resolved;
    let auth = if auth.contains('@') {
        auth
    } else {
        resolved = alias_target(auth)?; // `sftp://prod/var/www` with an alias called `prod`
        resolved.as_str()
    };
    if auth.starts_with('@') || auth.ends_with('@') {
        return None;
    }
    let mut out = format!("{PREFIX}\\{auth}");
    let segs: Vec<&str> = path.split('/').filter(|x| !x.is_empty()).collect();
    if segs.is_empty() {
        out.push_str("\\~");
    } else {
        for seg in segs {
            out.push('\\');
            out.push_str(seg);
        }
    }
    Some(PathBuf::from(out))
}

// ---------------------------------------------------------------- aliases

static ALIASES: LazyLock<RwLock<HashMap<String, String>>> = LazyLock::new(|| RwLock::new(HashMap::new()));

/// Replaces the alias table (`user@host:port` -> alias); called when the settings change.
pub fn set_aliases(m: HashMap<String, String>) {
    *ALIASES.write().unwrap() = m;
}

pub fn alias_of(l: &Loc) -> Option<String> {
    ALIASES.read().unwrap().get(&l.key()).cloned()
}

/// `user@host:port` of a path's connection.
pub fn conn_key(p: &Path) -> Option<String> {
    parse(p).map(|l| l.key())
}

/// Looks an alias up (case-insensitive) and returns `user@host[:port]`.
fn alias_target(name: &str) -> Option<String> {
    let map = ALIASES.read().unwrap();
    let key = map.iter().find(|(_, a)| a.eq_ignore_ascii_case(name)).map(|(k, _)| k.clone())?;
    Some(key.strip_suffix(":22").map(str::to_string).unwrap_or(key))
}

/// Short name of a connection: its alias, else `user@host`.
fn label_of(l: &Loc) -> String {
    alias_of(l).unwrap_or_else(|| format!("{}@{}", l.user, l.host))
}

/// Breadcrumb label of the server segment: `alias (user@host)` or just `user@host`.
pub fn crumb_label(p: &Path) -> Option<String> {
    let l = parse(p)?;
    let port = if l.port == 22 { String::new() } else { format!(":{}", l.port) };
    let plain = format!("{}@{}{port}", l.user, l.host);
    Some(match alias_of(&l) {
        Some(a) => format!("{a} ({plain})"),
        None => plain,
    })
}

/// Tab title for a connection root / home folder.
pub fn title(p: &Path) -> Option<String> {
    let l = parse(p)?;
    if l.rest.is_empty() || l.rest == ["~"] { Some(label_of(&l)) } else { None }
}

/// Human-friendly form of a remote path: `alias:\dir\sub` (or `user@host:\...`).
pub fn pretty(p: &Path) -> Option<String> {
    let l = parse(p)?;
    let head = label_of(&l);
    Some(if l.rest.is_empty() { head } else { format!("{head}:\\{}", l.rest.join("\\")) })
}

fn conn_path(l: &Loc) -> PathBuf {
    let port = if l.port == 22 { String::new() } else { format!(":{}", l.port) };
    PathBuf::from(format!("{PREFIX}\\{}@{}{port}", l.user, l.host))
}

/// Root of the server a path belongs to (`sftp:\user@host`, i.e. remote `/`).
pub fn root_of(p: &Path) -> Option<PathBuf> {
    parse(p).map(|l| conn_path(&l))
}

/// The remote (POSIX) path for a location, given the remote home folder.
fn remote_path(l: &Loc, home: &str) -> String {
    let mut segs: Vec<&str> = l.rest.iter().map(String::as_str).collect();
    let base = if segs.first() == Some(&"~") {
        segs.remove(0);
        home.trim_end_matches('/').to_string()
    } else {
        String::new()
    };
    let tail = segs.join("/");
    match (base.is_empty(), tail.is_empty()) {
        (true, true) => "/".to_string(),
        (true, false) => format!("/{tail}"),
        (false, true) => base,
        (false, false) => format!("{base}/{tail}"),
    }
}

fn join_remote(dir: &str, name: &str) -> String {
    format!("{}/{name}", dir.trim_end_matches('/'))
}

// ------------------------------------------------------------ known hosts

fn hosts_file() -> Option<PathBuf> {
    Some(dirs::config_dir()?.join("exyex").join("known_hosts.json"))
}

/// Trust on first use: remember the host key fingerprint, refuse when it changes.
fn check_host_key(hostport: &str, fp: &str) -> Result<(), String> {
    let file = hosts_file().ok_or("no config folder")?;
    let mut map: HashMap<String, String> = std::fs::read_to_string(&file).ok().and_then(|s| serde_json::from_str(&s).ok()).unwrap_or_default();
    match map.get(hostport) {
        Some(old) if old == fp => Ok(()),
        Some(old) => Err(format!(
            "HOST KEY CHANGED for {hostport}!\nsaved: {old}\nnow:   {fp}\nSomeone may be intercepting the connection. If the server was reinstalled, remove the entry from {}",
            file.display()
        )),
        None => {
            map.insert(hostport.to_string(), fp.to_string());
            if let Some(d) = file.parent() {
                let _ = std::fs::create_dir_all(d);
            }
            if let Ok(s) = serde_json::to_string_pretty(&map) {
                let _ = std::fs::write(&file, s);
            }
            Ok(())
        }
    }
}

// ------------------------------------------------------------- connections

static RT: LazyLock<tokio::runtime::Runtime> =
    LazyLock::new(|| tokio::runtime::Builder::new_multi_thread().worker_threads(2).enable_all().build().expect("tokio runtime"));

fn block<T>(f: impl Future<Output = T>) -> T {
    RT.block_on(f)
}

fn err<E: std::fmt::Display>(e: E) -> io::Error {
    io::Error::other(e.to_string())
}

struct Client {
    hostport: String,
    /// Why the host key was refused (read back after a failed connect).
    reason: Arc<Mutex<Option<String>>>,
}

impl client::Handler for Client {
    type Error = russh::Error;

    async fn check_server_key(&mut self, key: &PublicKeyOrCertificate) -> Result<bool, Self::Error> {
        let fp = match key {
            PublicKeyOrCertificate::PublicKey { key, .. } => key.fingerprint(HashAlg::Sha256).to_string(),
            PublicKeyOrCertificate::Certificate(c) => c.public_key().fingerprint(HashAlg::Sha256).to_string(),
        };
        match check_host_key(&self.hostport, &fp) {
            Ok(()) => Ok(true),
            Err(e) => {
                *self.reason.lock().unwrap() = Some(e);
                Ok(false)
            }
        }
    }
}

struct Conn {
    _handle: Handle<Client>,
    sftp: SftpSession,
    home: String,
}

static POOL: LazyLock<Mutex<HashMap<String, Arc<Conn>>>> = LazyLock::new(|| Mutex::new(HashMap::new()));

async fn connect(l: &Loc) -> io::Result<Arc<Conn>> {
    let config = Arc::new(client::Config {
        inactivity_timeout: Some(Duration::from_secs(900)),
        keepalive_interval: Some(Duration::from_secs(30)),
        ..Default::default()
    });
    let reason = Arc::new(Mutex::new(None));
    let handler = Client { hostport: format!("{}:{}", l.host, l.port), reason: reason.clone() };
    let addr = (l.host.clone(), l.port);
    let mut handle = match tokio::time::timeout(Duration::from_secs(15), client::connect(config, addr, handler)).await {
        Err(_) => return Err(err(format!("timeout connecting to {}:{}", l.host, l.port))),
        Ok(Err(e)) => {
            if let Some(r) = reason.lock().unwrap().take() {
                return Err(err(r));
            }
            return Err(err(format!("can't connect to {}:{}: {e}", l.host, l.port)));
        }
        Ok(Ok(h)) => h,
    };

    // authenticate with the keys loaded in Pageant
    let mut agent = AgentClient::connect_pageant().await.map_err(|e| err(format!("can't reach Pageant – is it running? ({e})")))?;
    let ids = agent.request_identities().await.map_err(|e| err(format!("Pageant: {e}")))?;
    if ids.is_empty() {
        return Err(err("Pageant has no keys loaded"));
    }
    let mut tried = 0;
    let mut ok = false;
    for id in ids {
        let AgentIdentity::PublicKey { key, .. } = id else { continue };
        tried += 1;
        let hash = if matches!(key.algorithm(), Algorithm::Rsa { .. }) { handle.best_supported_rsa_hash().await.ok().flatten().flatten() } else { None };
        if let Ok(res) = handle.authenticate_publickey_with(&l.user, key, hash, &mut agent).await {
            if matches!(res, russh::client::AuthResult::Success) {
                ok = true;
                break;
            }
        }
    }
    if !ok {
        return Err(err(format!("{}@{} rejected all {tried} key(s) from Pageant", l.user, l.host)));
    }

    let ch = handle.channel_open_session().await.map_err(err)?;
    ch.request_subsystem(true, "sftp").await.map_err(|e| err(format!("SFTP is not available on this server: {e}")))?;
    let sftp = SftpSession::new(ch.into_stream()).await.map_err(|e| err(format!("SFTP init failed: {e}")))?;
    sftp.set_timeout(60);
    let home = sftp.canonicalize(".").await.unwrap_or_else(|_| "/".into());
    Ok(Arc::new(Conn { _handle: handle, sftp, home }))
}

async fn conn(l: &Loc) -> io::Result<Arc<Conn>> {
    if let Some(c) = POOL.lock().unwrap().get(&l.key()) {
        return Ok(c.clone());
    }
    let c = connect(l).await?;
    POOL.lock().unwrap().insert(l.key(), c.clone());
    Ok(c)
}

fn looks_dead(e: &io::Error) -> bool {
    let m = e.to_string().to_lowercase();
    ["disconnect", "channel", "timeout", "send", "closed", "broken", "eof", "reset"].iter().any(|w| m.contains(w))
}

/// Runs an operation on the (pooled) connection; reconnects once if the session died.
fn run_on<T, Fut: Future<Output = io::Result<T>>>(l: &Loc, retry: bool, mk: impl Fn(Arc<Conn>, String) -> Fut) -> io::Result<T> {
    block(async {
        let mut attempt = 0;
        loop {
            let c = conn(l).await?;
            let rp = remote_path(l, &c.home);
            match mk(c, rp).await {
                Err(e) if retry && attempt == 0 && looks_dead(&e) => {
                    POOL.lock().unwrap().remove(&l.key());
                    attempt += 1;
                }
                Err(e) => {
                    if looks_dead(&e) {
                        POOL.lock().unwrap().remove(&l.key());
                    }
                    return Err(e);
                }
                ok => return ok,
            }
        }
    })
}

/// Drops all pooled connections.
pub fn disconnect_all() {
    POOL.lock().unwrap().clear();
}

fn loc_of(p: &Path) -> io::Result<Loc> {
    parse(p).ok_or_else(|| err(format!("not a valid SFTP location: {}", p.display())))
}

// ---------------------------------------------------------------- listing

fn to_time(secs: Option<u32>) -> Option<SystemTime> {
    secs.map(|s| UNIX_EPOCH + Duration::from_secs(s as u64))
}

/// Known connections (from favorites and recent folders) shown at `sftp:`.
fn known_connections() -> Vec<Loc> {
    let st = crate::store::Store::load();
    let mut seen: Vec<Loc> = vec![];
    for p in st.favorites.iter().chain(st.visits.keys()) {
        if let Some(mut l) = parse(Path::new(p)) {
            l.rest.clear();
            if !seen.iter().any(|x| x.key() == l.key()) {
                seen.push(l);
            }
        }
    }
    seen
}

fn entry(name: String, path: PathBuf, is_dir: bool, size: u64, modified: Option<SystemTime>) -> Entry {
    let ext = if is_dir { String::new() } else { name.rsplit_once('.').map(|x| x.1.to_lowercase()).unwrap_or_default() };
    Entry { hidden: name.starts_with('.'), name, path, is_dir, size, modified, is_up: false, deep: false, snippet: String::new(), ext }
}

pub fn list(p: &Path) -> io::Result<Vec<Entry>> {
    if is_root(p) {
        return Ok(known_connections()
            .iter()
            .map(|l| {
                let name = match alias_of(l) {
                    Some(a) => format!("{a}  —  {}@{}", l.user, l.host),
                    None => format!("{}@{}", l.user, l.host),
                };
                entry(name, conn_path(l), true, 0, None)
            })
            .collect());
    }
    let l = loc_of(p)?;
    run_on(&l, true, |c, rp| async move {
        let rd = c.sftp.read_dir(rp.clone()).await.map_err(err)?;
        let mut out = vec![];
        for e in rd {
            let name = e.file_name();
            if name == "." || name == ".." {
                continue;
            }
            let md = e.metadata();
            let mut is_dir = md.is_dir();
            if md.is_symlink() {
                // follow the link to see whether it points at a folder
                is_dir = c.sftp.metadata(join_remote(&rp, &name)).await.map(|m| m.is_dir()).unwrap_or(false);
            }
            out.push((name, is_dir, md.len(), to_time(md.mtime)));
        }
        Ok(out)
    })
    .map(|v| v.into_iter().map(|(name, is_dir, size, mtime)| entry(name.clone(), p.join(&name), is_dir, size, mtime)).collect())
}

pub fn exists(p: &Path) -> bool {
    let Ok(l) = loc_of(p) else { return false };
    run_on(&l, true, |c, rp| async move { c.sftp.try_exists(rp).await.map_err(err) }).unwrap_or(false)
}

pub fn is_dir(p: &Path) -> bool {
    if is_root(p) {
        return true;
    }
    let Ok(l) = loc_of(p) else { return false };
    run_on(&l, true, |c, rp| async move { c.sftp.metadata(rp).await.map(|m| m.is_dir()).map_err(err) }).unwrap_or(false)
}

// ----------------------------------------------------------- modifications

pub fn mkdir(p: &Path) -> io::Result<()> {
    let l = loc_of(p)?;
    run_on(&l, true, |c, rp| async move { c.sftp.create_dir(rp).await.map_err(err) })
}

pub fn create_file(p: &Path) -> io::Result<()> {
    let l = loc_of(p)?;
    run_on(&l, true, |c, rp| async move {
        let f = c.sftp.open_with_flags(rp, OpenFlags::CREATE | OpenFlags::WRITE | OpenFlags::EXCLUDE).await.map_err(err)?;
        f.close().await.map_err(err)
    })
}

pub fn rename(from: &Path, to: &Path) -> io::Result<()> {
    let (a, b) = (loc_of(from)?, loc_of(to)?);
    if a.key() != b.key() {
        return Err(err("can't rename across servers"));
    }
    run_on(&a, true, |c, rp| {
        let target = remote_path(&b, &c.home);
        async move { c.sftp.rename(rp, target).await.map_err(err) }
    })
}

/// One item of a remote tree: path, is-dir, size, mtime (parents come before their children).
type Node = (String, bool, u64, Option<u32>);

async fn walk(c: &Conn, root: &str) -> io::Result<Vec<Node>> {
    let md = c.sftp.symlink_metadata(root.to_string()).await.map_err(err)?;
    if !md.is_dir() {
        return Ok(vec![(root.to_string(), false, md.len(), md.mtime)]);
    }
    let mut out = vec![(root.to_string(), true, 0, md.mtime)];
    let mut stack = vec![root.to_string()];
    while let Some(dir) = stack.pop() {
        for e in c.sftp.read_dir(dir.clone()).await.map_err(err)? {
            let name = e.file_name();
            if name == "." || name == ".." {
                continue;
            }
            let child = join_remote(&dir, &name);
            let md = e.metadata();
            if md.is_symlink() {
                // links to files are copied as files; links to folders are skipped (no cycles)
                if let Ok(t) = c.sftp.metadata(child.clone()).await {
                    if !t.is_dir() {
                        out.push((child, false, t.len(), t.mtime));
                    }
                }
            } else if md.is_dir() {
                out.push((child.clone(), true, 0, md.mtime));
                stack.push(child);
            } else {
                out.push((child, false, md.len(), md.mtime));
            }
        }
    }
    Ok(out)
}

/// Deletes a file or a whole folder tree (permanently – there is no remote recycle bin).
pub fn remove_all(p: &Path) -> io::Result<()> {
    let l = loc_of(p)?;
    if l.rest.is_empty() || l.rest == ["~"] {
        return Err(err("refusing to delete the remote home / root folder"));
    }
    run_on(&l, false, |c, rp| async move {
        for (path, is_dir, _, _) in walk(&c, &rp).await?.into_iter().rev() {
            if is_dir {
                c.sftp.remove_dir(path).await.map_err(err)?;
            } else {
                c.sftp.remove_file(path).await.map_err(err)?;
            }
        }
        Ok(())
    })
}

pub fn size_of(p: &Path) -> u64 {
    let Ok(l) = loc_of(p) else { return 0 };
    run_on(&l, true, |c, rp| async move { Ok(walk(&c, &rp).await?.iter().map(|n| n.2).sum()) }).unwrap_or(0)
}

/// First free `name`, `name (2)`, … in a remote folder.
pub fn unique_remote(dir: &Path, name: &OsStr) -> PathBuf {
    let first = dir.join(name);
    if !exists(&first) {
        return first;
    }
    let n = name.to_string_lossy();
    let (stem, ext) = match n.rsplit_once('.') {
        Some((s, e)) if !s.is_empty() => (s.to_string(), format!(".{e}")),
        _ => (n.to_string(), String::new()),
    };
    for i in 2..1000 {
        let cand = dir.join(format!("{stem} ({i}){ext}"));
        if !exists(&cand) {
            return cand;
        }
    }
    first
}

// ---------------------------------------------------------------- transfers

/// Copies a remote file / folder to `local` (the full target path).
pub fn download(remote: &Path, local: &Path, cancel: &AtomicBool, progress: &mut dyn FnMut(u64)) -> Result<(), String> {
    let l = loc_of(remote).map_err(|e| e.to_string())?;
    let progress = RefCell::new(progress);
    run_on(&l, false, |c, rp| {
        let progress = &progress;
        async move {
            let nodes = walk(&c, &rp).await?;
            for (path, is_dir, _, mtime) in nodes {
                let rel = path[rp.len()..].trim_start_matches('/').to_string();
                let mut dest = local.to_path_buf();
                for seg in rel.split('/').filter(|s| !s.is_empty()) {
                    dest.push(seg);
                }
                if is_dir {
                    std::fs::create_dir_all(&dest)?;
                    continue;
                }
                let mut src = c.sftp.open(path).await.map_err(err)?;
                let mut dst = std::fs::File::create(&dest)?;
                let mut buf = vec![0u8; CHUNK];
                loop {
                    if cancel.load(Ordering::Relaxed) {
                        drop(dst);
                        let _ = std::fs::remove_file(&dest);
                        return Err(err("Cancelled"));
                    }
                    let n = src.read(&mut buf).await?;
                    if n == 0 {
                        break;
                    }
                    io::Write::write_all(&mut dst, &buf[..n])?;
                    (progress.borrow_mut())(n as u64);
                }
                if let Some(t) = to_time(mtime) {
                    let _ = dst.set_modified(t);
                }
            }
            Ok(())
        }
    })
    .map_err(|e| e.to_string())
}

/// Local tree → (local path, remote path, is_dir), parents first.
fn local_tree(local: &Path, remote: &str, out: &mut Vec<(PathBuf, String, bool)>) -> io::Result<()> {
    let md = std::fs::metadata(local)?;
    if md.is_dir() {
        out.push((local.to_path_buf(), remote.to_string(), true));
        for de in std::fs::read_dir(local)?.flatten() {
            local_tree(&de.path(), &join_remote(remote, &de.file_name().to_string_lossy()), out)?;
        }
    } else {
        out.push((local.to_path_buf(), remote.to_string(), false));
    }
    Ok(())
}

/// Copies a local file / folder to `remote` (the full target path).
pub fn upload(local: &Path, remote: &Path, cancel: &AtomicBool, progress: &mut dyn FnMut(u64)) -> Result<(), String> {
    let l = loc_of(remote).map_err(|e| e.to_string())?;
    let progress = RefCell::new(progress);
    run_on(&l, false, |c, rp| {
        let progress = &progress;
        async move {
            let mut tree = vec![];
            local_tree(local, &rp, &mut tree)?;
            for (lp, rpath, is_dir) in tree {
                if is_dir {
                    if !c.sftp.try_exists(rpath.clone()).await.unwrap_or(false) {
                        c.sftp.create_dir(rpath).await.map_err(err)?;
                    }
                    continue;
                }
                let mut src = std::fs::File::open(&lp)?;
                let mut dst = c
                    .sftp
                    .open_with_flags(rpath.clone(), OpenFlags::CREATE | OpenFlags::WRITE | OpenFlags::TRUNCATE)
                    .await
                    .map_err(|e| err(format!("{rpath}: {e}")))?;
                let mut buf = vec![0u8; CHUNK];
                loop {
                    if cancel.load(Ordering::Relaxed) {
                        let _ = dst.close().await;
                        let _ = c.sftp.remove_file(rpath).await;
                        return Err(err("Cancelled"));
                    }
                    let n = io::Read::read(&mut src, &mut buf)?;
                    if n == 0 {
                        break;
                    }
                    dst.write_all(&buf[..n]).await?;
                    (progress.borrow_mut())(n as u64);
                }
                dst.flush().await?;
                let _ = dst.close().await;
                if let Some(m) = std::fs::metadata(&lp).ok().and_then(|m| m.modified().ok()).and_then(|t| t.duration_since(UNIX_EPOCH).ok()) {
                    let attrs = FileAttributes { atime: Some(m.as_secs() as u32), mtime: Some(m.as_secs() as u32), ..FileAttributes::default() };
                    let _ = c.sftp.set_metadata(rpath, attrs).await;
                }
            }
            Ok(())
        }
    })
    .map_err(|e| e.to_string())
}

/// Reads at most `max` bytes from the start of a remote file.
pub fn read_head(p: &Path, max: usize) -> io::Result<Vec<u8>> {
    let l = loc_of(p)?;
    run_on(&l, true, |c, rp| async move {
        let mut f = c.sftp.open(rp).await.map_err(err)?;
        let mut buf = Vec::new();
        let mut chunk = vec![0u8; CHUNK.min(max.max(1))];
        while buf.len() < max {
            let n = f.read(&mut chunk).await?;
            if n == 0 {
                break;
            }
            buf.extend_from_slice(&chunk[..n.min(max - buf.len())]);
        }
        Ok(buf)
    })
}

/// Downloads a file to the temp folder (for "open") and returns the local path.
pub fn fetch_temp(p: &Path) -> Result<PathBuf, String> {
    use std::hash::{Hash, Hasher};
    let mut h = std::collections::hash_map::DefaultHasher::new();
    p.hash(&mut h);
    let dir = std::env::temp_dir().join("exyex-sftp").join(format!("{:x}", h.finish()));
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let name = p.file_name().ok_or("no file name")?;
    let target = dir.join(name);
    download(p, &target, &AtomicBool::new(false), &mut |_| {})?;
    Ok(target)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn url_and_path_round_trip() {
        let p = from_url("sftp://bob@example.com:2222/var/www/").unwrap();
        assert_eq!(p, PathBuf::from("sftp:\\bob@example.com:2222\\var\\www"));
        let l = parse(&p).unwrap();
        assert_eq!((l.user.as_str(), l.host.as_str(), l.port), ("bob", "example.com", 2222));
        assert_eq!(remote_path(&l, "/home/bob"), "/var/www");
        let home = from_url("ssh://bob@example.com").unwrap();
        let l = parse(&home).unwrap();
        assert_eq!(l.port, 22);
        assert_eq!(remote_path(&l, "/home/bob"), "/home/bob");
        let sub = parse(Path::new("sftp:\\bob@h\\~\\docs\\a b.txt")).unwrap();
        assert_eq!(remote_path(&sub, "/home/bob/"), "/home/bob/docs/a b.txt");
        assert!(is_remote(Path::new("SFTP:\\x@y")));
        assert!(!is_remote(Path::new("C:\\sftp")));
        assert!(is_root(Path::new("sftp:")));
        assert!(from_url("sftp://nouser/x").is_none());
        assert_eq!(parts(Path::new("sftp:/bob@h/a/b")).unwrap(), vec!["bob@h", "a", "b"]);
    }

    #[test]
    fn aliases_resolve_in_urls_and_titles() {
        let mut m = HashMap::new();
        m.insert("root@10.0.0.5:22".to_string(), "prod".to_string());
        set_aliases(m);
        let p = from_url("sftp://PROD/var/log").unwrap();
        assert_eq!(p, PathBuf::from(r"sftp:\root@10.0.0.5\var\log"));
        assert_eq!(title(Path::new(r"sftp:\root@10.0.0.5")).as_deref(), Some("prod"));
        assert_eq!(title(Path::new(r"sftp:\root@10.0.0.5\var")), None);
        assert_eq!(pretty(&p).as_deref(), Some(r"prod:\var\log"));
        assert!(from_url("sftp://nobody/x").is_none());
        set_aliases(HashMap::new());
    }

    /// Manual check (needs Pageant with a key and a reachable server):
    /// `EXYEX_SFTP_TEST=sftp://user@host/ cargo test sftp_live -- --ignored --nocapture`
    #[test]
    #[ignore]
    fn sftp_live() {
        let url = std::env::var("EXYEX_SFTP_TEST").expect("set EXYEX_SFTP_TEST");
        let p = from_url(&url).unwrap();
        let items = list(&p).unwrap();
        for e in items.iter().take(20) {
            println!("{} {} {}", if e.is_dir { "d" } else { "-" }, e.size, e.name);
        }
        println!("{} entries", items.len());
        // read-only transfer check on the smallest regular file
        if let Some(f) = items.iter().filter(|e| !e.is_dir && e.size > 0).min_by_key(|e| e.size) {
            let head = read_head(&f.path, 64).unwrap();
            let tmp = fetch_temp(&f.path).unwrap();
            let local = std::fs::metadata(&tmp).unwrap().len();
            println!("downloaded {} ({} bytes remote, {} local, head {} bytes)", f.name, f.size, local, head.len());
            assert_eq!(local, f.size);
            let _ = std::fs::remove_file(tmp);
        }
    }
}
