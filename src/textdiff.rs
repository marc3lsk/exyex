//! Side-by-side text diff of two files (window opened by the "Compare files" action).

use eframe::egui::{self, Align2, Color32, Context, FontId, Key, Rect, Sense, TextStyle, Ui, pos2, vec2};
use egui::text::{LayoutJob, TextFormat};
use similar::{Algorithm, ChangeTag, DiffOp, TextDiff as CharDiff, capture_diff_slices};
use std::{
    io::Read,
    path::{Path, PathBuf},
    sync::mpsc,
};

/// Files bigger than this are refused (the diff is computed in memory).
const MAX: usize = 4 * 1024 * 1024;
const NUM_W: f32 = 46.0;
const GAP: f32 = 6.0;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Kind {
    Same,
    Del,
    Ins,
    Chg,
}

#[derive(Clone, Copy, Debug)]
struct Row {
    kind: Kind,
    l: Option<usize>,
    r: Option<usize>,
}

type Loaded = Result<(String, String), String>;

pub struct TextDiff {
    title_l: String,
    title_r: String,
    rx: Option<mpsc::Receiver<Loaded>>,
    error: Option<String>,
    left: Vec<String>,
    right: Vec<String>,
    rows: Vec<Row>,
    /// Row index where each block of differences starts.
    hunks: Vec<usize>,
    cur: Option<usize>,
    ignore_ws: bool,
    ignore_case: bool,
    jump: Option<usize>,
    added: usize,
    removed: usize,
    /// Theme of this window (independent of the main window).
    pub dark: bool,
    /// The user flipped the theme here (so the app should remember it).
    pub touched: bool,
    theme_sent: Option<bool>,
}

fn decode(b: Vec<u8>) -> Result<String, String> {
    if b.len() > MAX {
        return Err(format!("File is larger than {} MB", MAX / 1024 / 1024));
    }
    if b.starts_with(&[0xFF, 0xFE]) || b.starts_with(&[0xFE, 0xFF]) {
        let le = b[0] == 0xFF;
        let u: Vec<u16> = b[2..].chunks_exact(2).map(|c| if le { u16::from_le_bytes([c[0], c[1]]) } else { u16::from_be_bytes([c[0], c[1]]) }).collect();
        return Ok(String::from_utf16_lossy(&u));
    }
    if b.iter().take(8192).any(|&c| c == 0) {
        return Err("Binary file – text compare needs text files".into());
    }
    let b = b.strip_prefix(&[0xEF, 0xBB, 0xBF]).map(<[u8]>::to_vec).unwrap_or(b);
    Ok(match String::from_utf8(b) {
        Ok(s) => s,
        Err(e) => String::from_utf8_lossy(e.as_bytes()).into_owned(),
    })
}

fn read_text(p: &Path) -> Result<String, String> {
    let bytes = if crate::sftp::is_remote(p) {
        crate::sftp::read_head(p, MAX + 1).map_err(|e| e.to_string())?
    } else if let Some((zip, inner)) = crate::archive::split_inner(p) {
        crate::archive::read_head(&zip, &inner, MAX + 1).map_err(|e| e.to_string())?
    } else {
        let mut v = Vec::new();
        std::fs::File::open(p).and_then(|f| f.take(MAX as u64 + 1).read_to_end(&mut v)).map_err(|e| e.to_string())?;
        v
    };
    decode(bytes).map_err(|e| format!("{}: {e}", p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()))
}

fn norm(s: &str, ws: bool, case: bool) -> String {
    let mut t = if ws { s.split_whitespace().collect::<Vec<_>>().join(" ") } else { s.to_string() };
    if case {
        t = t.to_lowercase();
    }
    t
}

impl TextDiff {
    pub fn open(ctx: &Context, a: PathBuf, b: PathBuf, dark: bool) -> Self {
        let (tx, rx) = mpsc::channel();
        let c = ctx.clone();
        let (pa, pb) = (a.clone(), b.clone());
        std::thread::spawn(move || {
            let _ = tx.send(read_text(&pa).and_then(|x| read_text(&pb).map(|y| (x, y))));
            c.request_repaint();
        });
        let name = |p: &Path| crate::sftp::pretty(p).unwrap_or_else(|| p.to_string_lossy().into_owned());
        TextDiff {
            title_l: name(&a),
            title_r: name(&b),
            rx: Some(rx),
            error: None,
            left: vec![],
            right: vec![],
            rows: vec![],
            hunks: vec![],
            cur: None,
            ignore_ws: false,
            ignore_case: false,
            jump: None,
            added: 0,
            removed: 0,
            dark,
            touched: false,
            theme_sent: None,
        }
    }

    fn poll(&mut self) {
        let Some(rx) = &self.rx else { return };
        match rx.try_recv() {
            Ok(Ok((a, b))) => {
                self.left = a.lines().map(str::to_string).collect();
                self.right = b.lines().map(str::to_string).collect();
                self.rx = None;
                self.rebuild();
            }
            Ok(Err(e)) => {
                self.error = Some(e);
                self.rx = None;
            }
            Err(mpsc::TryRecvError::Disconnected) => {
                self.error = Some("Loading failed".into());
                self.rx = None;
            }
            Err(mpsc::TryRecvError::Empty) => {}
        }
    }

    /// Computes the aligned rows (pure; see the tests).
    fn rebuild(&mut self) {
        let (rows, added, removed) = diff_rows(&self.left, &self.right, self.ignore_ws, self.ignore_case);
        self.hunks = rows.iter().enumerate().filter(|(i, r)| r.kind != Kind::Same && (*i == 0 || rows[i - 1].kind == Kind::Same)).map(|(i, _)| i).collect();
        self.rows = rows;
        self.added = added;
        self.removed = removed;
        self.cur = None;
    }

    /// Shows the diff in its own OS window. Returns false when it should close.
    pub fn ui(&mut self, ctx: &Context) -> bool {
        self.poll();
        let title = format!("mavyex — Compare: {}  ↔  {}", self.title_l, self.title_r);
        let builder = egui::ViewportBuilder::default()
            .with_title(title)
            .with_inner_size([1100.0, 680.0])
            .with_min_inner_size([480.0, 240.0])
            .with_icon(egui::IconData { rgba: crate::icon_draw::render_icon(64), width: 64, height: 64 });
        let mut close = false;
        ctx.show_viewport_immediate(egui::ViewportId::from_hash_of("mavyex-textdiff"), builder, |ui, class| {
            if ui.ctx().input(|i| i.viewport().close_requested() || i.key_pressed(Key::Escape)) {
                close = true;
                return;
            }
            // This window has its own theme: the style is set on the panel, not on the shared context.
            let theme = if self.dark { egui::Theme::Dark } else { egui::Theme::Light };
            let visuals = ui.ctx().style_of(theme).visuals.clone();
            if self.theme_sent != Some(self.dark) {
                self.theme_sent = Some(self.dark);
                ui.ctx().send_viewport_cmd(egui::ViewportCommand::SetTheme(if self.dark { egui::SystemTheme::Dark } else { egui::SystemTheme::Light }));
            }
            if class == egui::ViewportClass::EmbeddedWindow {
                // Backend without multi-window support: fall back to an in-app window.
                egui::Window::new("Compare").default_size(vec2(1100.0, 650.0)).show(ui.ctx(), |ui| self.body(ui));
            } else {
                egui::CentralPanel::default().frame(egui::Frame::new().fill(visuals.panel_fill).inner_margin(8.0)).show(ui, |ui| {
                    ui.style_mut().visuals = visuals;
                    self.body(ui);
                });
            }
        });
        !close
    }

    fn goto(&mut self, next: bool) {
        let n = self.hunks.len();
        if n == 0 {
            return;
        }
        let i = match (self.cur, next) {
            (None, true) => 0,
            (None, false) => n - 1,
            (Some(i), true) => (i + 1) % n,
            (Some(i), false) => (i + n - 1) % n,
        };
        self.cur = Some(i);
        self.jump = Some(self.hunks[i]);
    }

    fn theme_button(&mut self, ui: &mut Ui) {
        let (icon, tip) = if self.dark { ("☀", "Switch this window to the light theme") } else { ("🌙", "Switch this window to the dark theme") };
        if ui.button(icon).on_hover_text(tip).clicked() {
            self.dark = !self.dark;
            self.touched = true;
        }
    }

    fn body(&mut self, ui: &mut Ui) {
        if let Some(e) = self.error.clone() {
            ui.horizontal(|ui| {
                self.theme_button(ui);
                ui.colored_label(Color32::LIGHT_RED, e);
            });
            return;
        }
        if self.rx.is_some() {
            ui.horizontal(|ui| {
                self.theme_button(ui);
                ui.spinner();
                ui.label("Loading…");
            });
            return;
        }
        let (f7, f8) = ui.ctx().input(|i| (i.key_pressed(Key::F7), i.key_pressed(Key::F8)));
        let mut rebuild = false;
        ui.horizontal(|ui| {
            self.theme_button(ui);
            if ui.button("⏶ Previous (F7)").clicked() || f7 {
                self.goto(false);
            }
            if ui.button("⏷ Next (F8)").clicked() || f8 {
                self.goto(true);
            }
            rebuild |= ui.checkbox(&mut self.ignore_ws, "Ignore whitespace").changed();
            rebuild |= ui.checkbox(&mut self.ignore_case, "Ignore case").changed();
            ui.separator();
            if self.hunks.is_empty() {
                ui.strong("Files are identical");
            } else {
                let at = self.cur.map(|c| format!("{} / ", c + 1)).unwrap_or_default();
                ui.label(format!("{at}{} differences   +{} −{} lines", self.hunks.len(), self.added, self.removed));
            }
        });
        if rebuild {
            self.rebuild();
        }
        ui.separator();

        let dark = ui.visuals().dark_mode;
        let font = TextStyle::Monospace.resolve(ui.style());
        let rh = ui.text_style_height(&TextStyle::Monospace) + 3.0;
        ui.spacing_mut().item_spacing.y = 0.0;
        let mut area = egui::ScrollArea::vertical().auto_shrink([false, false]);
        if let Some(r) = self.jump.take() {
            area = area.vertical_scroll_offset((r as f32 - 2.0).max(0.0) * rh);
        }
        let (left, right, rows) = (&self.left, &self.right, &self.rows);
        area.show_rows(ui, rh, rows.len(), |ui, range| {
            for ri in range {
                let row = rows[ri];
                let (rect, _) = ui.allocate_exact_size(vec2(ui.available_width(), rh), Sense::hover());
                let half = (rect.width() - GAP) / 2.0;
                let lr = Rect::from_min_size(rect.min, vec2(half, rh));
                let rr = Rect::from_min_size(pos2(rect.min.x + half + GAP, rect.min.y), vec2(half, rh));
                let lt = row.l.map(|i| left[i].as_str());
                let rt = row.r.map(|i| right[i].as_str());
                paint_side(ui, lr, row.l, lt, rt, row.kind, true, dark, &font);
                paint_side(ui, rr, row.r, rt, lt, row.kind, false, dark, &font);
            }
        });
    }
}

/// Aligned side-by-side rows plus (added, removed) line counts.
fn diff_rows(left: &[String], right: &[String], ws: bool, case: bool) -> (Vec<Row>, usize, usize) {
    let nl: Vec<String> = left.iter().map(|s| norm(s, ws, case)).collect();
    let nr: Vec<String> = right.iter().map(|s| norm(s, ws, case)).collect();
    let (a, b): (Vec<&str>, Vec<&str>) = (nl.iter().map(String::as_str).collect(), nr.iter().map(String::as_str).collect());
    let mut rows = Vec::new();
    let (mut added, mut removed) = (0, 0);
    for op in capture_diff_slices(Algorithm::Myers, &a, &b) {
        match op {
            DiffOp::Equal { old_index, new_index, len } => {
                for k in 0..len {
                    rows.push(Row { kind: Kind::Same, l: Some(old_index + k), r: Some(new_index + k) });
                }
            }
            DiffOp::Delete { old_index, old_len, .. } => {
                removed += old_len;
                for k in 0..old_len {
                    rows.push(Row { kind: Kind::Del, l: Some(old_index + k), r: None });
                }
            }
            DiffOp::Insert { new_index, new_len, .. } => {
                added += new_len;
                for k in 0..new_len {
                    rows.push(Row { kind: Kind::Ins, l: None, r: Some(new_index + k) });
                }
            }
            DiffOp::Replace { old_index, old_len, new_index, new_len } => {
                removed += old_len;
                added += new_len;
                for k in 0..old_len.max(new_len) {
                    let l = (k < old_len).then_some(old_index + k);
                    let r = (k < new_len).then_some(new_index + k);
                    let kind = match (l, r) {
                        (Some(_), Some(_)) => Kind::Chg,
                        (Some(_), None) => Kind::Del,
                        _ => Kind::Ins,
                    };
                    rows.push(Row { kind, l, r });
                }
            }
        }
    }
    (rows, added, removed)
}

/// Splits `mine` into (text, changed) pieces by a character diff against `other`.
fn segments(mine: &str, other: &str, left: bool) -> Vec<(String, bool)> {
    let (a, b) = if left { (mine, other) } else { (other, mine) };
    if a.len() > 1500 || b.len() > 1500 {
        return vec![(mine.to_string(), true)];
    }
    let mut out: Vec<(String, bool)> = Vec::new();
    for c in CharDiff::from_chars(a, b).iter_all_changes() {
        let hl = match c.tag() {
            ChangeTag::Equal => false,
            ChangeTag::Delete if left => true,
            ChangeTag::Insert if !left => true,
            _ => continue,
        };
        match out.last_mut() {
            Some((s, h)) if *h == hl => s.push_str(c.value()),
            _ => out.push((c.value().to_string(), hl)),
        }
    }
    out
}

fn colors(kind: Kind, dark: bool) -> (Color32, Color32) {
    let c = Color32::from_rgb;
    match (kind, dark) {
        (Kind::Same, _) => (Color32::TRANSPARENT, Color32::TRANSPARENT),
        (Kind::Chg, true) => (c(78, 62, 28), c(140, 105, 35)),
        (Kind::Chg, false) => (c(255, 241, 212), c(255, 214, 140)),
        (Kind::Del, true) => (c(92, 36, 42), c(150, 50, 60)),
        (Kind::Del, false) => (c(255, 226, 229), c(255, 170, 180)),
        (_, true) => (c(30, 76, 46), c(45, 120, 65)),
        (_, false) => (c(222, 247, 229), c(150, 225, 170)),
    }
}

#[allow(clippy::too_many_arguments)]
fn paint_side(ui: &Ui, rect: Rect, idx: Option<usize>, mine: Option<&str>, other: Option<&str>, kind: Kind, left: bool, dark: bool, font: &FontId) {
    let p = ui.painter().with_clip_rect(rect.intersect(ui.clip_rect()));
    let (Some(i), Some(text)) = (idx, mine) else {
        p.rect_filled(rect, 0.0, if dark { Color32::from_gray(34) } else { Color32::from_gray(238) });
        return;
    };
    // Only the side that carries the change is tinted (Del: left, Ins: right, Chg: both).
    let tinted = match kind {
        Kind::Same => false,
        Kind::Chg => true,
        Kind::Del => left,
        Kind::Ins => !left,
    };
    let (bg, hl) = if tinted { colors(kind, dark) } else { (Color32::TRANSPARENT, Color32::TRANSPARENT) };
    p.rect_filled(rect, 0.0, bg);
    p.text(pos2(rect.left() + NUM_W - 6.0, rect.center().y), Align2::RIGHT_CENTER, (i + 1).to_string(), font.clone(), ui.visuals().weak_text_color());
    let text = text.replace('\t', "    ");
    let segs = match (kind, other) {
        (Kind::Chg, Some(o)) => segments(&text, &o.replace('\t', "    "), left),
        _ => vec![(text.clone(), false)],
    };
    let color = ui.visuals().text_color();
    let mut job = LayoutJob::default();
    job.wrap.max_width = f32::INFINITY;
    let mut used = 0usize;
    for (s, changed) in segs {
        if used > 600 {
            break;
        }
        let s: String = s.chars().take(600 - used.min(600)).collect();
        used += s.chars().count();
        job.append(&s, 0.0, TextFormat { font_id: font.clone(), color, background: if changed { hl } else { Color32::TRANSPARENT }, ..Default::default() });
    }
    let galley = ui.painter().layout_job(job);
    let pos = pos2(rect.left() + NUM_W, rect.center().y - galley.size().y / 2.0);
    p.galley(pos, galley, color);
}

#[cfg(test)]
mod tests {
    use super::*;

    fn lines(s: &str) -> Vec<String> {
        s.lines().map(str::to_string).collect()
    }

    #[test]
    fn aligns_changes_inserts_and_deletes() {
        let (rows, added, removed) = diff_rows(&lines("a\nb\nc\nd"), &lines("a\nB\nc\nx\nd"), false, false);
        let kinds: Vec<Kind> = rows.iter().map(|r| r.kind).collect();
        assert_eq!(kinds, [Kind::Same, Kind::Chg, Kind::Same, Kind::Ins, Kind::Same]);
        assert_eq!((added, removed), (2, 1));
    }

    #[test]
    fn whitespace_and_case_can_be_ignored() {
        let (l, r) = (lines("Hello   world\nfoo"), lines("hello world\nfoo"));
        assert_eq!(diff_rows(&l, &r, false, false).0.iter().filter(|r| r.kind != Kind::Same).count(), 1);
        assert!(diff_rows(&l, &r, true, true).0.iter().all(|r| r.kind == Kind::Same));
    }

    #[test]
    fn inline_segments_mark_only_the_changed_chars() {
        let s = segments("color red", "color blue", true);
        assert_eq!(s.iter().filter(|(_, h)| *h).map(|(t, _)| t.as_str()).collect::<String>(), "rd");
        assert!(decode(vec![0, 1, 2]).is_err());
        assert_eq!(decode(vec![0xEF, 0xBB, 0xBF, b'h', b'i']).unwrap(), "hi");
    }
}
