//! Per-user install / uninstall (no admin rights, no external installer):
//!   exyex --install     copy to %LOCALAPPDATA%\Programs\exyex, add to the user PATH,
//!                       register in "Apps & features", create a Start menu shortcut
//!   exyex --uninstall   undo all of that
//! A copy of the exe named `*_setup.exe` runs `--install` with prompts when double-clicked.

use std::{borrow::Cow, env, fs, io, os::windows::process::CommandExt, path::PathBuf, process::Command};
use windows::{
    Win32::{
        Foundation::{LPARAM, WPARAM},
        UI::WindowsAndMessaging::*,
    },
    core::PCWSTR,
};
use winreg::{RegKey, RegValue, enums::*};

const UNINSTALL_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\exyex";
const NO_WINDOW: u32 = 0x0800_0000;

fn install_dir() -> PathBuf {
    PathBuf::from(env::var_os("LOCALAPPDATA").unwrap_or_default()).join("Programs").join("exyex")
}

fn shortcut_path() -> PathBuf {
    PathBuf::from(env::var_os("APPDATA").unwrap_or_default()).join(r"Microsoft\Windows\Start Menu\Programs\exyex.lnk")
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(Some(0)).collect()
}

pub fn msgbox(title: &str, text: &str, style: MESSAGEBOX_STYLE) -> MESSAGEBOX_RESULT {
    let (t, x) = (wide(title), wide(text));
    unsafe { MessageBoxW(None, PCWSTR(x.as_ptr()), PCWSTR(t.as_ptr()), style) }
}

// ----------------------------------------------------------------- PATH

fn env_key() -> io::Result<RegKey> {
    RegKey::predef(HKEY_CURRENT_USER).open_subkey_with_flags("Environment", KEY_READ | KEY_WRITE)
}

fn decode(bytes: &[u8]) -> String {
    let w: Vec<u16> = bytes.chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect();
    String::from_utf16_lossy(&w).trim_end_matches('\0').to_string()
}

fn encode(s: &str) -> Vec<u8> {
    wide(s).iter().flat_map(|c| c.to_le_bytes()).collect()
}

/// Reads the user PATH keeping its registry type (usually REG_EXPAND_SZ, so
/// `%USERPROFILE%` style entries survive our edit).
fn read_path(key: &RegKey) -> (String, RegType) {
    match key.get_raw_value("Path") {
        Ok(v) => (decode(&v.bytes), v.vtype),
        Err(_) => (String::new(), REG_EXPAND_SZ),
    }
}

fn write_path(key: &RegKey, value: &str, vtype: RegType) -> io::Result<()> {
    key.set_raw_value("Path", &RegValue { bytes: Cow::Owned(encode(value)), vtype })
}

fn same_dir(a: &str, b: &str) -> bool {
    a.trim().trim_end_matches('\\').eq_ignore_ascii_case(b.trim().trim_end_matches('\\'))
}

/// Tells Explorer and newly started shells that the environment changed.
fn broadcast_env_change() {
    let env = wide("Environment");
    unsafe {
        let mut result = 0usize;
        let _ = SendMessageTimeoutW(
            HWND_BROADCAST,
            WM_SETTINGCHANGE,
            WPARAM(0),
            LPARAM(env.as_ptr() as isize),
            SMTO_ABORTIFHUNG,
            5000,
            Some(&mut result),
        );
    }
}

// -------------------------------------------------------------- install

pub fn install() -> Result<PathBuf, String> {
    let err = |what: &str, e: &dyn std::fmt::Display| format!("{what}: {e}");
    let exe = env::current_exe().map_err(|e| err("cannot locate this exe", &e))?;
    let dir = install_dir();
    let dest = dir.join("exyex.exe");
    fs::create_dir_all(&dir).map_err(|e| err("cannot create install folder", &e))?;

    let same = fs::canonicalize(&exe).ok() == fs::canonicalize(&dest).ok();
    if !same {
        fs::copy(&exe, &dest).map_err(|e| format!("Could not copy to {}: {e}\n\nIs exyex running? Close it and try again.", dest.display()))?;
    }

    // PATH (user scope)
    let key = env_key().map_err(|e| err("cannot open user environment", &e))?;
    let (path, vtype) = read_path(&key);
    let dir_str = dir.to_string_lossy().into_owned();
    if !path.split(';').any(|p| same_dir(p, &dir_str)) {
        let new = if path.trim().is_empty() { dir_str.clone() } else { format!("{};{}", path.trim_end_matches(';'), dir_str) };
        write_path(&key, &new, vtype).map_err(|e| err("cannot update PATH", &e))?;
    }

    // "Apps & features" entry
    let (u, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey(UNINSTALL_KEY).map_err(|e| err("cannot register uninstaller", &e))?;
    let exe_str = dest.to_string_lossy().into_owned();
    let _ = u.set_value("DisplayName", &"exyex");
    let _ = u.set_value("DisplayVersion", &env!("CARGO_PKG_VERSION"));
    let _ = u.set_value("Publisher", &"exyex");
    let _ = u.set_value("InstallLocation", &dir_str);
    let _ = u.set_value("DisplayIcon", &exe_str);
    let _ = u.set_value("UninstallString", &format!("\"{exe_str}\" --uninstall"));
    let _ = u.set_value("NoModify", &1u32);
    let _ = u.set_value("NoRepair", &1u32);

    // Start menu shortcut (best effort)
    let q = |s: &str| s.replace('\'', "''");
    let script = format!(
        "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{}');$s.TargetPath='{}';$s.WorkingDirectory='{}';$s.Save()",
        q(&shortcut_path().to_string_lossy()),
        q(&exe_str),
        q(&dir_str)
    );
    let _ = Command::new("powershell").args(["-NoProfile", "-WindowStyle", "Hidden", "-Command", &script]).creation_flags(NO_WINDOW).status();

    broadcast_env_change();
    Ok(dest)
}

pub fn uninstall() -> Result<(), String> {
    let dir = install_dir();
    let dir_str = dir.to_string_lossy().into_owned();
    if let Ok(key) = env_key() {
        let (path, vtype) = read_path(&key);
        let kept: Vec<&str> = path.split(';').filter(|p| !p.trim().is_empty() && !same_dir(p, &dir_str)).collect();
        write_path(&key, &kept.join(";"), vtype).map_err(|e| format!("cannot update PATH: {e}"))?;
    }
    let _ = RegKey::predef(HKEY_CURRENT_USER).delete_subkey_all(UNINSTALL_KEY);
    let _ = fs::remove_file(shortcut_path());
    broadcast_env_change();
    // We may be running from the install folder: delete it a moment after we exit.
    let cmd = format!("ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir_str}\"");
    let _ = Command::new("cmd").args(["/c", &cmd]).creation_flags(NO_WINDOW | 0x0000_0008).spawn();
    Ok(())
}

// --------------------------------------------------------- entry points

/// `exyex --install` / `exyex --uninstall`: silent apart from the result box.
pub fn run_cli(uninstall_mode: bool) {
    if uninstall_mode {
        match uninstall() {
            Ok(()) => {
                msgbox("exyex", "exyex was removed from your PATH and uninstalled.\nYour settings in %APPDATA%\\exyex were kept.", MB_OK | MB_ICONINFORMATION);
            }
            Err(e) => {
                msgbox("exyex uninstall failed", &e, MB_OK | MB_ICONERROR);
            }
        }
    } else {
        match install() {
            Ok(dest) => {
                msgbox(
                    "exyex",
                    &format!("Installed to {}\n\nOpen a NEW terminal and type:  exyex .", dest.display()),
                    MB_OK | MB_ICONINFORMATION,
                );
            }
            Err(e) => {
                msgbox("exyex install failed", &e, MB_OK | MB_ICONERROR);
            }
        }
    }
}

/// Double-clicked `exyex_<ver>_x64_setup.exe`: ask, install, offer to launch.
pub fn run_setup_wizard() {
    let ask = format!(
        "Install exyex {} for the current user?\n\n\u{2022} copies it to {}\n\u{2022} adds it to your PATH, so \"exyex .\" works in any terminal\n\u{2022} adds a Start menu shortcut",
        env!("CARGO_PKG_VERSION"),
        install_dir().display()
    );
    if msgbox("exyex setup", &ask, MB_YESNO | MB_ICONQUESTION) != IDYES {
        return;
    }
    match install() {
        Ok(dest) => {
            let r = msgbox(
                "exyex setup",
                "Installation finished.\n\nOpen a NEW terminal and type:  exyex .\n\nStart exyex now?",
                MB_YESNO | MB_ICONINFORMATION,
            );
            if r == IDYES {
                let _ = Command::new(dest).spawn();
            }
        }
        Err(e) => {
            msgbox("exyex setup failed", &e, MB_OK | MB_ICONERROR);
        }
    }
}
