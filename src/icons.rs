//! Real Windows shell icons (by file extension), cached as egui textures.

use eframe::egui::{ColorImage, Context, TextureHandle, TextureOptions};
use std::collections::HashMap;

#[derive(Default)]
pub struct Icons {
    cache: HashMap<String, Option<TextureHandle>>,
}

impl Icons {
    /// `ext` is the lowercase extension; directories use the key "<dir>".
    pub fn get(&mut self, ctx: &Context, ext: &str, is_dir: bool) -> Option<&TextureHandle> {
        self.fetch(ctx, ext, is_dir, false)
    }

    /// 32 px version (grid view).
    pub fn get_large(&mut self, ctx: &Context, ext: &str, is_dir: bool) -> Option<&TextureHandle> {
        self.fetch(ctx, ext, is_dir, true)
    }

    fn fetch(&mut self, ctx: &Context, ext: &str, is_dir: bool, large: bool) -> Option<&TextureHandle> {
        let key = format!("{}{}", if large { "L:" } else { "" }, if is_dir { "<dir>" } else { ext });
        self.cache
            .entry(key.clone())
            .or_insert_with(|| {
                load(ext, is_dir, large).map(|img| ctx.load_texture(format!("icon-{key}"), img, TextureOptions::LINEAR))
            })
            .as_ref()
    }
}

#[cfg(windows)]
fn load(ext: &str, is_dir: bool, large: bool) -> Option<ColorImage> {
    use windows::{
        Win32::{
            Graphics::Gdi::*,
            Storage::FileSystem::{FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_NORMAL},
            UI::{Shell::*, WindowsAndMessaging::*},
        },
        core::PCWSTR,
    };
    unsafe {
        let name: Vec<u16> = if is_dir { "folder".to_string() } else { format!("file.{ext}") }
            .encode_utf16()
            .chain(Some(0))
            .collect();
        let attrs = if is_dir { FILE_ATTRIBUTE_DIRECTORY } else { FILE_ATTRIBUTE_NORMAL };
        let mut info = SHFILEINFOW::default();
        let r = SHGetFileInfoW(
            PCWSTR(name.as_ptr()),
            attrs,
            Some(&mut info),
            std::mem::size_of::<SHFILEINFOW>() as u32,
            SHGFI_ICON | if large { SHGFI_LARGEICON } else { SHGFI_SMALLICON } | SHGFI_USEFILEATTRIBUTES,
        );
        if r == 0 || info.hIcon.is_invalid() {
            return None;
        }
        let hicon = info.hIcon;
        let mut ii = ICONINFO::default();
        if GetIconInfo(hicon, &mut ii).is_err() {
            let _ = DestroyIcon(hicon);
            return None;
        }
        let mut bm = BITMAP::default();
        GetObjectW(
            ii.hbmColor.into(),
            std::mem::size_of::<BITMAP>() as i32,
            Some(&mut bm as *mut _ as *mut _),
        );
        let (w, h) = (bm.bmWidth, bm.bmHeight);
        let result = if w > 0 && h > 0 {
            let hdc = GetDC(None);
            let mut bmi = BITMAPINFO::default();
            bmi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h; // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB.0;
            let mut px = vec![0u8; (w * h * 4) as usize];
            let ok = GetDIBits(hdc, ii.hbmColor, 0, h as u32, Some(px.as_mut_ptr() as *mut _), &mut bmi, DIB_RGB_COLORS);
            // Icons without an alpha channel carry transparency in the mask.
            let has_alpha = px.chunks_exact(4).any(|p| p[3] != 0);
            let mut mask = vec![0u8; (w * h * 4) as usize];
            if !has_alpha && !ii.hbmMask.is_invalid() {
                GetDIBits(hdc, ii.hbmMask, 0, h as u32, Some(mask.as_mut_ptr() as *mut _), &mut bmi, DIB_RGB_COLORS);
            }
            ReleaseDC(None, hdc);
            if ok == 0 {
                None
            } else {
                let mut rgba = Vec::with_capacity(px.len());
                for (i, p) in px.chunks_exact(4).enumerate() {
                    let a = if has_alpha { p[3] } else if mask[i * 4] == 0 { 255 } else { 0 };
                    rgba.extend_from_slice(&[p[2], p[1], p[0], a]);
                }
                Some(ColorImage::from_rgba_unmultiplied([w as usize, h as usize], &rgba))
            }
        } else {
            None
        };
        let _ = DeleteObject(ii.hbmColor.into());
        let _ = DeleteObject(ii.hbmMask.into());
        let _ = DestroyIcon(hicon);
        result
    }
}

#[cfg(not(windows))]
fn load(_ext: &str, _is_dir: bool, _large: bool) -> Option<ColorImage> {
    None
}
