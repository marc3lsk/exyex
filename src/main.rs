#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod actions;
mod app;
mod archive;
mod deep;
mod editor;
mod icon_draw;
mod icons;
#[cfg(windows)]
mod install;
mod model;
mod ops;
mod preview;
mod search;
mod sftp;
mod single;
mod store;
mod textdiff;
mod thumbs;

fn main() -> eframe::Result {
    let args: Vec<String> = std::env::args().skip(1).collect();

    #[cfg(windows)]
    {
        match args.first().map(String::as_str) {
            Some("--install") => return Ok(install::run_cli(false)),
            Some("--uninstall") => return Ok(install::run_cli(true)),
            _ => {}
        }
        // `mavyex_<ver>_x64_setup.exe` double-clicked: run the setup wizard.
        let is_setup = std::env::current_exe()
            .ok()
            .and_then(|p| p.file_stem().map(|s| s.to_string_lossy().to_lowercase()))
            .is_some_and(|s| s.ends_with("_setup"));
        if is_setup && args.is_empty() {
            return Ok(install::run_setup_wizard());
        }
    }

    // One window only: a second `mavyex <path>` hands the path to the running
    // instance (opens a new tab there) and exits.
    let server = match single::init(&args) {
        single::Init::Forwarded => return Ok(()),
        single::Init::Primary(s) => Some(s),
        single::Init::Standalone => None,
    };

    let saved = store::Store::load().window.filter(|w| w.on_screen());
    let mut viewport = eframe::egui::ViewportBuilder::default().with_title("mavyex").with_inner_size([1360.0, 820.0]);
    if let Some(w) = saved {
        viewport = viewport.with_inner_size([w.w, w.h]).with_position([w.x, w.y]).with_maximized(w.maximized);
    }
    let options = eframe::NativeOptions {
        viewport: viewport
            .with_min_inner_size([760.0, 420.0])
            .with_drag_and_drop(true)
            .with_icon(eframe::egui::IconData { rgba: icon_draw::render_icon(64), width: 64, height: 64 }),
        ..Default::default()
    };
    eframe::run_native("mavyex", options, Box::new(move |cc| Ok(Box::new(app::App::new(cc, server)))))
}
