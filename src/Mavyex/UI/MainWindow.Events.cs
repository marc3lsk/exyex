using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

public sealed partial class MainWindow
{
    // ------------------------------------------------------------------ keyboard (app.rs handle_keys + widget keys)

    private void WirePaneKeys(PaneView v)
    {
        // inline rename: Enter commits, Esc cancels
        v.InlineBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FinishInline(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { FinishInline(false); e.Handled = true; }
        };
        // search box: result navigation without leaving the box (keys consumed only while it has focus)
        v.FilterBox.PreviewKeyDown += (_, e) => SearchKeys(v, e);
        v.ExcludeBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { FocusList(); e.Handled = true; }
        };
        v.PathBox.PreviewKeyDown += (_, e) => PathEditKeys(v.Pi, e);
    }

    private void SearchKeys(PaneView v, KeyEventArgs e)
    {
        var tab = v.Tab;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int page = v.VisibleRows;
        bool handled = true;
        if (mods == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.Escape: CloseSearch(v.Pi); break;
                case Key.Enter: Run(Act.Open); break;
                case Key.Up: tab.MoveCursor(-1, false); break;
                case Key.Down: tab.MoveCursor(1, false); break;
                case Key.PageUp: tab.MoveCursor(-page, false); break;
                case Key.PageDown: tab.MoveCursor(page, false); break;
                case Key.Tab: FocusList(); break; // hand the keyboard to the list
                default: handled = false; break;
            }
        }
        else if (mods == ModifierKeys.Alt)
        {
            var o = tab.Search;
            switch (key)
            {
                case Key.D1: SetSearch(o with { Mode = SearchMode.Smart }); break;
                case Key.D2: SetSearch(o with { Mode = SearchMode.Fuzzy }); break;
                case Key.D3: SetSearch(o with { Mode = SearchMode.Wildcard }); break;
                case Key.D4: SetSearch(o with { Mode = SearchMode.Regex }); break;
                case Key.C: SetSearch(o with { MatchCase = !o.MatchCase }); break;
                case Key.S: v.KeyToggleSub(); break;
                case Key.T: v.KeyToggleContent(); break;
                case Key.Enter: Run(Act.ShowInFolder); break;
                default: handled = false; break;
            }
        }
        else handled = false;
        if (handled)
        {
            e.Handled = true;
            Render();
            v.EnsureCursorVisible();
        }
    }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (palette != null)
        {
            PaletteKeys(e);
            return;
        }
        if (dialog != null)
        {
            DialogKeys(e);
            return;
        }
        // a text box (path bar, search, inline rename) has the keyboard: its own handlers decide
        if (Keyboard.FocusedElement is TextBox) return;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return;
        renameTimer = null;
        e.Handled = true;
        // Cut / Copy / Paste (Shift+Delete = permanent delete, like Windows reporting it as Cut)
        if (mods == ModifierKeys.Control && key == Key.C) { Run(Act.ClipCopy); return; }
        if (mods == ModifierKeys.Control && key == Key.X) { Run(Act.ClipCut); return; }
        if (mods == ModifierKeys.Control && key == Key.V) { Run(Act.ClipPaste); return; }
        if (mods == ModifierKeys.Shift && key == Key.Delete) { Run(Act.DeletePermanent); return; }
        if (Actions.Bindings.FirstOrDefault(b => b.Key == key && b.Mods == mods) is { } hit)
        {
            Run(hit.Action);
            return;
        }
        var tab = CurTab;
        // Ctrl+1..8 jump to that tab, Ctrl+9 to the last one (like a browser)
        if (mods == ModifierKeys.Control && key is >= Key.D1 and <= Key.D9)
        {
            var pane = Panes[ActivePane];
            int n = key - Key.D1;
            int idx = n == 8 ? pane.Tabs.Count - 1 : n;
            if (idx < pane.Tabs.Count)
            {
                pane.Active = idx;
                dirty = true;
                Render();
            }
            return;
        }
        // cursor movement
        var v = views[ActivePane];
        int cols = Math.Max(1, v.GridCols);
        int page = v.VisibleRows * cols;
        int? d = key switch
        {
            Key.Up => -cols,
            Key.Down => cols,
            Key.PageUp => -page,
            Key.PageDown => page,
            Key.Home => int.MinValue / 2,
            Key.End => int.MaxValue / 2,
            Key.Left when v.GridCols > 0 => -1,
            Key.Right when v.GridCols > 0 => 1,
            _ => null,
        };
        if (d is { } delta && (mods == ModifierKeys.None || mods == ModifierKeys.Shift))
        {
            tab.MoveCursor(delta, mods == ModifierKeys.Shift);
            Render();
            v.EnsureCursorVisible();
            return;
        }
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            if (jobs.Any(j => j.State == JobState.Running))
            {
                foreach (var j in jobs.Where(j => j.State == JobState.Running)) j.Cancel();
                Say("Cancelling…");
            }
            else if (tab.Filter.Length > 0 || tab.FilterOpen)
            {
                tab.Filter = "";
                tab.FilterOpen = false;
                tab.RebuildView();
            }
            else tab.Selected.Clear();
            Render();
            return;
        }
        e.Handled = false; // let text input reach type-ahead
    }

    /// <summary>Type-ahead: just start typing to jump to a file.</summary>
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (palette != null || dialog != null || Keyboard.FocusedElement is TextBox) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Alt)) return;
        var typed = new string(e.Text.Where(c => !char.IsControl(c)).ToArray());
        if (typed.Trim().Length == 0 && !(typed.Length > 0 && typeahead.Text.Length > 0)) return;
        if ((DateTime.Now - typeahead.At).TotalMilliseconds > 900) typeahead.Text = "";
        typeahead = (typeahead.Text + typed, DateTime.Now);
        CurTab.JumpToPrefix(typeahead.Text);
        Say($"Jump: {typeahead.Text}");
        e.Handled = true;
        Render();
        views[ActivePane].EnsureCursorVisible();
    }

    // ------------------------------------------------------------------ list mouse events (app.rs apply)

    public void ActivatePane(int pi)
    {
        renameTimer = null;
        ActivePane = pi;
        Render();
    }

    public void ClickAt(int pi, int vi, bool ctrl, bool shift)
    {
        renameTimer = null;
        var t = Panes[pi].Tab;
        // a plain click on the row that already has the cursor, in an already-active pane, starts a rename
        // unless a double-click follows
        bool slowClick = !ctrl && !shift && prevActive == pi && Inline == null && vi == t.Cursor && vi < t.View.Count &&
                         t.Entries[t.View[vi]] is { IsUp: false } en &&
                         (t.Selected.Count == 0 || (t.Selected.Count == 1 && t.Selected.Contains(en.Path)));
        if (slowClick)
        {
            renameTimer = (pi, t.Path, t.Entries[t.View[vi]].Path, DateTime.Now);
            renameTicker.Stop();
            renameTicker.Start();
        }
        ActivePane = pi;
        prevActive = pi;
        if (vi >= t.View.Count) return;
        if (shift)
        {
            if (!ctrl) t.Selected.Clear();
            t.SelectRange(t.Anchor, vi);
            t.Cursor = vi;
        }
        else if (ctrl)
        {
            // the item under the cursor becomes part of the multi-selection
            if (t.Selected.Count == 0 && t.Cursor != vi && t.Current is { IsUp: false } c) t.Selected.Add(c.Path);
            t.ToggleAt(vi);
            t.Cursor = vi;
            t.Anchor = vi;
        }
        else
        {
            t.Selected.Clear();
            t.Cursor = vi;
            t.Anchor = vi;
        }
        Render();
    }

    private void RenameTimerFired()
    {
        renameTicker.Stop();
        if (renameTimer is not { } rt) return;
        renameTimer = null;
        var tab = Panes[rt.Pi].Tab;
        bool ok = ActivePane == rt.Pi && VPath.Same(tab.Path, rt.Dir) && dialog == null && palette == null && Inline == null &&
                  tab.Current is { IsUp: false } e && VPath.Same(e.Path, rt.Path) &&
                  (tab.Selected.Count == 0 || (tab.Selected.Count == 1 && tab.Selected.Contains(rt.Path)));
        if (ok)
        {
            StartInline(rt.Pi);
            Render();
        }
    }

    public void OpenAt(int pi, int vi)
    {
        renameTimer = null;
        renameTicker.Stop();
        ActivePane = pi;
        Panes[pi].Tab.SetCursor(vi);
        Run(Act.Open);
    }

    public void MiddleOpenAt(int pi, int vi)
    {
        ActivePane = pi;
        Panes[pi].Tab.SetCursor(vi);
        Run(Act.OpenInNewTab);
    }

    public void ContextAt(int pi, int vi)
    {
        renameTimer = null;
        ActivePane = pi;
        var t = Panes[pi].Tab;
        if (vi < t.View.Count)
        {
            if (!t.Selected.Contains(t.Entries[t.View[vi]].Path)) t.Selected.Clear();
            t.Cursor = vi;
        }
        Render();
    }

    public void ShowItemMenu(int pi, int vi, FrameworkElement target)
    {
        var t = Panes[pi].Tab;
        if (vi >= t.View.Count) return;
        var e = t.Entries[t.View[vi]];
        var m = new ContextMenu();
        foreach (var (label, a) in new[]
        {
            ("Open", Act.Open), ("Open in new tab", Act.OpenInNewTab), ("Rename", Act.Rename), ("Copy to other pane", Act.Copy),
            ("Move to other pane", Act.Move), ("Zip…  (Alt+Shift+F5)", Act.Zip), ("Copy", Act.ClipCopy), ("Cut", Act.ClipCut),
            ("Paste", Act.ClipPaste), ("Copy path", Act.CopyPath), ("New folder", Act.NewFolder),
            ("Open containing folder  (Alt+Enter)", Act.ShowInFolder), ("Show in Windows Explorer", Act.Reveal), ("Delete", Act.Delete),
            ("Properties  (Alt+Shift+Enter)", Act.Properties),
        })
        {
            var act = a;
            m.Items.Add(MenuItem(label, () => Run(act)));
        }
        if (e.IsDir && !e.IsUp) m.Items.Add(MenuItem("Add / remove favorite ★", () => Run(Act.FavoriteItem)));
        if (Sftp.IsRemote(e.Path) && !e.IsUp) m.Items.Add(MenuItem("Change owner (chown)…", () => Run(Act.SftpChown)));
        if (Sftp.IsRemote(e.Path)) m.Items.Add(MenuItem("Open in PuTTY", () => Run(Act.SftpPutty)));
        if (Sftp.IsRemote(e.Path)) m.Items.Add(MenuItem("SFTP alias…", () => Run(Act.SftpAlias)));
        m.PlacementTarget = target;
        m.IsOpen = true;
    }

    public void ShowBackgroundMenu(int pi, FrameworkElement target)
    {
        var m = new ContextMenu();
        foreach (var (label, a, enabled) in new[]
        {
            ("New folder", Act.NewFolder, true), ("New file", Act.NewFile, true), ("Paste", Act.ClipPaste, clipboard != null),
            ("Select all", Act.SelectAll, true), ("Refresh", Act.Refresh, true), ("Copy folder path", Act.CopyFolderPath, true),
            ("Open terminal here", Act.Terminal, true), ("Open folder in Windows Explorer", Act.ExplorerHere, true),
            ("Properties", Act.FolderProperties, true), ("Show hidden files", Act.ToggleHidden, true), ("Sort by name", Act.SortName, true),
            ("Sort by date", Act.SortDate, true), ("Sort by size", Act.SortSize, true),
        })
        {
            var act = a;
            m.Items.Add(MenuItem(label, () => Run(act, pi), enabled));
        }
        if (Sftp.Parse(Panes[pi].Tab.Path) != null) m.Items.Add(MenuItem("Open in PuTTY", () => Run(Act.SftpPutty, pi)));
        m.PlacementTarget = target;
        m.IsOpen = true;
    }

    /// <summary>View mode + column toggles (header and footer context menu).</summary>
    public void ShowViewMenu(FrameworkElement target)
    {
        var m = new ContextMenu();
        m.Items.Add(new MenuItem { Header = "View", IsEnabled = false });
        foreach (var (label, a, on) in new[] { ("Details", Act.ViewDetails, Store.View == ViewMode.Details), ("Compact list", Act.ViewCompact, Store.View == ViewMode.Compact), ("Grid with thumbnails", Act.ViewGrid, Store.View == ViewMode.Grid) })
        {
            var act = a;
            m.Items.Add(MenuItem((on ? "● " : "    ") + label, () => Run(act)));
        }
        m.Items.Add(new Separator());
        m.Items.Add(new MenuItem { Header = "Columns", IsEnabled = false });
        foreach (var (label, a, on) in new[] { ("Type", Act.ColExt, Store.Cols.Ext), ("Size", Act.ColSize, Store.Cols.Size), ("Modified", Act.ColDate, Store.Cols.Date) })
        {
            var act = a;
            m.Items.Add(MenuItem((on ? "☑ " : "☐ ") + label, () => Run(act)));
        }
        m.PlacementTarget = target;
        m.IsOpen = true;
    }

    // ------------------------------------------------------------------ pane chrome events

    public void ReloadPane(int pi)
    {
        ActivePane = pi;
        Panes[pi].Tab.Reload(null);
        dirty = true;
        Render();
    }

    public void SortPane(int pi, SortKey k)
    {
        Panes[pi].Tab.SetSort(k);
        Render();
    }

    public void SelectTab(int pi, int i)
    {
        ActivePane = pi;
        Panes[pi].Active = Math.Clamp(i, 0, Panes[pi].Tabs.Count - 1);
        dirty = true;
        Render();
    }

    public void CloseTabAt(int pi, int i)
    {
        var pane = Panes[pi];
        if (pane.Tabs.Count > 1 && i < pane.Tabs.Count)
        {
            var t = pane.Tabs[i];
            pane.Tabs.RemoveAt(i);
            t.DeepJob?.Dispose();
            closedTabs.Add((pi, t.Path));
            if (pane.Active > i || pane.Active >= pane.Tabs.Count) pane.Active = Math.Min(Math.Max(0, pane.Active - 1), pane.Tabs.Count - 1);
            dirty = true;
        }
        Render();
    }

    public void NewTabIn(int pi) => Run(Act.NewTab, pi);

    public void TabMenu(int pi, int i, TabCmd cmd)
    {
        ActivePane = pi;
        if (i >= Panes[pi].Tabs.Count) return;
        var path = Panes[pi].Tabs[i].Path;
        switch (cmd)
        {
            case TabCmd.Close: CloseTabAt(pi, i); break;
            case TabCmd.Others: CloseTabsWhere(pi, i, _ => true); break;
            case TabCmd.Left: CloseTabsWhere(pi, i, j => j < i); break;
            case TabCmd.Right: CloseTabsWhere(pi, i, j => j > i); break;
            case TabCmd.Duplicate:
                Panes[pi].Active = i;
                OpenNewTab(pi, path);
                break;
            case TabCmd.OpenOther:
                OpenNewTab(1 - pi, path);
                ActivePane = 1 - pi;
                break;
            case TabCmd.MoveOther:
                if (Panes[pi].Tabs.Count > 1)
                {
                    CloseTabAt(pi, i);
                    closedTabs.RemoveAt(closedTabs.Count - 1); // moved, not closed
                    OpenNewTab(1 - pi, path);
                    ActivePane = 1 - pi;
                }
                break;
            case TabCmd.CopyPath: SetClipboardText(path); break;
        }
        dirty = true;
        Render();
    }

    public void Go(int pi, string p)
    {
        ActivePane = pi;
        Navigate(pi, p);
        Render();
    }

    public void ToggleFavoriteOf(int pi)
    {
        ActivePane = pi;
        Run(Act.ToggleFavorite);
    }

    public void SetSearch(SearchOpts o)
    {
        Store.Search = o;
        foreach (var p in Panes)
            foreach (var t in p.Tabs)
            {
                t.Search = o;
                if (t.Filter.Length > 0)
                {
                    t.RebuildView();
                    t.SetCursor(0);
                }
            }
        dirty = true;
        Render();
    }

    public void SetExclude(string s)
    {
        Store.Exclude = s;
        foreach (var p in Panes)
            foreach (var t in p.Tabs) t.Exclude = s;
        dirty = true;
        Render();
    }

    public void CloseSearch(int pi)
    {
        var t = Panes[pi].Tab;
        t.Filter = "";
        t.FilterOpen = false;
        t.RebuildView();
        FocusList();
        Render();
    }

    /// <summary>A typed path (path bar, palette): navigate, or open the folder of a file with the cursor on it.</summary>
    private void GoTyped(int pi, string s)
    {
        ActivePane = pi;
        var baseDir = Panes[pi].Tab.Path;
        if (VPath.ParseUserPath(s, baseDir) is not { } p) return;
        bool archiveOk = false;
        if (Archive.Split(p) is { } a)
        {
            try { archiveOk = a.Inner.Length == 0 || Archive.List(a.Zip, a.Inner).Count > 0; } catch { }
        }
        if (Fs.IsDirPath(p) && (Archive.Split(p) == null || archiveOk)) Navigate(pi, p);
        else if (File.Exists(p))
        {
            if (VPath.Parent(p) is { } parent && VPath.FileName(p) is { } name && Navigate(pi, parent)) Panes[pi].Tab.Reload(name);
        }
        else Say($"⚠ Path not found: {p}");
    }

    // ------------------------------------------------------------------ path bar editing

    public void PathEditChanged(int pi, string text)
    {
        if (Panes[pi].PathEdit is not { } pe || pe.Text == text) return;
        pe.Text = text;
        UpdateSuggestions(pi);
        views[pi].Render();
    }

    private void UpdateSuggestions(int pi)
    {
        if (Panes[pi].PathEdit is not { } pe || pe.SuggFor == pe.Text) return;
        pe.Sugg = PathSuggestions.Compute(pe.Text, pe.Orig, Panes[pi].Tab.Path, Store);
        pe.SuggFor = pe.Text;
        pe.Sel = null;
    }

    private void ClosePathEdit(int pi)
    {
        Panes[pi].PathEdit = null;
        FocusList();
        Render();
    }

    public void PathEditLostFocus(int pi, DependencyObject? newFocus)
    {
        if (Panes[pi].PathEdit == null) return;
        // clicking the popup must not close the editor
        if (views[pi].IsInSuggestions(newFocus) || views[pi].IsMouseOverSuggestions()) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Panes[pi].PathEdit != null && !views[pi].PathBox.IsKeyboardFocused && !views[pi].IsMouseOverSuggestions()) ClosePathEdit(pi);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PathEditKeys(int pi, KeyEventArgs e)
    {
        if (Panes[pi].PathEdit is not { } pe) return;
        UpdateSuggestions(pi);
        var mods = Keyboard.Modifiers;
        bool handled = true;
        switch (e.Key)
        {
            case Key.Escape when mods == ModifierKeys.None:
                ClosePathEdit(pi);
                break;
            case Key.Enter when mods == ModifierKeys.None:
            {
                var target = pe.Sel is { } s && s < pe.Sugg.Count ? pe.Sugg[s].Path : pe.Text;
                Panes[pi].PathEdit = null;
                GoTyped(pi, target);
                FocusList();
                Render();
                break;
            }
            case Key.Down when mods == ModifierKeys.None:
                if (pe.Sugg.Count > 0) pe.Sel = pe.Sel is { } d ? Math.Min(d + 1, pe.Sugg.Count - 1) : 0;
                views[pi].Render();
                break;
            case Key.Up when mods == ModifierKeys.None:
                pe.Sel = pe.Sel is null or 0 ? null : pe.Sel - 1;
                views[pi].Render();
                break;
            case Key.Tab when mods == ModifierKeys.None:
            case Key.Right when mods == ModifierKeys.None && pe.Sel != null:
            {
                // take the highlighted (or first) suggestion into the field
                var sg = pe.Sel is { } s2 && s2 < pe.Sugg.Count ? pe.Sugg[s2] : pe.Sugg.FirstOrDefault();
                if (sg != null)
                {
                    pe.Text = Helpers.WithTrailingSep(sg.Path);
                    pe.Fresh = true;
                    pe.CaretAtEnd = true;
                    UpdateSuggestions(pi);
                    views[pi].Render();
                }
                break;
            }
            case Key.Delete when mods == ModifierKeys.None && pe.Sel is { } s3 && s3 < pe.Sugg.Count && pe.Sugg[s3].Tag.Length > 0:
                // Delete on a highlighted favorite / recent entry removes it from the list
                ForgetSuggestion(pi, pe.Sugg[s3]);
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    public void ForgetSuggestion(int pi, Sugg sg)
    {
        if (sg.Tag == "recent") Store.ForgetVisit(sg.Path);
        else Store.RemoveFavorite(sg.Path);
        Store.Save();
        if (Panes[pi].PathEdit is { } pe)
        {
            pe.SuggFor = "\u0001"; // force the list to be rebuilt
            UpdateSuggestions(pi);
        }
        views[pi].Render();
        views[pi].PathBox.Focus(); // keep typing after removing an entry
    }

    public void StarSuggestion(int pi, Sugg sg)
    {
        if (!Store.IsFavorite(sg.Path)) Store.ToggleFavorite(sg.Path);
        Store.Save();
        if (Panes[pi].PathEdit is { } pe)
        {
            pe.SuggFor = "\u0001";
            UpdateSuggestions(pi);
        }
        views[pi].Render();
        views[pi].PathBox.Focus();
    }

    public void SuggestionClicked(int pi, int idx)
    {
        if (Panes[pi].PathEdit is not { } pe || idx >= pe.Sugg.Count) return;
        var sg = pe.Sugg[idx];
        if (sg.Tag.Length == 0)
        {
            // a subfolder: keep editing inside it
            pe.Text = Helpers.WithTrailingSep(sg.Path);
            pe.Fresh = true;
            pe.CaretAtEnd = true;
            UpdateSuggestions(pi);
            views[pi].Render();
        }
        else
        {
            Panes[pi].PathEdit = null;
            GoTyped(pi, sg.Path);
            FocusList();
            Render();
        }
    }

    // ------------------------------------------------------------------ drag & drop

    public static List<string>? DraggedPaths(IDataObject data)
    {
        if (data.GetDataPresent(PathsFormat) && data.GetData(PathsFormat) is string[] own) return own.ToList();
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files) return files.ToList();
        return null;
    }

    public void StartFileDrag(int pi, List<string> paths, FrameworkElement source)
    {
        ActivePane = pi;
        var data = new DataObject();
        data.SetData(PathsFormat, paths.ToArray());
        // real local files can also be dropped onto Explorer / other apps
        var local = paths.Where(Helpers.IsLocal).ToArray();
        if (local.Length > 0) data.SetData(DataFormats.FileDrop, local);
        try
        {
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            foreach (var v in views) v.SetDropTarget(null);
            HighlightDropPane(null);
            ReloadAll(); // an external target may have moved the files
            Render();
        }
    }

    /// <summary>Move or copy? Internal drags: Shift = move, Ctrl = copy, otherwise move within a volume
    /// (Rust: same_volume). Files dropped from Explorer are always copied.</summary>
    public bool DropMove(List<string> paths, string dest, DragDropKeyStates keys, bool internalDrag)
    {
        if (!internalDrag) return false;
        bool ctrl = keys.HasFlag(DragDropKeyStates.ControlKey), shift = keys.HasFlag(DragDropKeyStates.ShiftKey);
        return shift || (!ctrl && paths.Count > 0 && Helpers.SameVolume(paths[0], dest));
    }

    public void DropFiles(int pi, List<string> paths, string dest, DragDropKeyStates keys, bool internalDrag)
    {
        if (paths.Count == 0) return;
        bool sameDir = paths.All(p => VPath.Parent(p) is { } par && VPath.Same(par, dest));
        if (sameDir || paths.Any(p => VPath.Same(p, dest))) return;
        ActivePane = pi;
        StartOp(DropMove(paths, dest, keys, internalDrag) ? OpKind.Move : OpKind.Copy, paths, dest);
        Render();
    }

    private int? dropPane;

    public void HighlightDropPane(int? pi)
    {
        if (dropPane == pi) return;
        dropPane = pi;
        for (int i = 0; i < 2; i++)
            views[i].Effect = pi == i ? new System.Windows.Media.Effects.DropShadowEffect { Color = Paint.Accent, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 } : null;
    }

    // --- tabs: reorder inside a strip, drop on the other pane = open there too (Shift = move)

    private (int Pi, int Idx)? tabDrag;

    public void StartTabDrag(int pi, int idx, FrameworkElement source)
    {
        tabDrag = (pi, idx);
        var data = new DataObject(TabFormat, $"{pi}:{idx}");
        try
        {
            DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
        }
        finally
        {
            tabDrag = null;
            foreach (var v in views) v.ShowInsertMark(null);
            HighlightDropPane(null);
            Render();
        }
    }

    public void TabInsertMark(int pi, double? x) => views[pi].ShowInsertMark(x);

    public void TabDragOverStrip(int pi, double x)
    {
        if (tabDrag is not { } td) return;
        if (td.Pi == pi)
        {
            HighlightDropPane(null);
            views[pi].ShowInsertMark(views[pi].TabStripInsertX(views[pi].TabInsertIndex(x)));
        }
        else TabDragOverPane(pi);
    }

    public void TabDragOverPane(int pi)
    {
        if (tabDrag is not { } td) return;
        views[td.Pi].ShowInsertMark(null);
        HighlightDropPane(td.Pi != pi ? pi : null);
    }

    public void TabDropOnStrip(int pi, double x)
    {
        if (tabDrag is not { } td) return;
        if (td.Pi != pi)
        {
            TabDropOnPane(pi, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            return;
        }
        int at = views[pi].TabInsertIndex(x);
        var pane = Panes[pi];
        int idx = td.Idx;
        if (at != idx && at != idx + 1 && idx < pane.Tabs.Count)
        {
            var t = pane.Tabs[idx];
            pane.Tabs.RemoveAt(idx);
            int ins = at > idx ? at - 1 : at;
            pane.Tabs.Insert(ins, t);
            pane.Active = pane.Active == idx ? ins
                : idx < pane.Active && ins >= pane.Active ? pane.Active - 1
                : idx > pane.Active && ins <= pane.Active ? pane.Active + 1
                : pane.Active;
            dirty = true;
        }
        Render();
    }

    public void TabDropOnPane(int target, bool shift)
    {
        if (tabDrag is not { } td || td.Pi == target || td.Idx >= Panes[td.Pi].Tabs.Count) return;
        var path = Panes[td.Pi].Tabs[td.Idx].Path;
        OpenNewTab(target, path);
        ActivePane = target;
        var from = Panes[td.Pi];
        if (shift && from.Tabs.Count > 1)
        {
            from.Tabs.RemoveAt(td.Idx);
            if (from.Active > td.Idx || from.Active >= from.Tabs.Count) from.Active = Math.Min(Math.Max(0, from.Active - 1), from.Tabs.Count - 1);
        }
        dirty = true;
        Render();
    }
}
