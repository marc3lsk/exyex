//! External editor for F4: a command template such as `code {file}` or
//! `"C:\Program Files\Notepad++\notepad++.exe" -n{line} {file}`, chosen per file extension.

use std::path::{Path, PathBuf};

/// Used when nothing is configured.
pub const DEFAULT: &str = "notepad {file}";

/// Picks the template for `file`: the first matching rule, else `default_cmd`, else [`DEFAULT`].
/// `rules` holds one rule per line, `ext1,ext2 = command` (`*` matches everything, `#` starts a comment).
pub fn command_for(default_cmd: &str, rules: &str, file: &Path) -> String {
    let ext = file.extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default();
    for line in rules.lines().map(str::trim).filter(|l| !l.is_empty() && !l.starts_with('#')) {
        let Some((exts, cmd)) = line.split_once('=') else { continue };
        let hit = exts
            .split([',', ' ', ';'])
            .map(str::trim)
            .filter(|e| !e.is_empty())
            .any(|e| e == "*" || e.trim_start_matches("*.").trim_start_matches('.').to_lowercase() == ext);
        if hit && !cmd.trim().is_empty() {
            return cmd.trim().to_string();
        }
    }
    let d = default_cmd.trim();
    if d.is_empty() { DEFAULT.to_string() } else { d.to_string() }
}

/// Splits a command line into arguments; double quotes group words.
pub fn split_args(s: &str) -> Vec<String> {
    let (mut out, mut cur, mut quoted, mut any) = (vec![], String::new(), false, false);
    for c in s.chars() {
        match c {
            '"' => {
                quoted = !quoted;
                any = true;
            }
            c if c.is_whitespace() && !quoted => {
                if any || !cur.is_empty() {
                    out.push(std::mem::take(&mut cur));
                    any = false;
                }
            }
            c => cur.push(c),
        }
    }
    if any || !cur.is_empty() {
        out.push(cur);
    }
    out
}

/// Program + arguments with `{file}`, `{dir}` and `{line}` filled in. The file is appended
/// when the template does not mention it.
pub fn build(template: &str, file: &Path) -> Vec<String> {
    let f = file.to_string_lossy();
    let dir = file.parent().map(|p| p.to_string_lossy().into_owned()).unwrap_or_default();
    let mut parts = split_args(template);
    let uses_file = parts.iter().any(|p| p.contains("{file}"));
    for p in &mut parts {
        *p = p.replace("{file}", &f).replace("{dir}", &dir).replace("{line}", "1");
    }
    if !uses_file {
        parts.push(f.into_owned());
    }
    parts
}

fn resolve_program(name: &str) -> PathBuf {
    const EXTS: [&str; 5] = ["", ".exe", ".cmd", ".bat", ".com"];
    let has_dir = name.contains(['\\', '/']);
    let dirs: Vec<PathBuf> = if has_dir { vec![PathBuf::new()] } else { std::env::var_os("PATH").map(|p| std::env::split_paths(&p).collect()).unwrap_or_default() };
    for d in dirs {
        for e in EXTS {
            let cand = d.join(format!("{name}{e}"));
            if cand.is_file() && cand.extension().is_some() {
                return cand;
            }
        }
    }
    PathBuf::from(name)
}

/// Starts the editor and returns immediately.
pub fn launch(template: &str, file: &Path) -> Result<(), String> {
    let parts = build(template, file);
    let Some((prog, args)) = parts.split_first() else { return Err("empty editor command".into()) };
    let prog = resolve_program(prog);
    let mut cmd = std::process::Command::new(&prog);
    cmd.args(args);
    if let Some(d) = file.parent().filter(|d| d.is_dir()) {
        cmd.current_dir(d);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        let batch = prog.extension().is_some_and(|e| e.eq_ignore_ascii_case("cmd") || e.eq_ignore_ascii_case("bat"));
        if batch {
            cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW: no console flash for code.cmd and friends
        }
    }
    cmd.spawn().map(|_| ()).map_err(|e| format!("could not start \"{}\": {e}", prog.display()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn picks_rule_by_extension_then_default() {
        let rules = "# comment\nrs, toml = code {file}\n*.md = typora\n.log = \"C:\\Tools\\log viewer.exe\" {file}";
        assert_eq!(command_for("", rules, Path::new("a.RS")), "code {file}");
        assert_eq!(command_for("", rules, Path::new("x.log")), "\"C:\\Tools\\log viewer.exe\" {file}");
        assert_eq!(command_for("vim", rules, Path::new("a.txt")), "vim");
        assert_eq!(command_for("  ", rules, Path::new("a.txt")), DEFAULT);
    }

    #[test]
    fn builds_arguments_with_placeholders() {
        let f = Path::new("C:\\my dir\\a b.txt");
        assert_eq!(build("\"C:\\Program Files\\N\\n.exe\" -n{line} {file}", f), ["C:\\Program Files\\N\\n.exe", "-n1", "C:\\my dir\\a b.txt"]);
        assert_eq!(build("code", f), ["code", "C:\\my dir\\a b.txt"]); // file appended
        assert_eq!(build("ed --dir={dir} {file}", f)[1], "--dir=C:\\my dir");
        assert_eq!(split_args("a \"\" b"), ["a", "", "b"]);
    }
}
