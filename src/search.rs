//! In-folder search: Smart / Fuzzy / Wildcard / Regex matching with scoring
//! (for ranking) and match positions (for highlighting).

use crate::model::Entry;
use regex::{Regex, RegexBuilder};
use std::time::{Duration, SystemTime};
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug, Default)]
pub enum SearchMode {
    /// Space separated words: plain text = contains, `*`/`?` = wildcard,
    /// `!word` = exclude, leading `~` = fuzzy, leading `re:` = regex.
    #[default]
    Smart,
    Fuzzy,
    Wildcard,
    Regex,
}

impl SearchMode {
    pub const ALL: [SearchMode; 4] = [SearchMode::Smart, SearchMode::Fuzzy, SearchMode::Wildcard, SearchMode::Regex];

    pub fn label(self) -> &'static str {
        match self {
            SearchMode::Smart => "Smart",
            SearchMode::Fuzzy => "Fuzzy",
            SearchMode::Wildcard => "Wildcard",
            SearchMode::Regex => "Regex",
        }
    }

    /// Short glyph for the compact search bar (the tooltip carries the full name).
    pub fn icon(self) -> &'static str {
        match self {
            SearchMode::Smart => "✦",
            SearchMode::Fuzzy => "≈",
            SearchMode::Wildcard => "*",
            SearchMode::Regex => ".*",
        }
    }

    pub fn hint(self) -> &'static str {
        match self {
            SearchMode::Smart => "type to filter  ·  *.rs  ·  !exclude  ·  ~fuzzy  ·  re:regex  ·  :size>10mb  :age<7d  :type=img",
            SearchMode::Fuzzy => "fuzzy: letters in order, e.g. rdme finds README.md",
            SearchMode::Wildcard => "wildcard: * and ?, e.g. *.rs or report_??.pdf",
            SearchMode::Regex => "regular expression, e.g. ^img_\\d+\\.(jpg|png)$",
        }
    }

    pub fn tooltip(self) -> &'static str {
        match self {
            SearchMode::Smart => "Smart (Alt+1)\nWords are combined (AND). Plain text = contains,\n*/? = wildcard, !word = exclude, ~text = fuzzy, re:pattern = regex",
            SearchMode::Fuzzy => "Fuzzy (Alt+2)\nThe letters must appear in this order, not necessarily next to each other.\nBest matches come first.",
            SearchMode::Wildcard => "Wildcard (Alt+3)\n* = any text, ? = one character. The whole name must match;\nwithout * or ? the text is matched anywhere in the name.",
            SearchMode::Regex => "Regex (Alt+4)\nRust regular expression, matched anywhere in the name.",
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug, Default)]
#[serde(default)]
pub struct SearchOpts {
    pub mode: SearchMode,
    pub match_case: bool,
    /// Also search inside subfolders (background walk).
    pub deep: bool,
    /// Search the *contents* of files (implies subfolders).
    pub content: bool,
}

enum Tok {
    Sub(Vec<char>),
    Glob(String),
}

enum Kind {
    All,
    Smart(Vec<(Tok, bool)>), // (token, negated)
    Fuzzy(Vec<char>),
    Wild(String),
    Re(Regex),
    Bad(String),
}

pub struct Matcher {
    kind: Kind,
    case: bool,
    filters: Vec<Filter>,
}

impl Default for Matcher {
    fn default() -> Self {
        Matcher { kind: Kind::All, case: false, filters: vec![] }
    }
}

fn fold(c: char, case: bool) -> char {
    if case { c } else { c.to_lowercase().next().unwrap_or(c) }
}

fn folded(s: &str, case: bool) -> Vec<char> {
    s.chars().map(|c| fold(c, case)).collect()
}

/// Case-aware glob with `*` and `?`.
pub fn glob_match(pat: &str, text: &str, case: bool) -> bool {
    let p = folded(pat, case);
    let t = folded(text, case);
    let (mut pi, mut ti, mut star, mut mark) = (0, 0, None, 0);
    while ti < t.len() {
        if pi < p.len() && (p[pi] == '?' || p[pi] == t[ti]) {
            pi += 1;
            ti += 1;
        } else if pi < p.len() && p[pi] == '*' {
            star = Some(pi);
            mark = ti;
            pi += 1;
        } else if let Some(s) = star {
            pi = s + 1;
            mark += 1;
            ti = mark;
        } else {
            return false;
        }
    }
    while pi < p.len() && p[pi] == '*' {
        pi += 1;
    }
    pi == p.len()
}

fn find_sub(hay: &[char], needle: &[char]) -> Option<usize> {
    if needle.is_empty() {
        return Some(0);
    }
    hay.windows(needle.len()).position(|w| w == needle)
}

fn fuzzy_match(q: &[char], name: &str, case: bool) -> Option<(i32, Vec<usize>)> {
    if q.is_empty() {
        return Some((0, vec![]));
    }
    let orig: Vec<char> = name.chars().collect();
    let t: Vec<char> = orig.iter().map(|&c| fold(c, case)).collect();
    // forward pass: where does the leftmost match end?
    let (mut qi, mut end) = (0, 0);
    for (i, &c) in t.iter().enumerate() {
        if c == q[qi] {
            qi += 1;
            if qi == q.len() {
                end = i;
                break;
            }
        }
    }
    if qi < q.len() {
        return None;
    }
    // backward pass: tighten the window so the match is as compact as possible
    let mut pos = vec![0usize; q.len()];
    let (mut qj, mut i) = (q.len(), end as isize);
    while qj > 0 && i >= 0 {
        if t[i as usize] == q[qj - 1] {
            qj -= 1;
            pos[qj] = i as usize;
        }
        i -= 1;
    }
    let mut score = 0i32;
    let mut prev: Option<usize> = None;
    for &p in &pos {
        let mut s = 16;
        if let Some(pp) = prev {
            if pp + 1 == p {
                s += 24; // consecutive
            } else {
                s -= ((p - pp - 1) as i32).min(8); // gap penalty
            }
        }
        if p == 0 || !orig[p - 1].is_alphanumeric() {
            s += 14; // start of a word
        } else if orig[p].is_uppercase() && orig[p - 1].is_lowercase() {
            s += 10; // camelCase boundary
        }
        score += s;
        prev = Some(p);
    }
    // a compact match beats one spread over the whole name
    let span = pos[pos.len() - 1] - pos[0] + 1;
    score -= (span - q.len()) as i32 * 2;
    if pos[0] == 0 {
        score += 10;
    }
    score -= orig.len() as i32 / 6; // shorter names win ties
    Some((score, pos))
}

fn positions_to_ranges(pos: &[usize]) -> Vec<(usize, usize)> {
    let mut out: Vec<(usize, usize)> = vec![];
    for &p in pos {
        match out.last_mut() {
            Some(last) if last.1 == p => last.1 = p + 1,
            _ => out.push((p, p + 1)),
        }
    }
    out
}

fn merge(mut v: Vec<(usize, usize)>) -> Vec<(usize, usize)> {
    v.sort();
    let mut out: Vec<(usize, usize)> = vec![];
    for r in v {
        match out.last_mut() {
            Some(last) if r.0 <= last.1 => last.1 = last.1.max(r.1),
            _ => out.push(r),
        }
    }
    out
}

fn build_regex(pat: &str, case: bool) -> Kind {
    match RegexBuilder::new(pat).case_insensitive(!case).size_limit(1 << 20).build() {
        Ok(r) => Kind::Re(r),
        Err(e) => {
            // keep it short: regex errors are multi-line
            let msg = e.to_string();
            Kind::Bad(msg.lines().last().unwrap_or("invalid regex").trim().to_string())
        }
    }
}

// ------------------------------------------------------------------ filters

#[derive(Clone, Copy, PartialEq, Debug)]
enum Cmp {
    Gt,
    Ge,
    Lt,
    Le,
    Eq,
}

impl Cmp {
    fn test<T: PartialOrd>(self, a: T, b: T) -> bool {
        match self {
            Cmp::Gt => a > b,
            Cmp::Ge => a >= b,
            Cmp::Lt => a < b,
            Cmp::Le => a <= b,
            Cmp::Eq => a == b,
        }
    }
}

#[derive(Clone, Debug)]
enum Filter {
    Size(Cmp, u64),
    /// Age of the modification time in seconds (`:age<7d` = modified within the last 7 days).
    Age(Cmp, u64),
    /// Absolute modification date (start of day).
    Date(Cmp, SystemTime),
    /// Kinds: group names (img, video, ...), `dir`, `file` or plain extensions.
    Kind(Vec<String>),
}

const GROUPS: &[(&str, &[&str])] = &[
    ("img", &["png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "svg", "tif", "tiff", "heic"]),
    ("video", &["mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v"]),
    ("audio", &["mp3", "wav", "flac", "ogg", "m4a", "aac", "wma"]),
    ("doc", &["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "txt", "md", "rtf", "csv"]),
    ("archive", &["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "jar"]),
    ("code", &["rs", "py", "js", "ts", "tsx", "jsx", "java", "c", "cpp", "h", "hpp", "cs", "go", "rb", "php", "sh", "ps1", "json", "toml", "yaml", "yml", "xml", "html", "css", "sql"]),
];

fn parse_op(s: &str) -> Option<(Cmp, &str)> {
    for (p, c) in [(">=", Cmp::Ge), ("<=", Cmp::Le), (">", Cmp::Gt), ("<", Cmp::Lt), ("=", Cmp::Eq), (":", Cmp::Eq)] {
        if let Some(r) = s.strip_prefix(p) {
            return Some((c, r));
        }
    }
    None
}

fn parse_size(v: &str) -> Option<u64> {
    let v = v.trim().to_lowercase();
    let i = v.find(|c: char| !(c.is_ascii_digit() || c == '.')).unwrap_or(v.len());
    let n: f64 = v[..i].parse().ok()?;
    let mul: f64 = match v[i..].trim() {
        "" | "b" => 1.0,
        "k" | "kb" | "kib" => 1024.0,
        "m" | "mb" | "mib" => 1024.0 * 1024.0,
        "g" | "gb" | "gib" => 1024.0 * 1024.0 * 1024.0,
        "t" | "tb" | "tib" => 1024.0_f64.powi(4),
        _ => return None,
    };
    Some((n * mul) as u64)
}

fn parse_age(v: &str) -> Option<u64> {
    let v = v.trim().to_lowercase();
    let i = v.find(|c: char| !(c.is_ascii_digit() || c == '.'))?;
    let n: f64 = v[..i].parse().ok()?;
    let unit: f64 = match &v[i..] {
        "min" => 60.0,
        "h" => 3600.0,
        "d" => 86400.0,
        "w" => 7.0 * 86400.0,
        "mo" => 30.0 * 86400.0,
        "y" => 365.0 * 86400.0,
        _ => return None,
    };
    Some((n * unit) as u64)
}

fn parse_date(v: &str) -> Option<SystemTime> {
    use chrono::TimeZone;
    let d = chrono::NaiveDate::parse_from_str(v.trim(), "%Y-%m-%d").ok()?;
    chrono::Local.from_local_datetime(&d.and_hms_opt(0, 0, 0)?).single().map(SystemTime::from)
}

/// `Ok(None)` = not a filter token (plain text); `Err` = looks like one but is malformed.
fn parse_filter(tok: &str) -> Result<Option<Filter>, String> {
    let Some(body) = tok.strip_prefix(':') else { return Ok(None) };
    let key_end = body.find(|c: char| !c.is_ascii_alphabetic()).unwrap_or(body.len());
    let (key, rest) = body.split_at(key_end);
    let key = key.to_lowercase();
    if !matches!(key.as_str(), "size" | "age" | "modified" | "mod" | "date" | "type" | "ext") {
        return Ok(None);
    }
    let bad = || format!("bad filter {tok}");
    let (cmp, val) = parse_op(rest).ok_or_else(bad)?;
    if val.is_empty() {
        return Err(bad());
    }
    match key.as_str() {
        "size" => parse_size(val).map(|n| Some(Filter::Size(cmp, n))).ok_or_else(bad),
        "age" => parse_age(val).map(|n| Some(Filter::Age(cmp, n))).ok_or_else(bad),
        "modified" | "mod" | "date" => {
            if let Some(n) = parse_age(val) {
                Ok(Some(Filter::Age(cmp, n)))
            } else {
                parse_date(val).map(|d| Some(Filter::Date(cmp, d))).ok_or_else(bad)
            }
        }
        _ => Ok(Some(Filter::Kind(
            val.to_lowercase().split(',').map(|s| s.trim().trim_start_matches('.').to_string()).filter(|s| !s.is_empty()).collect(),
        ))),
    }
}

impl Filter {
    fn test(&self, e: &Entry) -> bool {
        match self {
            Filter::Size(c, n) => !e.is_dir && c.test(e.size, *n),
            Filter::Age(c, secs) => e
                .modified
                .is_some_and(|m| c.test(SystemTime::now().duration_since(m).unwrap_or(Duration::ZERO).as_secs(), *secs)),
            Filter::Date(c, d) => e.modified.is_some_and(|m| {
                if *c == Cmp::Eq {
                    m >= *d && m < *d + Duration::from_secs(86400)
                } else {
                    c.test(m, *d)
                }
            }),
            Filter::Kind(kinds) => kinds.iter().any(|k| match k.as_str() {
                "dir" | "folder" => e.is_dir,
                "file" => !e.is_dir,
                _ => !e.is_dir && (GROUPS.iter().any(|(g, exts)| g == k && exts.contains(&e.ext.as_str())) || e.ext == *k),
            }),
        }
    }
}

/// Compiles a query: `:size>10mb`, `:age<7d`, `:type=img` tokens are filters on the
/// item's metadata, everything else is matched against the name by the chosen mode.
pub fn compile(query: &str, opts: SearchOpts) -> Matcher {
    let mut filters = vec![];
    let mut rest: Vec<&str> = vec![];
    for tok in query.split_whitespace() {
        match parse_filter(tok) {
            Ok(Some(f)) => filters.push(f),
            Ok(None) => rest.push(tok),
            Err(e) => return Matcher { kind: Kind::Bad(e), case: opts.match_case, filters: vec![] },
        }
    }
    let mut m = compile_text(&rest.join(" "), opts);
    m.filters = filters;
    m
}

fn compile_text(query: &str, opts: SearchOpts) -> Matcher {
    let q = query.trim();
    let case = opts.match_case;
    if q.is_empty() {
        return Matcher { kind: Kind::All, case, filters: vec![] };
    }
    let fuzzy = |s: &str| Kind::Fuzzy(s.chars().filter(|c| !c.is_whitespace()).map(|c| fold(c, case)).collect());
    let kind = match opts.mode {
        SearchMode::Fuzzy => fuzzy(q),
        SearchMode::Regex => build_regex(q, case),
        SearchMode::Wildcard => {
            if q.contains(['*', '?']) { Kind::Wild(q.to_string()) } else { Kind::Wild(format!("*{q}*")) }
        }
        SearchMode::Smart => {
            if let Some(rest) = q.strip_prefix("re:") {
                if rest.trim().is_empty() { Kind::All } else { build_regex(rest.trim(), case) }
            } else if let Some(rest) = q.strip_prefix('~') {
                if rest.trim().is_empty() { Kind::All } else { fuzzy(rest.trim()) }
            } else {
                let toks: Vec<(Tok, bool)> = q
                    .split_whitespace()
                    .map(|w| {
                        let (neg, w) = match w.strip_prefix('!') {
                            Some(r) if !r.is_empty() => (true, r),
                            _ => (false, w),
                        };
                        if w.contains(['*', '?']) { (Tok::Glob(w.to_string()), neg) } else { (Tok::Sub(folded(w, case)), neg) }
                    })
                    .collect();
                Kind::Smart(toks)
            }
        }
    };
    Matcher { kind, case, filters: vec![] }
}

impl Matcher {
    pub fn is_all(&self) -> bool {
        matches!(self.kind, Kind::All) && self.filters.is_empty()
    }

    /// The name part of the query is empty (only `:filters`, or nothing).
    pub fn name_all(&self) -> bool {
        matches!(self.kind, Kind::All)
    }

    /// Metadata filters (`:size>...`, `:age<...`, `:type=...`).
    pub fn filters_ok(&self, e: &Entry) -> bool {
        self.filters.iter().all(|f| f.test(e))
    }

    /// Set when the pattern is invalid (regex); the list then stays unfiltered.
    pub fn error(&self) -> Option<&str> {
        if let Kind::Bad(e) = &self.kind { Some(e) } else { None }
    }

    /// Fuzzy results are ordered by relevance instead of the sort column.
    pub fn ranked(&self) -> bool {
        matches!(self.kind, Kind::Fuzzy(_))
    }

    /// `None` = does not match. Higher score = better match (only meaningful when `ranked`).
    pub fn score(&self, name: &str) -> Option<i32> {
        match &self.kind {
            Kind::All | Kind::Bad(_) => Some(0),
            Kind::Fuzzy(q) => fuzzy_match(q, name, self.case).map(|m| m.0),
            Kind::Wild(p) => glob_match(p, name, self.case).then_some(0),
            Kind::Re(r) => r.is_match(name).then_some(0),
            Kind::Smart(toks) => {
                let hay = folded(name, self.case);
                for (tok, neg) in toks {
                    let hit = match tok {
                        Tok::Sub(q) => find_sub(&hay, q).is_some(),
                        Tok::Glob(p) => glob_match(p, name, self.case),
                    };
                    if hit == *neg {
                        return None;
                    }
                }
                Some(0)
            }
        }
    }

    /// Character ranges of `name` to highlight.
    pub fn ranges(&self, name: &str) -> Vec<(usize, usize)> {
        match &self.kind {
            Kind::Fuzzy(q) => fuzzy_match(q, name, self.case).map(|m| positions_to_ranges(&m.1)).unwrap_or_default(),
            Kind::Re(r) => r
                .find_iter(name)
                .filter(|m| !m.is_empty())
                .take(16)
                .map(|m| (name[..m.start()].chars().count(), name[..m.end()].chars().count()))
                .collect(),
            Kind::Smart(toks) => {
                let hay = folded(name, self.case);
                let v = toks
                    .iter()
                    .filter_map(|(t, neg)| match (t, neg) {
                        (Tok::Sub(q), false) if !q.is_empty() => find_sub(&hay, q).map(|i| (i, i + q.len())),
                        _ => None,
                    })
                    .collect();
                merge(v)
            }
            _ => vec![],
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn m(q: &str, mode: SearchMode) -> Matcher {
        compile(q, SearchOpts { mode, match_case: false, ..Default::default() })
    }

    #[test]
    fn smart_words_wildcards_and_exclusion() {
        let s = m("report !draft *.pdf", SearchMode::Smart);
        assert!(s.score("Report_final.PDF").is_some());
        assert!(s.score("report_draft.pdf").is_none(), "excluded word");
        assert!(s.score("report.docx").is_none(), "wildcard token must match");
        assert!(s.score("notes.pdf").is_none(), "all words must match");
    }

    #[test]
    fn smart_prefixes_switch_to_fuzzy_and_regex() {
        assert!(m("~rdme", SearchMode::Smart).score("README.md").is_some());
        assert!(m("re:^img_\\d+\\.png$", SearchMode::Smart).score("img_042.png").is_some());
        assert!(m("re:^img_\\d+\\.png$", SearchMode::Smart).score("img_x.png").is_none());
    }

    #[test]
    fn fuzzy_ranks_compact_and_word_start_matches_higher() {
        let f = m("rdme", SearchMode::Fuzzy);
        let good = f.score("README.md").unwrap();
        let scattered = f.score("really_dumb_message_end.txt").unwrap();
        assert!(good > scattered, "{good} should beat {scattered}");
        assert!(f.score("notes.txt").is_none());
        assert!(f.ranked());
    }

    #[test]
    fn fuzzy_reports_highlight_ranges() {
        // R(0) ... D(3) M(4) E(5): one lone letter, then a compact run
        assert_eq!(m("rdme", SearchMode::Fuzzy).ranges("README.md"), vec![(0, 1), (3, 6)]);
    }

    #[test]
    fn wildcard_is_anchored_but_plain_text_means_contains() {
        assert!(m("*.rs", SearchMode::Wildcard).score("main.rs").is_some());
        assert!(m("*.rs", SearchMode::Wildcard).score("main.rs.bak").is_none());
        assert!(m("report_??.pdf", SearchMode::Wildcard).score("report_01.pdf").is_some());
        assert!(m("port", SearchMode::Wildcard).score("report.pdf").is_some());
    }

    #[test]
    fn regex_errors_do_not_hide_files() {
        let r = m("([", SearchMode::Regex);
        assert!(r.error().is_some());
        assert!(r.score("anything").is_some(), "invalid pattern keeps the list unfiltered");
        assert!(m("^a.*z$", SearchMode::Regex).score("abcz").is_some());
    }

    #[test]
    fn case_sensitivity_toggle() {
        let q = |case| compile("README", SearchOpts { mode: SearchMode::Smart, match_case: case, ..Default::default() });
        assert!(q(false).score("readme.md").is_some());
        assert!(q(true).score("readme.md").is_none());
        assert!(q(true).score("README.md").is_some());
    }

    #[test]
    fn regex_ranges_are_character_based() {
        let r = m("é+", SearchMode::Regex);
        assert_eq!(r.ranges("aéé.txt"), vec![(1, 3)]);
    }
}

#[cfg(test)]
mod filter_tests {
    use super::*;

    fn entry(name: &str, size: u64, age_days: u64, dir: bool) -> Entry {
        Entry {
            name: name.into(),
            path: name.into(),
            is_dir: dir,
            size,
            modified: Some(SystemTime::now() - Duration::from_secs(age_days * 86400 + 60)),
            hidden: false,
            is_up: false,
            deep: false,
            snippet: String::new(),
            ext: name.rsplit_once('.').map(|x| x.1.to_lowercase()).unwrap_or_default(),
        }
    }

    #[test]
    fn size_age_and_type_filters() {
        let o = SearchOpts::default();
        let big = entry("a.mp4", 20 << 20, 30, false);
        let small = entry("b.png", 100, 1, false);
        let d = entry("sub", 0, 1, true);
        let m = compile(":size>10mb", o);
        assert!(m.filters_ok(&big) && !m.filters_ok(&small) && !m.filters_ok(&d));
        let m = compile(":age<7d", o);
        assert!(!m.filters_ok(&big) && m.filters_ok(&small));
        let m = compile(":modified>7d", o);
        assert!(m.filters_ok(&big) && !m.filters_ok(&small));
        let m = compile(":type=img,video", o);
        assert!(m.filters_ok(&big) && m.filters_ok(&small) && !m.filters_ok(&d));
        let m = compile(":type:dir", o);
        assert!(m.filters_ok(&d) && !m.filters_ok(&big));
        let m = compile(":date>2000-01-01 :size<1kb b", o);
        assert!(m.filters_ok(&small) && !m.filters_ok(&big));
        assert_eq!(m.score("b.png"), Some(0));
        assert_eq!(m.score("zzz"), None);
    }

    #[test]
    fn bad_filter_is_an_error_and_unknown_colon_is_text() {
        let o = SearchOpts::default();
        assert!(compile(":size>abc", o).error().is_some());
        assert!(compile(":size", o).error().is_some());
        let m = compile(":foo", o);
        assert!(m.error().is_none());
        assert_eq!(m.score("x:foo.txt"), Some(0));
    }
}
