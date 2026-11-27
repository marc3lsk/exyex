using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

/// <summary>A path-bar suggestion (tag "" = subfolder completion, else "★ favorite" / "recent").</summary>
public sealed record Sugg(string Path, string Tag);

/// <summary>The path bar while it is being edited (Ctrl+L).</summary>
public sealed class PathEdit
{
    public string Orig = "";
    public string Text = "";
    public bool Fresh = true;
    public bool CaretAtEnd;
    public string SuggFor = "\u0001";
    public List<Sugg> Sugg = [];
    public int? Sel;
}

public sealed class Pane
{
    public List<Tab> Tabs { get; } = [];
    public int Active;
    public PathEdit? PathEdit;
    public bool FocusFilter;
    public Tab Tab => Tabs[Active];
}

/// <summary>The modal dialogs (app.rs Dialog). Each is shown as an in-window overlay.</summary>
public abstract class Dialog
{
    public bool Fresh = true;
}

public sealed class NewFolderDialog : Dialog { public string Text = "New folder"; }
public sealed class NewFileDialog : Dialog { public string Text = "New file.txt"; }
/// <summary>Shift+Alt+F5: compress Paths into Dir\Text.</summary>
public sealed class ZipDialog : Dialog { public required List<string> Paths; public required string Dir; public string Text = ""; }
/// <summary>The archive name already exists in Dir: replace or keep both?</summary>
public sealed class ZipExistsDialog : Dialog { public required List<string> Paths; public required string Dir; public required string Name; }
public sealed class SftpAliasDialog : Dialog { public required string Key; public string Text = ""; }
public sealed class DeleteDialog : Dialog { public required List<string> Paths; public bool Permanent; }
/// <summary>SFTP: change the Linux owner / group (runs chown on the server).</summary>
public sealed class ChownDialog : Dialog
{
    public required List<string> Paths;
    public string Owner = "", Group = "";
    public bool Recursive = true, Sudo;
}
/// <summary>F4 editor settings: default command + per-extension rules.</summary>
public sealed class EditorDialog : Dialog { public string Cmd = "", Rules = ""; }
/// <summary>The user left an archive that was modified: write the changes back?</summary>
public sealed class ArchiveUpdateDialog : Dialog { public required string Zip; public bool Quit; }
/// <summary>F5 / F6: confirm copying or moving to the other pane.</summary>
public sealed class TransferDialog : Dialog { public OpKind Kind; public required List<string> Paths; public required string Dest; }
/// <summary>Copy / move where some targets already exist: asks about Queue[0], then the next…</summary>
public sealed class ConflictDialog : Dialog
{
    public OpKind Kind;
    public required List<string> Srcs;
    public required string Dest;
    public required List<string> Queue;
    public Dictionary<string, Policy> Chosen = new(StringComparer.OrdinalIgnoreCase);
    public bool All;
    public List<Entry> Existing = [];
}

/// <summary>In-place rename editor shown inside a file-list row.</summary>
public sealed class InlineRename
{
    public int Pi;
    public required string Dir;
    public required string Path;
    public string Text = "";
    public bool IsDir;
}

public enum OpenMode { Here, NewTab, OtherPane }

/// <summary>A command palette entry.</summary>
public abstract record PaletteItem;
public sealed record ActionItem(Act Action) : PaletteItem;
public sealed record FolderItem(string Path, string Tag) : PaletteItem;
public sealed record GotoItem(string Path) : PaletteItem;

public sealed class PaletteState
{
    public string Query = "";
    public bool FoldersOnly;
    public int Sel;
    public List<(string Path, string Tag, int Bonus)> Folders = [];
}

public enum TabCmd { Close, Others, Left, Right, Duplicate, OpenOther, MoveOther, CopyPath }

/// <summary>A remote file downloaded for editing (F4); saving it in the editor uploads it back.</summary>
public sealed class RemoteEdit
{
    public required string Remote;
    public required string Local;
    /// <summary>(mtime, size) of the local copy at the last upload / download.</summary>
    public (DateTime?, long) Seen;
    public DateTime? ChangedAt;
    public bool Busy;
}

/// <summary>Compare marks (app.rs Diff).</summary>
public enum Diff { OnlyHere, Newer, Older, Size }
