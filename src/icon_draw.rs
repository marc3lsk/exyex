// Shared by the app (window/taskbar icon) and build.rs (embedded .exe icon).
// Design: a blue rounded square with two white bars - "two panes".

/// Renders the icon as straight (non-premultiplied) RGBA, `size` x `size`.
pub fn render_icon(size: u32) -> Vec<u8> {
    const SS: u32 = 4; // supersampling per axis
    let inside = |x: f32, y: f32, x0: f32, y0: f32, x1: f32, y1: f32, r: f32| -> bool {
        if x < x0 || x > x1 || y < y0 || y > y1 {
            return false;
        }
        let cx = x.clamp(x0 + r, x1 - r);
        let cy = y.clamp(y0 + r, y1 - r);
        (x - cx).powi(2) + (y - cy).powi(2) <= r * r
    };
    let bg = (76.0f32, 139.0f32, 245.0f32);
    let mut out = vec![0u8; (size * size * 4) as usize];
    for py in 0..size {
        for px in 0..size {
            let (mut r, mut g, mut b, mut n) = (0.0f32, 0.0f32, 0.0f32, 0.0f32);
            for sy in 0..SS {
                for sx in 0..SS {
                    let x = (px as f32 + (sx as f32 + 0.5) / SS as f32) / size as f32;
                    let y = (py as f32 + (sy as f32 + 0.5) / SS as f32) / size as f32;
                    if !inside(x, y, 0.03, 0.03, 0.97, 0.97, 0.22) {
                        continue;
                    }
                    let (mut cr, mut cg, mut cb) = bg;
                    if inside(x, y, 0.20, 0.22, 0.46, 0.78, 0.06) {
                        (cr, cg, cb) = (255.0, 255.0, 255.0);
                    } else if inside(x, y, 0.54, 0.22, 0.80, 0.78, 0.06) {
                        let k = 0.7; // the "other" pane is a bit dimmer
                        cr = bg.0 + (255.0 - bg.0) * k;
                        cg = bg.1 + (255.0 - bg.1) * k;
                        cb = bg.2 + (255.0 - bg.2) * k;
                    }
                    r += cr;
                    g += cg;
                    b += cb;
                    n += 1.0;
                }
            }
            if n > 0.0 {
                let i = ((py * size + px) * 4) as usize;
                out[i] = (r / n).round() as u8;
                out[i + 1] = (g / n).round() as u8;
                out[i + 2] = (b / n).round() as u8;
                out[i + 3] = (255.0 * n / (SS * SS) as f32).round() as u8;
            }
        }
    }
    out
}
