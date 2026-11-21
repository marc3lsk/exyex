//! Single-instance support: a second `exyex <path>` hands the path to the
//! running window (which opens it as a new tab) and exits.
//!
//! Detection uses a named mutex; the path travels over a per-user named pipe
//! (the default pipe ACL only lets the owner and admins write to it).

use eframe::egui;
use std::{
    path::PathBuf,
    sync::{Arc, OnceLock, mpsc},
};

/// What a second instance asked for. `None` = just bring the window to front.
pub type OpenRequest = Option<PathBuf>;

pub struct Server {
    pub rx: mpsc::Receiver<OpenRequest>,
    ctx: Arc<OnceLock<egui::Context>>,
}

impl Server {
    /// Lets the pipe thread wake the UI when a request arrives.
    pub fn set_ctx(&self, ctx: egui::Context) {
        let _ = self.ctx.set(ctx);
    }
}

pub enum Init {
    /// We are the first instance; keep `Server` and open the window.
    Primary(Server),
    /// The request was handed to the running instance; exit now.
    Forwarded,
    /// Something went wrong (or non-Windows): just run standalone.
    Standalone,
}

#[cfg(not(windows))]
pub fn init(_args: &[String]) -> Init {
    Init::Standalone
}

#[cfg(windows)]
pub fn init(args: &[String]) -> Init {
    use std::{
        fs::{File, OpenOptions},
        io::{Read, Write},
        os::windows::io::FromRawHandle,
        time::Duration,
    };
    use windows::{
        Win32::{
            Foundation::{ERROR_ALREADY_EXISTS, GetLastError, INVALID_HANDLE_VALUE},
            Storage::FileSystem::PIPE_ACCESS_INBOUND,
            System::{
                Pipes::{ConnectNamedPipe, CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE, PIPE_UNLIMITED_INSTANCES, PIPE_WAIT},
                Threading::CreateMutexW,
            },
            UI::WindowsAndMessaging::{ASFW_ANY, AllowSetForegroundWindow},
        },
        core::PCWSTR,
    };

    let user = std::env::var("USERNAME").unwrap_or_default();
    let mutex_name: Vec<u16> = format!("Local\\exyex-instance-{user}").encode_utf16().chain(Some(0)).collect();
    let pipe_path = format!(r"\\.\pipe\exyex-{user}");

    let mutex = unsafe { CreateMutexW(None, false, PCWSTR(mutex_name.as_ptr())) };
    let Ok(mutex) = mutex else { return Init::Standalone };
    let already_running = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
    std::mem::forget(mutex); // held for the lifetime of the process

    if already_running {
        // Resolve relative paths ("exyex .") against *our* working directory.
        let cwd = std::env::current_dir().unwrap_or_default();
        let path = args
            .first()
            .and_then(|a| crate::model::parse_user_path(a, &cwd))
            .map(|p| p.to_string_lossy().into_owned())
            .unwrap_or_default();
        unsafe {
            let _ = AllowSetForegroundWindow(ASFW_ANY);
        }
        // The first instance may still be creating its pipe: retry briefly.
        for _ in 0..40 {
            if let Ok(mut f) = OpenOptions::new().write(true).open(&pipe_path) {
                if f.write_all(path.as_bytes()).is_ok() {
                    return Init::Forwarded;
                }
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        return Init::Standalone; // the other instance is not answering
    }

    let (tx, rx) = mpsc::channel::<OpenRequest>();
    let ctx: Arc<OnceLock<egui::Context>> = Arc::new(OnceLock::new());
    let ctx2 = ctx.clone();
    let pipe_name: Vec<u16> = pipe_path.encode_utf16().chain(Some(0)).collect();
    std::thread::spawn(move || {
        loop {
            let pipe = unsafe {
                CreateNamedPipeW(
                    PCWSTR(pipe_name.as_ptr()),
                    PIPE_ACCESS_INBOUND,
                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                    PIPE_UNLIMITED_INSTANCES,
                    4096,
                    4096,
                    0,
                    None,
                )
            };
            if pipe == INVALID_HANDLE_VALUE {
                std::thread::sleep(Duration::from_millis(500));
                continue;
            }
            // Errors here (e.g. client already connected) are fine: we just read.
            unsafe {
                let _ = ConnectNamedPipe(pipe, None);
            }
            // Takes ownership: the handle closes when `f` is dropped.
            let mut f = unsafe { File::from_raw_handle(pipe.0 as _) };
            let mut buf = Vec::new();
            let _ = f.read_to_end(&mut buf);
            drop(f);
            let text = String::from_utf8_lossy(&buf).trim().to_string();
            let req = if text.is_empty() { None } else { Some(PathBuf::from(text)) };
            if tx.send(req).is_err() {
                return; // app is gone
            }
            if let Some(c) = ctx2.get() {
                c.request_repaint();
            }
        }
    });
    Init::Primary(Server { rx, ctx })
}
