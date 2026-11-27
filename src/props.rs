//! Windows "Properties" dialog (the same sheet Explorer shows) for local files / folders / drives.

use std::path::{Path, PathBuf};

/// Opens the shell Properties dialog: one item via `SHObjectProperties`, several via
/// `SHMultiFileProperties` (a data object built from their PIDLs). Local paths only.
#[cfg(windows)]
pub fn show(paths: &[PathBuf]) -> Result<(), String> {
    use windows::{
        Win32::{
            System::Com::{COINIT_APARTMENTTHREADED, CoInitializeEx, CoTaskMemFree, IDataObject},
            UI::Shell::{BHID_DataObject, Common::ITEMIDLIST, IShellItemArray, SHCreateShellItemArrayFromIDLists, SHMultiFileProperties, SHOP_FILEPATH, SHObjectProperties, SHParseDisplayName},
        },
        core::{HSTRING, PCWSTR},
    };
    match paths {
        [] => Ok(()),
        [one] => {
            let p = HSTRING::from(wide_path(one).as_str());
            let ok = unsafe { SHObjectProperties(None, SHOP_FILEPATH, &p, PCWSTR::null()) };
            if ok.as_bool() { Ok(()) } else { Err(format!("Could not open properties of {}", one.display())) }
        }
        many => unsafe {
            // STA is what the shell expects; S_FALSE / RPC_E_CHANGED_MODE (already initialized) are fine
            let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
            let mut pidls: Vec<*const ITEMIDLIST> = vec![];
            let mut err = None;
            for p in many {
                let mut pidl: *mut ITEMIDLIST = std::ptr::null_mut();
                let h = HSTRING::from(wide_path(p).as_str());
                match SHParseDisplayName(&h, None, &mut pidl, 0, None) {
                    Ok(()) => pidls.push(pidl),
                    Err(e) => {
                        err = Some(format!("{}: {e}", p.display()));
                        break;
                    }
                }
            }
            let r = match err {
                Some(e) => Err(e),
                None => SHCreateShellItemArrayFromIDLists(&pidls)
                    .and_then(|arr: IShellItemArray| arr.BindToHandler::<_, IDataObject>(None, &BHID_DataObject))
                    .and_then(|obj| SHMultiFileProperties(&obj, 0))
                    .map_err(|e| format!("Could not open properties: {e}")),
            };
            for p in pidls {
                CoTaskMemFree(Some(p as *const _));
            }
            r
        },
    }
}

#[cfg(not(windows))]
pub fn show(_paths: &[PathBuf]) -> Result<(), String> {
    Err("Properties are only available on Windows".into())
}

/// `C:` → `C:\` (a bare drive letter means "current dir on that drive" to the shell).
fn wide_path(p: &Path) -> String {
    let s = p.to_string_lossy().into_owned();
    if s.len() == 2 && s.ends_with(':') { format!("{s}\\") } else { s }
}
