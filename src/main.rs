#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod actions;
mod app;
mod icon_draw;
mod icons;
mod model;
mod ops;
mod store;

fn main() -> eframe::Result {
    let options = eframe::NativeOptions {
        viewport: eframe::egui::ViewportBuilder::default()
            .with_title("exyex")
            .with_inner_size([1360.0, 820.0])
            .with_min_inner_size([760.0, 420.0])
            .with_drag_and_drop(true)
            .with_icon(eframe::egui::IconData { rgba: icon_draw::render_icon(64), width: 64, height: 64 }),
        ..Default::default()
    };
    eframe::run_native("exyex", options, Box::new(|cc| Ok(Box::new(app::App::new(cc)))))
}
