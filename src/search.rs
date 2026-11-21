//! In-folder search: Smart / Fuzzy / Wildcard / Regex matching with scoring
//! (for ranking) and match positions (for highlighting).

use regex::{Regex, RegexBuilder};
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

    pub fn hint(self) -> &'static str {
        match self {
            SearchMode::Smart => "type to filter  ·  *.rs  ·  !exclude  ·  ~fuzzy  ·  re:regex",
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
}

impl Default for Matcher {
    fn default() -> Self {
        Matcher { kind: Kind::All, case: false }
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

pub fn compile(query: &str, opts: SearchOpts) -> Matcher {
    let q = query.trim();
    let case = opts.match_case;
    if q.is_empty() {
        return Matcher { kind: Kind::All, case };
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
    Matcher { kind, case }
}

impl Matcher {
    pub fn is_all(&self) -> bool {
        matches!(self.kind, Kind::All)
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
        compile(q, SearchOpts { mode, match_case: false, deep: false })
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
        let q = |case| compile("README", SearchOpts { mode: SearchMode::Smart, match_case: case, deep: false });
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
