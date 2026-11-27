using System.Windows.Input;

namespace Mavyex.Core;

/// <summary>Every user-triggerable command (actions.rs).</summary>
public enum Act
{
    Palette, FolderJump, SwitchPane, SwapPanes, MirrorToOther, NewTab, CloseTab, CloseOtherTabs, CloseTabsLeft,
    CloseTabsRight, ReopenTab, NextTab, PrevTab, Up, Back, Forward, Open, OpenInNewTab, Refresh, EditPath, Filter, Copy,
    Move, NewFolder, NewFile, Delete, DeletePermanent, Rename, Zip, ClipCopy, ClipCut, ClipPaste, CopyPath,
    CopyFolderPath, SelectAll, SelectNone, InvertSelection, ToggleSelect, ToggleHidden, SortName, SortExt, SortSize,
    SortDate, ToggleFavorite, Reveal, Properties, FolderProperties, ShowInFolder, ConnectSftp, SftpAlias, SftpChown,
    SftpPutty, FavoriteItem, SftpRoot, ViewCycle, ViewDetails, ViewCompact, ViewGrid, ColExt, ColSize, ColDate,
    QuickLook, CompareToggle, SelectDiff, SyncToOther, TextCompare, EditFile, EditorSettings, Root, ExplorerHere,
    PowerShellHere, CmdHere, Terminal, ThemeCycle, ThemeSystem, ThemeLight, ThemeDark, Quit,
}

public sealed record Binding(ModifierKeys Mods, Key Key, Act Action);

public static class Actions
{
    /// <summary>Actions listed in the command palette (in display order).</summary>
    public static readonly Act[] PaletteActions =
    [
        Act.FolderJump, Act.Copy, Act.Move, Act.Rename, Act.Zip, Act.Delete, Act.DeletePermanent, Act.NewFolder, Act.NewFile,
        Act.ClipCopy, Act.ClipCut, Act.ClipPaste, Act.CopyPath, Act.CopyFolderPath, Act.NewTab, Act.CloseTab,
        Act.CloseOtherTabs, Act.CloseTabsLeft, Act.CloseTabsRight, Act.ReopenTab, Act.NextTab, Act.PrevTab, Act.SwitchPane,
        Act.SwapPanes, Act.MirrorToOther, Act.Up, Act.Back, Act.Forward, Act.Open, Act.OpenInNewTab, Act.Refresh,
        Act.EditPath, Act.Filter, Act.SelectAll, Act.SelectNone, Act.InvertSelection, Act.ToggleSelect, Act.ToggleHidden,
        Act.SortName, Act.SortExt, Act.SortSize, Act.SortDate, Act.ToggleFavorite, Act.Reveal, Act.Properties,
        Act.FolderProperties, Act.ShowInFolder, Act.ConnectSftp, Act.SftpAlias, Act.SftpChown, Act.SftpPutty,
        Act.FavoriteItem, Act.SftpRoot, Act.ViewCycle, Act.ViewDetails, Act.ViewCompact, Act.ViewGrid, Act.ColExt,
        Act.ColSize, Act.ColDate, Act.QuickLook, Act.CompareToggle, Act.SelectDiff, Act.SyncToOther, Act.TextCompare,
        Act.EditFile, Act.EditorSettings, Act.Root, Act.ExplorerHere, Act.Terminal, Act.PowerShellHere, Act.CmdHere,
        Act.ThemeCycle, Act.ThemeSystem, Act.ThemeLight, Act.ThemeDark, Act.Quit,
    ];

    public static string Name(Act a) => a switch
    {
        Act.Palette => "Command palette",
        Act.FolderJump => "Jump to folder (favorites, frequent, recent)",
        Act.SwitchPane => "Switch active pane",
        Act.SwapPanes => "Swap left and right panes",
        Act.MirrorToOther => "Open this folder in the other pane",
        Act.NewTab => "New tab (duplicate current)",
        Act.CloseTab => "Close tab",
        Act.CloseOtherTabs => "Close other tabs",
        Act.CloseTabsLeft => "Close tabs to the left",
        Act.CloseTabsRight => "Close tabs to the right",
        Act.ReopenTab => "Reopen closed tab",
        Act.NextTab => "Next tab",
        Act.PrevTab => "Previous tab",
        Act.Up => "Go to parent folder",
        Act.Root => "Go to root of this drive",
        Act.ConnectSftp => "Connect to SFTP server (Pageant)…",
        Act.SftpRoot => "SFTP: known connections",
        Act.SftpAlias => "SFTP: set alias for this connection…",
        Act.SftpChown => "SFTP: change Linux owner (chown)…",
        Act.SftpPutty => "SFTP: open this server in PuTTY (SSH)",
        Act.FavoriteItem => "Add / remove the selected folder to favorites",
        Act.ViewCycle => "View: cycle Details / Compact / Grid",
        Act.ViewDetails => "View: details",
        Act.ViewCompact => "View: compact list",
        Act.ViewGrid => "View: grid with thumbnails",
        Act.ColExt => "Columns: show / hide extension",
        Act.ColSize => "Columns: show / hide size",
        Act.ColDate => "Columns: show / hide date modified",
        Act.QuickLook => "Quick Look: preview the selected item",
        Act.CompareToggle => "Compare panes: mark differences",
        Act.SelectDiff => "Select items that differ from the other pane",
        Act.SyncToOther => "Sync: copy differing items to the other pane",
        Act.TextCompare => "Compare files: text diff (2 selected, or cursor in both panes)",
        Act.EditFile => "Edit file in the external editor (F4)",
        Act.EditorSettings => "Settings: editor for F4 (command, per-extension rules)…",
        Act.Back => "Back",
        Act.Forward => "Forward",
        Act.Open => "Open",
        Act.OpenInNewTab => "Open folder in new tab",
        Act.Refresh => "Refresh",
        Act.EditPath => "Edit path",
        Act.Filter => "Filter this folder",
        Act.Copy => "Copy to other pane",
        Act.Move => "Move to other pane",
        Act.NewFolder => "New folder",
        Act.NewFile => "New file",
        Act.Delete => "Delete (Recycle Bin)",
        Act.DeletePermanent => "Delete permanently",
        Act.Rename => "Rename",
        Act.Zip => "Zip the selected files / folders…",
        Act.ClipCopy => "Copy",
        Act.ClipCut => "Cut",
        Act.ClipPaste => "Paste",
        Act.CopyPath => "Copy full path as text",
        Act.CopyFolderPath => "Copy current folder path",
        Act.SelectAll => "Select all",
        Act.SelectNone => "Clear selection",
        Act.InvertSelection => "Invert selection",
        Act.ToggleSelect => "Toggle selection and move down",
        Act.ToggleHidden => "Show / hide hidden files",
        Act.SortName => "Sort by name",
        Act.SortExt => "Sort by extension",
        Act.SortSize => "Sort by size",
        Act.SortDate => "Sort by date modified",
        Act.ToggleFavorite => "Add / remove folder from favorites",
        Act.Reveal => "Show in Windows Explorer",
        Act.Properties => "Properties (Windows dialog)",
        Act.FolderProperties => "Properties of this folder (Windows dialog)",
        Act.ShowInFolder => "Open containing folder (focus this item)",
        Act.ExplorerHere => "Open this folder in Windows Explorer",
        Act.Terminal => "Open terminal here (Windows Terminal)",
        Act.PowerShellHere => "Open PowerShell here",
        Act.CmdHere => "Open Command Prompt (cmd) here",
        Act.ThemeCycle => "Theme: cycle System / Light / Dark",
        Act.ThemeSystem => "Theme: follow system",
        Act.ThemeLight => "Theme: light",
        Act.ThemeDark => "Theme: dark",
        Act.Quit => "Quit",
        _ => a.ToString(),
    };

    /// <summary>Total Commander + Explorer hybrid. More specific modifier combos come first because the first match
    /// consumes the key.</summary>
    public static readonly IReadOnlyList<Binding> Bindings = Build();

    private static List<Binding> Build()
    {
        const ModifierKeys n = ModifierKeys.None, c = ModifierKeys.Control, s = ModifierKeys.Shift, a = ModifierKeys.Alt;
        const ModifierKeys cs = c | s, al = a | s;
        static Binding B(ModifierKeys m, Key k, Act x) => new(m, k, x);
        return
        [
            B(cs, Key.D, Act.TextCompare),
            B(cs, Key.T, Act.ReopenTab),
            B(cs, Key.Tab, Act.PrevTab),
            B(cs, Key.C, Act.CopyPath),
            B(cs, Key.E, Act.Reveal),
            B(cs, Key.O, Act.Terminal),
            B(cs, Key.N, Act.NewFolder),
            B(cs, Key.A, Act.SelectNone),
            B(cs, Key.L, Act.ThemeCycle),
            B(c, Key.P, Act.Palette),
            B(c, Key.D, Act.FolderJump),
            B(c, Key.B, Act.ToggleFavorite),
            B(c, Key.T, Act.NewTab),
            B(c, Key.W, Act.CloseTab),
            B(c, Key.Tab, Act.NextTab),
            B(c, Key.PageDown, Act.NextTab),
            B(c, Key.PageUp, Act.PrevTab),
            B(c, Key.U, Act.SwapPanes),
            B(c, Key.R, Act.Refresh),
            B(c, Key.L, Act.EditPath),
            B(c, Key.F, Act.Filter),
            B(c, Key.A, Act.SelectAll),
            B(c, Key.I, Act.InvertSelection),
            B(c, Key.H, Act.ToggleHidden),
            B(c, Key.Q, Act.Quit),
            B(c, Key.Back, Act.Root),
            B(al, Key.P, Act.PowerShellHere),
            B(al, Key.C, Act.CmdHere),
            B(al, Key.E, Act.ExplorerHere),
            B(al, Key.S, Act.SyncToOther),
            B(al, Key.F5, Act.Zip),
            B(cs, Key.M, Act.SelectDiff),
            B(c, Key.M, Act.CompareToggle),
            B(n, Key.F3, Act.QuickLook),
            B(a, Key.V, Act.ViewCycle),
            B(cs, Key.K, Act.ConnectSftp),
            B(al, Key.Enter, Act.Properties),
            B(a, Key.Enter, Act.ShowInFolder),
            B(c, Key.Enter, Act.OpenInNewTab),
            B(c, Key.F3, Act.SortName),
            B(c, Key.F4, Act.SortExt),
            B(c, Key.F5, Act.SortDate),
            B(c, Key.F6, Act.SortSize),
            B(s, Key.F4, Act.NewFile),
            B(s, Key.F6, Act.Rename),
            B(a, Key.D, Act.EditPath),
            B(a, Key.Up, Act.Up),
            B(a, Key.Left, Act.Back),
            B(a, Key.Right, Act.Forward),
            B(n, Key.Tab, Act.SwitchPane),
            B(n, Key.Enter, Act.Open),
            B(n, Key.Back, Act.Up),
            B(n, Key.F2, Act.Rename),
            B(n, Key.F4, Act.EditFile),
            B(n, Key.F5, Act.Copy),
            B(n, Key.F6, Act.Move),
            B(n, Key.F7, Act.NewFolder),
            B(n, Key.F8, Act.Delete),
            B(n, Key.Delete, Act.Delete),
            B(n, Key.Space, Act.ToggleSelect),
            B(n, Key.Insert, Act.ToggleSelect),
        ];
    }

    private static string KeyName(Key k) => k switch
    {
        Key.Back => "Backspace",
        Key.Up => "ArrowUp",
        Key.Down => "ArrowDown",
        Key.Left => "ArrowLeft",
        Key.Right => "ArrowRight",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        Key.Return => "Enter",
        _ => k.ToString(),
    };

    public static string ShortcutLabel(Act a)
    {
        var labels = Bindings.Where(b => b.Action == a).Select(b =>
        {
            var s = "";
            if (b.Mods.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
            if (b.Mods.HasFlag(ModifierKeys.Alt)) s += "Alt+";
            if (b.Mods.HasFlag(ModifierKeys.Shift)) s += "Shift+";
            return s + KeyName(b.Key);
        }).ToList();
        // fixed extras handled outside the table
        switch (a)
        {
            case Act.ClipCopy: labels.Add("Ctrl+C"); break;
            case Act.ClipCut: labels.Add("Ctrl+X"); break;
            case Act.ClipPaste: labels.Add("Ctrl+V"); break;
            case Act.DeletePermanent: labels.Add("Shift+Delete"); break;
        }
        return string.Join(" / ", labels);
    }
}
