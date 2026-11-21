//! Quick Look: a side panel that previews the item under the cursor
//! (text, images, folders, archive contents, binary hex head).

use crate::{
    archive,
    model::{Entry, fmt_size, fmt_time},
};
use eframe::egui::{self, Color32, ColorImage, RichText, TextureHandle, Ui};
use std::{
    path::{Path, PathBuf},
    sync::mpsc::{Receiver, channel},
};

const TEXT_BYTES: usize = 64 * 1024;
const TEXT_LINES: usize = 1200;
const IMAGE_MAX_FILE: u64 = 60 * 1024 * 1024;
const IMAGE_EXTS: [&str; 7] = ["png", "jpg", "jpeg", "gif", "bmp", "webp", "ico"];

pub enum PvData {
    Text { text: String, truncated: bool },
    Image(ColorImage, [u32; 2]),
    /// Rendered vector image plus its XML source (user can switch between them).
    Svg { img: ColorImage, size: [u32; 2], source: String },
    Folder { dirs: usize, files: usize, bytes: u64, names: Vec<String> },
    Hex(String),
    Note(String),
}

/// Blocking loader – runs on a worker thread.
pub fn load(path: &Path, e: &Entry) -> PvData {
    if crate::sftp::is_remote(path) {
        if e.is_dir {
            return match crate::sftp::list(path) {
                Ok(v) => folder_summary(&v),
                Err(err) => PvData::Note(err.to_string()),
            };
        }
        if IMAGE_EXTS.contains(&e.ext.as_str()) && e.size <= 8 * 1024 * 1024 {
            return match crate::sftp::read_head(path, 8 * 1024 * 1024) {
                Ok(b) => match image::load_from_memory(&b) {
                    Ok(img) => {
                        let (w, h) = (img.width(), img.height());
                        let img = if w.max(h) > 1600 { img.thumbnail(1600, 1600) } else { img };
                        let rgba = img.to_rgba8();
                        let size = [rgba.width() as usize, rgba.height() as usize];
                        PvData::Image(ColorImage::from_rgba_unmultiplied(size, rgba.as_raw()), [w, h])
                    }
                    Err(err) => PvData::Note(format!("Can't decode image: {err}")),
                },
                Err(err) => PvData::Note(err.to_string()),
            };
        }
        return match crate::sftp::read_head(path, TEXT_BYTES) {
            Ok(b) => bytes_preview(&b, e.size),
            Err(err) => PvData::Note(err.to_string()),
        };
    }
    if let Some((zip, inner)) = archive::split(path) {
        if e.is_dir || inner.is_empty() {
            return match archive::list(&zip, &inner) {
                Ok(v) => folder_summary(&v),
                Err(err) => PvData::Note(err.to_string()),
            };
        }
        return match archive::read_head(&zip, &inner, TEXT_BYTES) {
            Ok(b) => bytes_preview(&b, e.size),
            Err(err) => PvData::Note(err.to_string()),
        };
    }
    if e.is_dir {
        return match crate::model::read_dir(path) {
            Ok(v) => folder_summary(&v),
            Err(err) => PvData::Note(err.to_string()),
        };
    }
    if e.ext == "svg" {
        return load_svg(path);
    }
    if IMAGE_EXTS.contains(&e.ext.as_str()) {
        if e.size > IMAGE_MAX_FILE {
            return PvData::Note("Image is too large to preview".into());
        }
        return match image::open(path) {
            Ok(img) => {
                let (w, h) = (img.width(), img.height());
                let img = if w.max(h) > 1600 { img.thumbnail(1600, 1600) } else { img };
                let rgba = img.to_rgba8();
                let size = [rgba.width() as usize, rgba.height() as usize];
                PvData::Image(ColorImage::from_rgba_unmultiplied(size, rgba.as_raw()), [w, h])
            }
            Err(err) => PvData::Note(format!("Can't decode image: {err}")),
        };
    }
    match std::fs::File::open(path).and_then(|f| {
        use std::io::Read;
        let mut buf = Vec::new();
        f.take(TEXT_BYTES as u64).read_to_end(&mut buf)?;
        Ok(buf)
    }) {
        Ok(b) => bytes_preview(&b, e.size),
        Err(err) => PvData::Note(err.to_string()),
    }
}

fn load_svg(path: &Path) -> PvData {
    let data = match std::fs::read(path) {
        Ok(d) if d.len() <= 8 * 1024 * 1024 => d,
        Ok(_) => return PvData::Note("SVG is too large to preview".into()),
        Err(e) => return PvData::Note(e.to_string()),
    };
    let source = String::from_utf8_lossy(&data).replace('\t', "    ");
    match render_svg(&data, 1200.0) {
        Ok((img, size)) => PvData::Svg { img, size, source },
        Err(e) => PvData::Text { text: source, truncated: false }.with_note(format!("Can't render SVG: {e}")),
    }
}

/// Renders SVG data so that its longer side is `target` pixels; also returns the nominal size.
pub fn render_svg(data: &[u8], target: f32) -> Result<(ColorImage, [u32; 2]), String> {
    let mut opt = resvg::usvg::Options::default();
    opt.fontdb_mut().load_system_fonts();
    let tree = resvg::usvg::Tree::from_data(data, &opt).map_err(|e| e.to_string())?;
    let sz = tree.size();
    let (w, h) = (sz.width().max(1.0), sz.height().max(1.0));
    let scale = (target / w.max(h)).clamp(0.01, 8.0);
    let (pw, ph) = ((w * scale).ceil().max(1.0) as u32, (h * scale).ceil().max(1.0) as u32);
    let mut pix = resvg::tiny_skia::Pixmap::new(pw, ph).ok_or("SVG has an invalid size")?;
    resvg::render(&tree, resvg::tiny_skia::Transform::from_scale(scale, scale), &mut pix.as_mut());
    Ok((ColorImage::from_rgba_premultiplied([pw as usize, ph as usize], pix.data()), [w.round() as u32, h.round() as u32]))
}

impl PvData {
    /// Text preview that explains why the richer view is missing.
    fn with_note(self, note: String) -> PvData {
        match self {
            PvData::Text { text, truncated } => PvData::Text { text: format!("<!-- {note} -->
{text}"), truncated },
            other => other,
        }
    }
}

fn folder_summary(v: &[Entry]) -> PvData {
    let dirs = v.iter().filter(|e| e.is_dir).count();
    let files = v.iter().filter(|e| !e.is_dir && !e.is_up).count();
    let bytes = v.iter().filter(|e| !e.is_dir).map(|e| e.size).sum();
    let mut names: Vec<&Entry> = v.iter().filter(|e| !e.is_up).collect();
    names.sort_by(|a, b| b.is_dir.cmp(&a.is_dir).then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase())));
    let names = names.into_iter().take(60).map(|e| if e.is_dir { format!("📁 {}", e.name) } else { e.name.clone() }).collect();
    PvData::Folder { dirs, files, bytes, names }
}

fn bytes_preview(b: &[u8], full_size: u64) -> PvData {
    if b.is_empty() {
        return PvData::Note("Empty file".into());
    }
    let binary = b[..b.len().min(8192)].contains(&0);
    if binary {
        let mut s = String::new();
        for (i, chunk) in b.chunks(16).take(24).enumerate() {
            let hex: Vec<String> = chunk.iter().map(|x| format!("{x:02x}")).collect();
            let asc: String = chunk.iter().map(|&x| if (32..127).contains(&x) { x as char } else { '.' }).collect();
            s += &format!("{:06x}  {:<47}  {}\n", i * 16, hex.join(" "), asc);
        }
        return PvData::Hex(s);
    }
    let text = String::from_utf8_lossy(b);
    let mut out = String::new();
    let mut lines = 0;
    for l in text.lines() {
        if lines >= TEXT_LINES {
            break;
        }
        out.push_str(&l.replace('\t', "    "));
        out.push('\n');
        lines += 1;
    }
    PvData::Text { text: out, truncated: lines >= TEXT_LINES || (b.len() as u64) < full_size }
}

#[derive(Default)]
pub struct Previewer {
    path: PathBuf,
    rx: Option<Receiver<(PathBuf, PvData)>>,
    data: Option<PvData>,
    tex: Option<TextureHandle>,
    /// SVG files: show the XML source instead of the rendered image.
    svg_source: bool,
}

impl Previewer {
    /// Draws the panel contents; returns true when the user asked to close it.
    pub fn ui(&mut self, ui: &mut Ui, e: Option<&Entry>) -> bool {
        let ctx = ui.ctx().clone();
        let mut close = false;
        ui.horizontal(|ui| {
            ui.strong("Quick Look");
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if ui.small_button("✕").on_hover_text("Close (F3)").clicked() {
                    close = true;
                }
            });
        });
        ui.separator();
        let Some(e) = e.filter(|e| !e.is_up) else {
            ui.weak("Nothing selected");
            return close;
        };
        if self.path != e.path {
            self.path = e.path.clone();
            self.data = None;
            self.tex = None;
            let (tx, rx) = channel();
            self.rx = Some(rx);
            let (p, ent, c2) = (e.path.clone(), e.clone(), ctx.clone());
            std::thread::spawn(move || {
                let d = load(&p, &ent);
                let _ = tx.send((p, d));
                c2.request_repaint();
            });
        }
        if let Some(rx) = &self.rx {
            if let Ok((p, d)) = rx.try_recv() {
                if p == self.path {
                    if let PvData::Image(img, _) | PvData::Svg { img, .. } = &d {
                        self.tex = Some(ctx.load_texture("quicklook", img.clone(), egui::TextureOptions::LINEAR));
                    }
                    self.data = Some(d);
                    self.rx = None;
                }
            }
        }

        ui.label(RichText::new(&e.name).strong().size(15.0));
        let mut meta = Vec::new();
        if !e.is_dir {
            meta.push(fmt_size(e.size));
        }
        if e.modified.is_some() {
            meta.push(fmt_time(e.modified));
        }
        ui.weak(meta.join("  ·  "));
        ui.add_space(4.0);

        let Some(data) = &self.data else {
            ui.horizontal(|ui| {
                ui.spinner();
                ui.weak("Loading…");
            });
            return close;
        };
        egui::ScrollArea::both().auto_shrink([false, false]).show(ui, |ui| match data {
            PvData::Text { text, truncated } => {
                ui.add(egui::Label::new(RichText::new(text).monospace().size(12.5)).extend());
                if *truncated {
                    ui.weak("… (truncated)");
                }
            }
            PvData::Image(_, [w, h]) => {
                ui.weak(format!("{w} × {h}"));
                if let Some(t) = &self.tex {
                    ui.add(egui::Image::new(t).shrink_to_fit().corner_radius(4));
                }
            }
            PvData::Svg { size: [w, h], source, .. } => {
                ui.horizontal(|ui| {
                    ui.selectable_value(&mut self.svg_source, false, "Image");
                    ui.selectable_value(&mut self.svg_source, true, "Source");
                    ui.weak(format!("{w} × {h}"));
                });
                if self.svg_source {
                    ui.add(egui::Label::new(RichText::new(source).monospace().size(12.5)).extend());
                } else if let Some(t) = &self.tex {
                    // light checkerboard-free backdrop so dark/transparent art stays visible
                    egui::Frame::new().fill(Color32::from_gray(245)).corner_radius(4).inner_margin(6).show(ui, |ui| {
                        ui.add(egui::Image::new(t).shrink_to_fit());
                    });
                }
            }
            PvData::Folder { dirs, files, bytes, names } => {
                ui.label(format!("{dirs} folder(s), {files} file(s), {}", fmt_size(*bytes)));
                ui.add_space(4.0);
                for n in names {
                    ui.label(n);
                }
                if dirs + files > names.len() {
                    ui.weak(format!("… and {} more", dirs + files - names.len()));
                }
            }
            PvData::Hex(s) => {
                ui.weak("Binary file – first bytes:");
                ui.add(egui::Label::new(RichText::new(s).monospace().size(11.5).color(Color32::GRAY)).extend());
            }
            PvData::Note(n) => {
                ui.weak(n);
            }
        });
        close
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn svg_renders_to_image_and_keeps_source() {
        let p = std::env::temp_dir().join(format!("exyex-test-{}.svg", std::process::id()));
        std::fs::write(&p, r##"<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><rect width="40" height="20" fill="#f00"/></svg>"##).unwrap();
        match load_svg(&p) {
            PvData::Svg { img, size, source } => {
                assert_eq!(size, [40, 20]);
                assert_eq!(img.size[0] * 20, img.size[1] * 40); // aspect ratio kept
                assert!(img.pixels.iter().any(|c| c.r() > 200 && c.g() < 50));
                assert!(source.contains("<rect"));
            }
            _ => panic!("expected Svg"),
        }
        let _ = std::fs::remove_file(&p);
    }
}
