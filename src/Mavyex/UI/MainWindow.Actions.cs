using System.Diagnostics;
using System.Windows;
using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

public sealed partial class MainWindow
{
    /// <summary>Runs a command (app.rs run). pi: the pane whose button was clicked (becomes active first).</summary>
    public void Run(Act a, int? pi = null)
    {
        if (pi is { } p) ActivePane = p;
        renameTimer = null;
        RunCore(a);
        Render();
        if (Keyboard_IsListContext()) views[ActivePane].EnsureCursorVisible();
    }

    private bool Keyboard_IsListContext() => dialog == null && palette == null;

    private void RunCore(Act a)
    {
        int pi = ActivePane;
        var tab = CurTab;
        switch (a)
        {
            case Act.Palette: OpenPalette(false); break;
            case Act.FolderJump: OpenPalette(true); break;
            case Act.SwitchPane: ActivePane = Other; break;
            case Act.SwapPanes:
            {
                var (t0, a0) = (Panes[0].Tabs.ToList(), Panes[0].Active);
                Panes[0].Tabs.Clear();
                Panes[0].Tabs.AddRange(Panes[1].Tabs);
                Panes[0].Active = Panes[1].Active;
                Panes[1].Tabs.Clear();
                Panes[1].Tabs.AddRange(t0);
                Panes[1].Active = a0;
                break;
            }
            case Act.MirrorToOther: Navigate(Other, tab.Path); break;
            case Act.NewTab:
                OpenNewTab(pi, tab.Path);
                RunCore(Act.EditPath); // start typing right away: the path bar opens with favorites and recents
                break;
            case Act.CloseTab:
            {
                var pane = Panes[pi];
                if (pane.Tabs.Count > 1)
                {
                    var t = pane.Tabs[pane.Active];
                    pane.Tabs.RemoveAt(pane.Active);
                    t.DeepJob?.Dispose();
                    closedTabs.Add((pi, t.Path));
                    pane.Active = Math.Min(pane.Active, pane.Tabs.Count - 1);
                    dirty = true;
                }
                else Say("Can't close the last tab of a pane");
                break;
            }
            case Act.CloseOtherTabs: CloseTabsWhere(pi, Panes[pi].Active, _ => true); break;
            case Act.CloseTabsLeft: { int act = Panes[pi].Active; CloseTabsWhere(pi, act, i => i < act); break; }
            case Act.CloseTabsRight: { int act = Panes[pi].Active; CloseTabsWhere(pi, act, i => i > act); break; }
            case Act.ReopenTab:
                if (closedTabs.Count > 0)
                {
                    var (p, path) = closedTabs[^1];
                    closedTabs.RemoveAt(closedTabs.Count - 1);
                    OpenNewTab(p, path);
                }
                break;
            case Act.NextTab:
            case Act.PrevTab:
            {
                var pane = Panes[pi];
                int n = pane.Tabs.Count;
                pane.Active = a == Act.NextTab ? (pane.Active + 1) % n : (pane.Active + n - 1) % n;
                dirty = true;
                break;
            }
            case Act.Up: NavResult(tab.Up()); break;
            case Act.Back: NavResult(tab.GoBack()); break;
            case Act.Forward: NavResult(tab.GoForward()); break;
            case Act.Open: OpenCurrent(); break;
            case Act.ShowInFolder:
            {
                if (tab.Current is not { IsUp: false } e || VPath.Parent(e.Path) is not { } parent) return;
                var err = tab.GoFocus(parent, VPath.DisplayName(e.Path));
                if (err != null) Say(err);
                else
                {
                    Store.RecordVisit(parent);
                    dirty = true;
                }
                break;
            }
            case Act.FavoriteItem:
            {
                var p = tab.Current is { IsDir: true, IsUp: false } e ? e.Path : tab.Path;
                bool on = Store.ToggleFavorite(p);
                dirty = true;
                Say(on ? $"Added {Sftp.Pretty(p) ?? VPath.DisplayName(p)} to favorites ★" : "Removed from favorites");
                break;
            }
            case Act.SftpAlias:
            {
                var p = tab.Current is { IsUp: false } e && Sftp.IsRemote(e.Path) ? e.Path : tab.Path;
                if (Sftp.ConnKey(p) is { } key)
                {
                    dialog = new SftpAliasDialog { Key = key, Text = Store.Aliases.GetValueOrDefault(key, "") };
                    ShowDialog();
                }
                else Say("Open an SFTP folder (or select a connection in sftp:) first");
                break;
            }
            case Act.SftpChown:
            {
                var paths = tab.Targets().Where(Sftp.IsRemote).ToList();
                if (paths.Count == 0) Say("Select files or folders on an SFTP server first");
                else
                {
                    dialog = new ChownDialog { Paths = paths };
                    ShowDialog();
                }
                break;
            }
            case Act.SftpPutty:
            {
                var l = Sftp.Parse(tab.Path) ?? (tab.Current is { } c ? Sftp.Parse(c.Path) : null);
                if (l == null)
                {
                    Say("Open an SFTP server first");
                    return;
                }
                string[] candidates = ["putty.exe", @"C:\Program Files\PuTTY\putty.exe", @"C:\Program Files (x86)\PuTTY\putty.exe"];
                bool started = candidates.Any(c => TryStart(c, ["-ssh", $"{l.User}@{l.Host}", "-P", l.Port.ToString()]));
                Say(started ? $"PuTTY: {l.User}@{l.Host}" : "⚠ Could not start putty.exe – install PuTTY or add it to PATH");
                break;
            }
            case Act.ConnectSftp:
                Panes[pi].PathEdit = new PathEdit { Text = "sftp://", CaretAtEnd = true };
                Say("Type sftp://user@host[:port]/path – keys come from Pageant");
                break;
            case Act.SftpRoot: Navigate(pi, "sftp:"); break;
            case Act.QuickLook: preview = !preview; break;
            case Act.TextCompare: TextCompare(); break;
            case Act.ViewCycle:
            case Act.ViewDetails:
            case Act.ViewCompact:
            case Act.ViewGrid:
                Store.View = a switch
                {
                    Act.ViewDetails => ViewMode.Details,
                    Act.ViewCompact => ViewMode.Compact,
                    Act.ViewGrid => ViewMode.Grid,
                    _ => Store.View switch { ViewMode.Details => ViewMode.Compact, ViewMode.Compact => ViewMode.Grid, _ => ViewMode.Details },
                };
                dirty = true;
                Say($"View: {ViewLabel(Store.View)}");
                break;
            case Act.ColExt: Store.Cols.Ext = !Store.Cols.Ext; dirty = true; break;
            case Act.ColSize: Store.Cols.Size = !Store.Cols.Size; dirty = true; break;
            case Act.ColDate: Store.Cols.Date = !Store.Cols.Date; dirty = true; break;
            case Act.CompareToggle:
                compare = !compare;
                if (compare)
                {
                    diff[0] = Helpers.DiffMap(Panes[0].Tab, Panes[1].Tab);
                    diff[1] = Helpers.DiffMap(Panes[1].Tab, Panes[0].Tab);
                }
                else
                {
                    diff[0] = [];
                    diff[1] = [];
                }
                Say(compare ? "Comparing panes: green = only here, orange = newer here, red = older here, purple = different size" : "Compare off");
                break;
            case Act.SelectDiff:
            {
                int n = SelectDiff(pi);
                Say(n == 0 ? "Nothing differs – this pane has no new or newer items" : $"{n} item(s) selected");
                break;
            }
            case Act.SyncToOther:
                if (SelectDiff(pi) == 0) Say("Panes are in sync – nothing to copy");
                else RunCore(Act.Copy);
                break;
            case Act.Root:
            {
                var root = Sftp.RootOf(tab.Path) ?? VPath.Components(tab.Path).FirstOrDefault();
                if (root != null && !VPath.Same(root, tab.Path)) Navigate(pi, root);
                break;
            }
            case Act.OpenInNewTab:
                if (tab.Current is { } cur) OpenNewTab(pi, cur.IsDir ? cur.Path : tab.Path);
                break;
            case Act.Refresh:
                Drives = Helpers.ListDrives();
                ReloadAll();
                break;
            case Act.EditPath:
                Panes[pi].PathEdit = new PathEdit { Orig = tab.Path, Text = tab.Path };
                break;
            case Act.Filter:
                tab.FilterOpen = true;
                Panes[pi].FocusFilter = true;
                break;
            case Act.Copy:
            case Act.Move:
            {
                var paths = tab.Targets();
                if (paths.Count > 0)
                {
                    dialog = new TransferDialog { Kind = a == Act.Copy ? OpKind.Copy : OpKind.Move, Paths = paths, Dest = Panes[Other].Tab.Path };
                    ShowDialog();
                }
                break;
            }
            case Act.NewFolder: dialog = new NewFolderDialog(); ShowDialog(); break;
            case Act.NewFile: dialog = new NewFileDialog(); ShowDialog(); break;
            case Act.Zip:
            {
                var paths = tab.Targets();
                var dir = tab.Path;
                if (paths.Count == 0) return;
                if (Sftp.IsRemote(dir) || Archive.InArchive(dir) || paths.Any(p => Sftp.IsRemote(p) || Archive.InArchive(p)))
                {
                    Say("Zipping works on local files only");
                    return;
                }
                var baseName = paths.Count == 1 ? (VPath.Stem(paths[0]) is { Length: > 0 } s ? s : VPath.FileName(paths[0])) : VPath.FileName(dir);
                dialog = new ZipDialog { Paths = paths, Dir = dir, Text = (baseName ?? "archive") + ".zip" };
                ShowDialog();
                break;
            }
            case Act.Delete:
            case Act.DeletePermanent:
            {
                var paths = tab.Targets();
                if (paths.Count > 0)
                {
                    bool permanent = a == Act.DeletePermanent || paths.Any(p => Sftp.IsRemote(p) || Archive.SplitInner(p) != null);
                    dialog = new DeleteDialog { Paths = paths, Permanent = permanent };
                    ShowDialog();
                }
                break;
            }
            case Act.Rename: StartInline(pi); break;
            case Act.EditFile:
            {
                if (tab.Current is not { IsDir: false, IsUp: false } e)
                {
                    RunCore(Act.EditPath);
                    return;
                }
                if (Archive.IsArchiveName(e.Path) && Archive.SplitInner(e.Path) == null)
                {
                    Say("Press Enter to browse an archive; F4 edits the files inside it");
                    return;
                }
                EditFile(e.Path);
                break;
            }
            case Act.EditorSettings:
                dialog = new EditorDialog { Cmd = Store.Editor, Rules = Store.EditorRules };
                ShowDialog();
                break;
            case Act.ClipCopy:
            case Act.ClipCut:
            {
                var paths = tab.Targets();
                if (paths.Count > 0)
                {
                    clipboard = (paths, a == Act.ClipCut);
                    Say($"{paths.Count} item(s) {(a == Act.ClipCut ? "cut" : "copied")} — paste with Ctrl+V");
                }
                break;
            }
            case Act.ClipPaste:
                if (clipboard is { } cb)
                {
                    StartOp(cb.Cut ? OpKind.Move : OpKind.Copy, cb.Paths, tab.Path);
                    if (cb.Cut) clipboard = null;
                }
                else Say("Clipboard is empty");
                break;
            case Act.CopyFolderPath:
                SetClipboardText(tab.Path);
                Say("Folder path copied to clipboard");
                break;
            case Act.CopyPath:
                SetClipboardText(string.Join("\n", tab.Targets()));
                Say("Path copied to clipboard");
                break;
            case Act.SelectAll: tab.SelectAll(); break;
            case Act.SelectNone: tab.Selected.Clear(); break;
            case Act.InvertSelection: tab.Invert(); break;
            case Act.ToggleSelect:
                tab.ToggleAt(tab.Cursor);
                tab.MoveCursor(1, false);
                break;
            case Act.ToggleHidden:
                Store.ShowHidden = !Store.ShowHidden;
                foreach (var p in Panes)
                    foreach (var t in p.Tabs)
                    {
                        t.ShowHidden = Store.ShowHidden;
                        t.RebuildView();
                    }
                dirty = true;
                break;
            case Act.SortName: tab.SetSort(SortKey.Name); break;
            case Act.SortExt: tab.SetSort(SortKey.Ext); break;
            case Act.SortSize: tab.SetSort(SortKey.Size); break;
            case Act.SortDate: tab.SetSort(SortKey.Modified); break;
            case Act.ToggleFavorite:
            {
                bool on = Store.ToggleFavorite(tab.Path);
                dirty = true;
                Say(on ? "Added to favorites ★" : "Removed from favorites");
                break;
            }
            case Act.Reveal:
            {
                var target = tab.Current?.Path ?? tab.Path;
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"")); } catch { }
                break;
            }
            case Act.Properties:
            case Act.FolderProperties:
            {
                var paths = a == Act.FolderProperties ? [] : tab.Targets();
                if (paths.Count == 0) paths = [tab.Path];
                if (paths.Any(p => Sftp.IsRemote(p) || Archive.SplitInner(p) != null))
                {
                    Say("Properties work on local files only");
                    return;
                }
                try { Props.Show(paths); } catch (Exception e) { Say($"⚠ {e.Message}"); }
                break;
            }
            case Act.ExplorerHere:
                if (!TryStart("explorer.exe", [tab.Path])) Say("⚠ Could not start Explorer");
                break;
            case Act.PowerShellHere:
            case Act.CmdHere:
            {
                var exe = a == Act.PowerShellHere ? "powershell.exe" : "cmd.exe";
                if (!TryStart("cmd.exe", ["/c", "start", "", exe], tab.Path)) Say($"⚠ Could not start {exe}");
                break;
            }
            case Act.Terminal:
                if (!TryStart("wt.exe", ["-d", tab.Path]) && !TryStart("cmd.exe", ["/c", "start", "cmd.exe"], tab.Path))
                    Say("⚠ Could not start terminal");
                break;
            case Act.ThemeCycle:
            case Act.ThemeSystem:
            case Act.ThemeLight:
            case Act.ThemeDark:
                Store.Theme = a switch
                {
                    Act.ThemeSystem => ThemeChoice.System,
                    Act.ThemeLight => ThemeChoice.Light,
                    Act.ThemeDark => ThemeChoice.Dark,
                    _ => Store.Theme switch { ThemeChoice.System => ThemeChoice.Light, ThemeChoice.Light => ThemeChoice.Dark, _ => ThemeChoice.System },
                };
                Theme.Apply(Store.Theme);
                dirty = true;
                Say($"Theme: {ThemeLabel(Store.Theme)}");
                break;
            case Act.Quit: RequestQuit(); break;
        }
    }

    private void NavResult(string? err)
    {
        if (err != null) Say(err);
        else if (CurTab.LoadingTo == null)
        {
            Store.RecordVisit(CurTab.Path);
            dirty = true;
        }
    }

    private static bool TryStart(string exe, string[] args, string? cwd = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = exe.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (cwd != null && !Sftp.IsRemote(cwd)) psi.WorkingDirectory = cwd;
            return Process.Start(psi) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void OpenWithShell(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void SetClipboardText(string s)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(s, true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(40);
            }
        }
    }

    private void OpenCurrent()
    {
        int pi = ActivePane;
        if (CurTab.Current is not { } e) return;
        if (e.IsDir)
        {
            Navigate(pi, e.Path);
            return;
        }
        if (Sftp.IsRemote(e.Path))
        {
            var (path, name) = (e.Path, e.Name);
            Say($"Downloading {name}…");
            Task.Run(() =>
            {
                string msg;
                try
                {
                    var tmp = Sftp.FetchTemp(path);
                    try
                    {
                        OpenWithShell(tmp);
                        msg = $"Opened {tmp} (a temporary copy – edits are not uploaded)";
                    }
                    catch (Exception ex)
                    {
                        msg = $"⚠ Could not open {tmp}: {ex.Message}";
                    }
                }
                catch (Exception ex)
                {
                    msg = $"⚠ {name}: {ex.Message}";
                }
                Dispatcher.BeginInvoke(() => { Say(msg); Render(); });
            });
            return;
        }
        if (Archive.IsArchiveName(e.Path) && Archive.SplitInner(e.Path) == null)
        {
            Navigate(pi, e.Path); // browse the archive like a folder
            return;
        }
        if (Archive.SplitInner(e.Path) is { } ai)
        {
            try
            {
                var tmp = Archive.Active(ai.Zip) != null ? Archive.RealForEdit(e.Path) : Archive.ExtractTemp(ai.Zip, ai.Inner);
                try { OpenWithShell(tmp); } catch (Exception ex) { Say($"⚠ Could not open {e.Name}: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                Say($"⚠ {ex.Message}");
            }
            return;
        }
        try { OpenWithShell(e.Path); } catch (Exception ex) { Say($"⚠ Could not open {e.Name}: {ex.Message}"); }
    }

    private void TextCompare()
    {
        var t = CurTab;
        bool IsFile(Tab tb, string p) => tb.Entries.FirstOrDefault(e => VPath.Same(e.Path, p)) is { IsDir: false, IsUp: false };
        var sel = t.Targets();
        (string, string)? pair = null;
        if (sel.Count == 2)
        {
            if (IsFile(t, sel[0]) && IsFile(t, sel[1])) pair = (sel[0], sel[1]);
        }
        else
        {
            var o = Panes[Other].Tab;
            if (t.Current is { IsDir: false, IsUp: false } a && o.Current is { IsDir: false, IsUp: false } b) pair = (a.Path, b.Path);
        }
        if (pair is not { } pr)
        {
            Say("Select two files (or put the cursor on a file in both panes) to compare");
            return;
        }
        textDiff?.Close();
        textDiff = new TextDiffWindow(pr.Item1, pr.Item2, Store.DiffDark, d =>
        {
            Store.DiffDark = d;
            dirty = true;
        });
        textDiff.Closed += (_, _) => textDiff = null;
        textDiff.Show();
    }

    /// <summary>Closes every tab of pane pi for which close(index) is true, except anchor (which is kept and becomes the
    /// active tab if the active one was closed).</summary>
    private void CloseTabsWhere(int pi, int anchor, Func<int, bool> close)
    {
        var pane = Panes[pi];
        if (anchor >= pane.Tabs.Count) return;
        int oldActive = pane.Active;
        var kept = new List<Tab>();
        var closed = new List<string>();
        int? newActive = null;
        int anchorNew = 0;
        for (int i = 0; i < pane.Tabs.Count; i++)
        {
            var t = pane.Tabs[i];
            if (i != anchor && close(i))
            {
                closed.Add(t.Path);
                t.DeepJob?.Dispose();
                continue;
            }
            if (i == oldActive) newActive = kept.Count;
            if (i == anchor) anchorNew = kept.Count;
            kept.Add(t);
        }
        pane.Tabs.Clear();
        pane.Tabs.AddRange(kept);
        pane.Active = newActive ?? anchorNew;
        for (int i = closed.Count - 1; i >= 0; i--) closedTabs.Add((pi, closed[i]));
        dirty = true;
        if (closed.Count > 0) Say($"Closed {closed.Count} tab(s) — Ctrl+Shift+T reopens the last one");
    }

    /// <summary>Selects everything in pane pi that is missing / newer / different in the other pane.</summary>
    private int SelectDiff(int pi)
    {
        var map = Helpers.DiffMap(Panes[pi].Tab, Panes[1 - pi].Tab);
        var t = Panes[pi].Tab;
        t.Selected.Clear();
        foreach (var e in t.Entries)
        {
            if (e.IsUp || e.Deep) continue;
            if (map.TryGetValue(e.Name.ToLowerInvariant(), out var d) && d is Diff.OnlyHere or Diff.Newer or Diff.Size) t.Selected.Add(e.Path);
        }
        return t.Selected.Count;
    }

    // ------------------------------------------------------------------ editing (F4)

    private void LaunchEditor(string file)
    {
        var cmd = Editor.CommandFor(Store.Editor, Store.EditorRules, file);
        try
        {
            Editor.Launch(cmd, file);
            Say($"Editing {VPath.FileName(file)}");
        }
        catch (Exception e)
        {
            Say($"⚠ {e.Message} – set the editor with Ctrl+P → \"Settings: editor\"");
        }
    }

    /// <summary>F4 on a file: local files open directly, archive members in their staging copy, SFTP files are
    /// downloaded first and uploaded again whenever they are saved.</summary>
    private void EditFile(string path)
    {
        if (Sftp.IsRemote(path))
        {
            var name = VPath.DisplayName(path);
            Say($"Downloading {name} for editing…");
            Task.Run(() =>
            {
                try
                {
                    var local = Sftp.FetchTemp(path);
                    Dispatcher.BeginInvoke(() => { StartRemoteEdit(path, local); Render(); });
                }
                catch (Exception e)
                {
                    Dispatcher.BeginInvoke(() => { Say($"⚠ {name}: {e.Message}"); Render(); });
                }
            });
            return;
        }
        var real = path;
        if (Archive.SplitInner(path) != null)
        {
            Say("Preparing the archive for editing…");
            try { real = Archive.RealForEdit(path); }
            catch (Exception e)
            {
                Say($"⚠ {e.Message}");
                return;
            }
        }
        LaunchEditor(real);
    }

    private static (DateTime?, long) Stamp(string f)
    {
        try
        {
            var fi = new FileInfo(f);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : (null, 0);
        }
        catch
        {
            return (null, 0);
        }
    }

    private void StartRemoteEdit(string remote, string local)
    {
        remoteEdits.RemoveAll(e => e.Remote == remote);
        remoteEdits.Add(new RemoteEdit { Remote = remote, Local = local, Seen = Stamp(local) });
        LaunchEditor(local);
        Say($"Editing {VPath.DisplayName(local)} – saving it uploads the file back to the server");
    }

    /// <summary>Uploads edited remote files back once they have been quiet for 700 ms (editors often write twice).</summary>
    private bool PollEdits()
    {
        if (remoteEdits.Count == 0 || (DateTime.Now - editPoll).TotalMilliseconds < 300) return false;
        editPoll = DateTime.Now;
        foreach (var e in remoteEdits)
        {
            if (e.Busy) continue;
            var now = Stamp(e.Local);
            if (now == e.Seen) e.ChangedAt = null;
            else if (e.ChangedAt is { } t)
            {
                if ((DateTime.Now - t).TotalMilliseconds >= 700)
                {
                    e.Busy = true;
                    e.ChangedAt = null;
                    e.Seen = now;
                    var (local, remote) = (e.Local, e.Remote);
                    Task.Run(() =>
                    {
                        string? err = null;
                        try { Sftp.Upload(local, remote, CancellationToken.None, _ => { }); }
                        catch (Exception ex) { err = ex.Message; }
                        Dispatcher.BeginInvoke(() => UploadDone(remote, err));
                    });
                }
            }
            else e.ChangedAt = DateTime.Now;
        }
        return false;
    }

    private void UploadDone(string remote, string? err)
    {
        if (remoteEdits.FirstOrDefault(e => e.Remote == remote) is { } re) re.Busy = false;
        if (err == null)
        {
            Say($"Uploaded {VPath.DisplayName(remote)} ✓");
            var parent = VPath.Parent(remote);
            foreach (var p in Panes)
                if (parent != null && VPath.Same(p.Tab.Path, parent)) p.Tab.Reload(null);
        }
        else Say($"⚠ Upload of {VPath.DisplayName(remote)} failed: {err}");
        Render();
    }

    /// <summary>Asks "update the archive?" once no tab is inside a modified archive any more.</summary>
    private bool CheckArchives()
    {
        if (dialog != null) return false;
        foreach (var zip in Archive.Sessions())
        {
            var key = zip.ToLowerInvariant();
            if (Panes.Any(p => p.Tabs.Any(t => Archive.Contains(zip, t.Path))))
            {
                archSkip.Remove(key);
                continue;
            }
            if (archSkip.Contains(key)) continue;
            if (Archive.IsDirty(zip))
            {
                dialog = new ArchiveUpdateDialog { Zip = zip };
                ShowDialog();
                return true;
            }
            Archive.Discard(zip); // nothing changed: drop the working copy
        }
        return false;
    }

    // ------------------------------------------------------------------ rename / create / alias

    private void StartInline(int pi)
    {
        var tab = Panes[pi].Tab;
        if (tab.Current is not { IsUp: false } e) return;
        Inline = new InlineRename { Pi = pi, Dir = tab.Path, Path = e.Path, Text = VPath.FileName(e.Path) ?? e.Name, IsDir = e.IsDir };
        renameTimer = null;
        var box = views[pi].InlineBox;
        box.Text = Inline.Text;
        Dispatcher.BeginInvoke(() =>
        {
            views[pi].PositionInline();
            box.Focus();
            box.Select(0, Inline.IsDir ? box.Text.Length : Helpers.StemLen(box.Text));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private bool inlineClosing;

    /// <summary>Enter / blur commits, Esc cancels.</summary>
    private void FinishInline(bool commit)
    {
        if (Inline is not { } ir || inlineClosing) return;
        inlineClosing = true;
        var text = views[ir.Pi].InlineBox.Text;
        Inline = null;
        views[ir.Pi].InlineBox.Visibility = Visibility.Collapsed;
        FocusList();
        inlineClosing = false;
        if (commit)
        {
            var name = text.Trim();
            bool unchanged = VPath.FileName(ir.Path) == name;
            if (name.Length > 0 && !unchanged) DoRename(ir.Path, name);
        }
        Render();
    }

    public void InlineCommitOnBlur(int pi)
    {
        if (Inline is { } ir && ir.Pi == pi && !inlineClosing) FinishInline(true);
    }

    private void DoRename(string path, string newName)
    {
        if (Helpers.ValidateName(newName) is { } err)
        {
            Say($"⚠ {err}");
            return;
        }
        var target = VPath.Join(VPath.Parent(path) ?? "", newName);
        bool remote = Sftp.IsRemote(path);
        string rpath = path, rtarget = target;
        // inside an archive the rename happens in its staging copy
        if (Archive.SplitInner(path) != null)
        {
            try
            {
                rpath = Archive.RealForWrite(path);
                rtarget = Archive.RealForWrite(target);
            }
            catch (Exception e)
            {
                Say($"⚠ {e.Message}");
                return;
            }
        }
        bool exists = remote ? Sftp.Exists(rtarget) : File.Exists(rtarget) || Directory.Exists(rtarget);
        if (rtarget != rpath && exists && !string.Equals(rtarget, rpath, StringComparison.OrdinalIgnoreCase))
        {
            Say($"⚠ \"{newName}\" already exists");
            return;
        }
        try
        {
            if (remote) Sftp.Rename(rpath, rtarget);
            else if (Directory.Exists(rpath)) Directory.Move(rpath, rtarget);
            else File.Move(rpath, rtarget);
            Say($"Renamed to {newName}");
            var focus = CurTab.NoteRename(path, target);
            foreach (var p in Panes)
            {
                p.Tab.Selected.Clear();
                p.Tab.Reload(null);
            }
            CurTab.Reload(focus);
        }
        catch (Exception e)
        {
            Say($"⚠ Rename failed: {e.Message}");
        }
    }

    /// <summary>Sets (or, when empty, removes) the alias of an SFTP connection.</summary>
    private void SetAlias(string key, string alias)
    {
        if (alias.Length == 0)
        {
            Store.Aliases.Remove(key);
            Say("Alias removed");
        }
        else
        {
            if (alias.IndexOfAny(['@', '/', '\\', ':', ' ']) >= 0)
            {
                Say("⚠ An alias can't contain spaces or @ / \\ :");
                return;
            }
            if (Store.Aliases.Any(kv => kv.Key != key && string.Equals(kv.Value, alias, StringComparison.OrdinalIgnoreCase)))
            {
                Say($"⚠ Alias \"{alias}\" is already used by another connection");
                return;
            }
            Store.Aliases[key] = alias;
            Say($"{key} is now \"{alias}\" – try sftp://{alias}/");
        }
        Sftp.SetAliases(new Dictionary<string, string>(Store.Aliases));
        Store.Save();
        ReloadAll();
    }

    private void DoCreate(string name, bool folder)
    {
        if (Helpers.ValidateName(name) is { } err)
        {
            Say($"⚠ {err}");
            return;
        }
        var target = VPath.Join(CurTab.Path, name);
        if (Archive.InArchive(target))
        {
            try { target = Archive.RealForWrite(target); }
            catch (Exception e)
            {
                Say($"⚠ {e.Message}");
                return;
            }
        }
        try
        {
            if (Sftp.IsRemote(target))
            {
                if (folder) Sftp.Mkdir(target);
                else Sftp.CreateFile(target);
            }
            else if (folder)
            {
                if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Cannot create a file when that file already exists.");
                Directory.CreateDirectory(target);
            }
            else
            {
                using var _ = new FileStream(target, FileMode.CreateNew);
            }
            Say($"Created {name}");
            ReloadAll();
            CurTab.Reload(name);
        }
        catch (Exception e)
        {
            Say($"⚠ {e.Message}");
        }
    }
}
