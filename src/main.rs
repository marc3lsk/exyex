#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod actions;
mod app;
mod icon_draw;
mod icons;
#[cfg(windows)]
mod install;
mod model;
mod ops;
mod single;
mod store;

fn main() -> eframe::Result {
    let args: Vec<String> = std::env::args().skip(1).collect();

    #[cfg(windows)]
    {
        match args.first().map(String::as_str) {
            Some("--install") => return Ok(install::run_cli(false)),
            Some("--uninstall") => return Ok(install::run_cli(true)),
            _ => {}
        }
        // `exyex_<ver>_x64_setup.exe` double-clicked: run the setup wizard.
        let is_setup = std::env::current_exe()
            .ok()
            .and_then(|p| p.file_stem().map(|s| s.to_string_lossy().to_lowercase()))
            .is_some_and(|s| s.ends_with("_setup"));
        if is_setup && args.is_empty() {
            return Ok(install::run_setup_wizard());
        }
    }

    // One window only: a second `exyex <path>` hands the path to the running
    // instance (opens a new tab there) and exits.
    let server = match single::init(&args) {
        single::Init::Forwarded => return Ok(()),
        single::Init::Primary(s) => Some(s),
        single::Init::Standalone => None,
    };

    let options = eframe::NativeOptions {
        viewport: eframe::egui::ViewportBuilder::default()
            .with_title("exyex")
            .with_inner_size([1360.0, 820.0])
            .with_min_inner_size([760.0, 420.0])
            .with_drag_and_drop(true)
            .with_icon(eframe::egui::IconData { rgba: icon_draw::render_icon(64), width: 64, height: 64 }),
        ..Default::default()
    };
    eframe::run_native("exyex", options, Box::new(move |cc| Ok(Box::new(app::App::new(cc, server)))))
}
