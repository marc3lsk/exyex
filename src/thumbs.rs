//! Image thumbnails for the grid view: decoded on worker threads, cached as textures.

use eframe::egui::{ColorImage, Context, TextureHandle, TextureOptions};
use std::{
    collections::HashMap,
    path::{Path, PathBuf},
    sync::mpsc::{Receiver, Sender, channel},
};

const EXTS: [&str; 9] = ["png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "svg", "tiff"];
const MAX_FILE: u64 = 40 * 1024 * 1024;
const MAX_INFLIGHT: usize = 4;
const MAX_CACHED: usize = 500;
const THUMB: u32 = 192;

enum Slot {
    Loading,
    Ready(TextureHandle),
    Failed,
}

pub struct Thumbs {
    map: HashMap<PathBuf, Slot>,
    tx: Sender<(PathBuf, Option<ColorImage>)>,
    rx: Receiver<(PathBuf, Option<ColorImage>)>,
    inflight: usize,
}

impl Default for Thumbs {
    fn default() -> Self {
        let (tx, rx) = channel();
        Thumbs { map: HashMap::new(), tx, rx, inflight: 0 }
    }
}

pub fn is_thumbnailable(ext: &str, size: u64) -> bool {
    size <= MAX_FILE && EXTS.contains(&ext)
}

fn decode(path: &Path) -> Option<ColorImage> {
    if path.extension().is_some_and(|e| e.eq_ignore_ascii_case("svg")) {
        let data = std::fs::read(path).ok()?;
        return crate::preview::render_svg(&data, THUMB as f32).ok().map(|r| r.0);
    }
    let img = image::open(path).ok()?.thumbnail(THUMB, THUMB).to_rgba8();
    Some(ColorImage::from_rgba_unmultiplied([img.width() as usize, img.height() as usize], img.as_raw()))
}

impl Thumbs {
    /// Uploads finished thumbnails; returns true while more are pending (keep repainting).
    pub fn poll(&mut self, ctx: &Context) -> bool {
        let mut uploads = 0;
        while uploads < 6 {
            let Ok((p, img)) = self.rx.try_recv() else { break };
            self.inflight = self.inflight.saturating_sub(1);
            let slot = match img {
                Some(i) => Slot::Ready(ctx.load_texture(format!("thumb-{}", p.display()), i, TextureOptions::LINEAR)),
                None => Slot::Failed,
            };
            self.map.insert(p, slot);
            uploads += 1;
        }
        self.inflight > 0 || uploads == 6
    }

    /// Texture for `path` if it is ready; otherwise queues the decode (a few at a time).
    pub fn get(&mut self, ctx: &Context, path: &Path) -> Option<&TextureHandle> {
        if !self.map.contains_key(path) {
            if self.inflight >= MAX_INFLIGHT {
                ctx.request_repaint_after(std::time::Duration::from_millis(40));
                return None;
            }
            if self.map.len() > MAX_CACHED {
                self.map.retain(|_, s| matches!(s, Slot::Loading)); // drop old textures
            }
            self.map.insert(path.to_path_buf(), Slot::Loading);
            self.inflight += 1;
            let (p, tx, c) = (path.to_path_buf(), self.tx.clone(), ctx.clone());
            std::thread::spawn(move || {
                let img = decode(&p);
                let _ = tx.send((p, img));
                c.request_repaint();
            });
        }
        match self.map.get(path) {
            Some(Slot::Ready(t)) => Some(t),
            _ => None,
        }
    }
}
