//! Every user-triggerable command, its key bindings, and fuzzy matching
//! used by the command palette.

use eframe::egui::{Key, Modifiers};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Action {
    Palette,
    FolderJump,
    SwitchPane,
    SwapPanes,
    MirrorToOther,
    NewTab,
    CloseTab,
    CloseOtherTabs,
    CloseTabsLeft,
    CloseTabsRight,
    ReopenTab,
    NextTab,
    PrevTab,
    Up,
    Back,
    Forward,
    Open,
    OpenInNewTab,
    Refresh,
    EditPath,
    Filter,
    Copy,
    Move,
    NewFolder,
    NewFile,
    Delete,
    DeletePermanent,
    Rename,
    ClipCopy,
    ClipCut,
    ClipPaste,
    CopyPath,
    SelectAll,
    SelectNone,
    InvertSelection,
    ToggleSelect,
    ToggleHidden,
    SortName,
    SortExt,
    SortSize,
    SortDate,
    ToggleFavorite,
    Reveal,
    Terminal,
    ThemeCycle,
    ThemeSystem,
    ThemeLight,
    ThemeDark,
    Quit,
}

use Action::*;

/// Actions listed in the command palette (in display order).
pub const PALETTE_ACTIONS: &[Action] = &[
    FolderJump, Copy, Move, Rename, Delete, DeletePermanent, NewFolder, NewFile, ClipCopy, ClipCut, ClipPaste,
    CopyPath, NewTab, CloseTab, CloseOtherTabs, CloseTabsLeft, CloseTabsRight, ReopenTab, NextTab, PrevTab, SwitchPane, SwapPanes, MirrorToOther, Up, Back,
    Forward, Open, OpenInNewTab, Refresh, EditPath, Filter, SelectAll, SelectNone, InvertSelection, ToggleSelect,
    ToggleHidden, SortName, SortExt, SortSize, SortDate, ToggleFavorite, Reveal, Terminal, ThemeCycle, ThemeSystem, ThemeLight, ThemeDark, Quit,
];

pub fn name(a: Action) -> &'static str {
    match a {
        Palette => "Command palette",
        FolderJump => "Jump to folder (favorites, frequent, recent)",
        SwitchPane => "Switch active pane",
        SwapPanes => "Swap left and right panes",
        MirrorToOther => "Open this folder in the other pane",
        NewTab => "New tab (duplicate current)",
        CloseTab => "Close tab",
        CloseOtherTabs => "Close other tabs",
        CloseTabsLeft => "Close tabs to the left",
        CloseTabsRight => "Close tabs to the right",
        ReopenTab => "Reopen closed tab",
        NextTab => "Next tab",
        PrevTab => "Previous tab",
        Up => "Go to parent folder",
        Back => "Back",
        Forward => "Forward",
        Open => "Open",
        OpenInNewTab => "Open folder in new tab",
        Refresh => "Refresh",
        EditPath => "Edit path",
        Filter => "Filter this folder",
        Copy => "Copy to other pane",
        Move => "Move to other pane",
        NewFolder => "New folder",
        NewFile => "New file",
        Delete => "Delete (Recycle Bin)",
        DeletePermanent => "Delete permanently",
        Rename => "Rename",
        ClipCopy => "Copy",
        ClipCut => "Cut",
        ClipPaste => "Paste",
        CopyPath => "Copy full path as text",
        SelectAll => "Select all",
        SelectNone => "Clear selection",
        InvertSelection => "Invert selection",
        ToggleSelect => "Toggle selection and move down",
        ToggleHidden => "Show / hide hidden files",
        SortName => "Sort by name",
        SortExt => "Sort by extension",
        SortSize => "Sort by size",
        SortDate => "Sort by date modified",
        ToggleFavorite => "Add / remove folder from favorites",
        Reveal => "Show in Windows Explorer",
        Terminal => "Open terminal here",
        ThemeCycle => "Theme: cycle System / Light / Dark",
        ThemeSystem => "Theme: follow system",
        ThemeLight => "Theme: light",
        ThemeDark => "Theme: dark",
        Quit => "Quit",
    }
}

pub struct Binding {
    pub mods: Modifiers,
    pub key: Key,
    pub action: Action,
}

/// Total Commander + Explorer hybrid. More specific modifier combos come first
/// because the first match consumes the key.
pub fn bindings() -> Vec<Binding> {
    let n = Modifiers::NONE;
    let c = Modifiers::CTRL;
    let s = Modifiers::SHIFT;
    let a = Modifiers::ALT;
    let cs = c.plus(s);
    let b = |mods, key, action| Binding { mods, key, action };
    vec![
        b(cs, Key::T, ReopenTab),
        b(cs, Key::Tab, PrevTab),
        b(cs, Key::C, CopyPath),
        b(cs, Key::E, Reveal),
        b(cs, Key::O, Terminal),
        b(cs, Key::N, NewFolder),
        b(cs, Key::A, SelectNone),
        b(cs, Key::L, ThemeCycle),
        b(c, Key::P, Palette),
        b(c, Key::D, FolderJump),
        b(c, Key::B, ToggleFavorite),
        b(c, Key::T, NewTab),
        b(c, Key::W, CloseTab),
        b(c, Key::Tab, NextTab),
        b(c, Key::PageDown, NextTab),
        b(c, Key::PageUp, PrevTab),
        b(c, Key::U, SwapPanes),
        b(c, Key::R, Refresh),
        b(c, Key::L, EditPath),
        b(c, Key::F, Filter),
        b(c, Key::A, SelectAll),
        b(c, Key::I, InvertSelection),
        b(c, Key::H, ToggleHidden),
        b(c, Key::Q, Quit),
        b(c, Key::Enter, OpenInNewTab),
        b(c, Key::F3, SortName),
        b(c, Key::F4, SortExt),
        b(c, Key::F5, SortDate),
        b(c, Key::F6, SortSize),
        b(s, Key::F4, NewFile),
        b(s, Key::F6, Rename),
        b(a, Key::D, EditPath),
        b(a, Key::ArrowUp, Up),
        b(a, Key::ArrowLeft, Back),
        b(a, Key::ArrowRight, Forward),
        b(n, Key::Tab, SwitchPane),
        b(n, Key::Enter, Open),
        b(n, Key::Backspace, Up),
        b(n, Key::F2, Rename),
        b(n, Key::F4, EditPath),
        b(n, Key::F5, Copy),
        b(n, Key::F6, Move),
        b(n, Key::F7, NewFolder),
        b(n, Key::F8, Delete),
        b(n, Key::Delete, Delete),
        b(n, Key::Space, ToggleSelect),
        b(n, Key::Insert, ToggleSelect),
    ]
}

pub fn shortcut_label(a: Action, table: &[Binding]) -> String {
    let labels: Vec<String> = table
        .iter()
        .filter(|b| b.action == a)
        .map(|b| {
            let mut s = String::new();
            if b.mods.ctrl {
                s += "Ctrl+";
            }
            if b.mods.alt {
                s += "Alt+";
            }
            if b.mods.shift {
                s += "Shift+";
            }
            s += b.key.name();
            s
        })
        .collect();
    let mut labels = labels;
    // Fixed extras handled outside the table
    match a {
        ClipCopy => labels.push("Ctrl+C".into()),
        ClipCut => labels.push("Ctrl+X".into()),
        ClipPaste => labels.push("Ctrl+V".into()),
        DeletePermanent => labels.push("Shift+Delete".into()),
        _ => {}
    }
    labels.join(" / ")
}

// ------------------------------------------------------------------- fuzzy

/// Subsequence fuzzy match. Each whitespace separated token of `query` must
/// match; higher is better.
pub fn fuzzy(query: &str, text: &str) -> Option<i32> {
    let t_orig: Vec<char> = text.chars().collect();
    let t: Vec<char> = text.to_lowercase().chars().collect();
    if t.len() != t_orig.len() {
        return fuzzy_simple(query, &t);
    }
    let mut total = 0;
    for tok in query.split_whitespace() {
        let q: Vec<char> = tok.to_lowercase().chars().collect();
        let mut qi = 0;
        let mut score = 0;
        let mut last: Option<usize> = None;
        for (i, &c) in t.iter().enumerate() {
            if qi < q.len() && c == q[qi] {
                let mut s = 10;
                if last.is_some_and(|l| l + 1 == i) {
                    s += 15;
                }
                if i == 0 || !t_orig[i - 1].is_alphanumeric() || (t_orig[i].is_uppercase() && t_orig[i - 1].is_lowercase()) {
                    s += 12;
                }
                score += s;
                last = Some(i);
                qi += 1;
            }
        }
        if qi < q.len() {
            return None;
        }
        let lt: String = t.iter().collect();
        if lt.contains(&tok.to_lowercase()) {
            score += 30;
        }
        total += score;
    }
    Some(total - (t.len() as i32) / 6)
}

fn fuzzy_simple(query: &str, t: &[char]) -> Option<i32> {
    let s: String = t.iter().collect();
    query.split_whitespace().all(|tok| s.contains(&tok.to_lowercase())).then_some(10)
}
