using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

/// <summary>Command palette and the modal dialogs, shown as in-window overlays (egui windows in the Rust version).</summary>
public sealed partial class MainWindow
{
    private Border? overlayCard;

    private void HideOverlay()
    {
        overlay.Children.Clear();
        overlay.Visibility = Visibility.Collapsed;
        overlayCard = null;
        FocusList();
    }

    private Border Card(double width)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Width = width,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = Theme.IsDark ? 0.55 : 0.3 },
        };
        b.SetResourceReference(Border.BackgroundProperty, "PanelBg");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        b.BorderThickness = new Thickness(1);
        return b;
    }

    private static TextBlock Label(string s, bool weak = false, bool strong = false, double size = 13)
    {
        var t = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), FontSize = size };
        if (strong) t.FontWeight = FontWeights.SemiBold;
        t.SetResourceReference(TextBlock.ForegroundProperty, weak ? "WeakFg" : "Fg");
        return t;
    }

    // ------------------------------------------------------------------ palette

    private void OpenPalette(bool foldersOnly)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<(string, string, int)>();
        void Add(string p, string tag, int bonus)
        {
            if (p.Length > 0 && seen.Add(p)) folders.Add((p, tag, bonus));
        }
        foreach (var f in Store.Favorites) Add(f, "★ favorite", 2000);
        foreach (var p in Panes.SelectMany(p => p.Tabs.Select(t => t.Path))) Add(p, "open tab", 300);
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (label, p) in new[]
        {
            ("home", up), ("desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("downloads", Path.Combine(up, "Downloads")), ("documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
        })
            Add(p, label, 100);
        foreach (var d in Drives) Add(d, "drive", 100);
        foreach (var (p, score) in Store.Frecent().Take(150))
            if (Fs.IsDirPath(p)) Add(p, "frequent", Math.Min((int)score, 250));
        palette = new PaletteState { FoldersOnly = foldersOnly, Folders = folders };
        ShowPalette(fresh: true);
    }

    private List<PaletteItem> PaletteItems(PaletteState pal)
    {
        var q = pal.Query.Trim();
        if (q.Length == 0)
            return pal.FoldersOnly ? pal.Folders.Select(f => (PaletteItem)new FolderItem(f.Path, f.Tag)).ToList()
                                   : Actions.PaletteActions.Select(a => (PaletteItem)new ActionItem(a)).ToList();
        var scored = new List<(int, PaletteItem)>();
        if (VPath.ParseUserPath(q, CurTab.Path) is { } p && (q.IndexOfAny(['\\', '/', ':']) >= 0 || q.StartsWith('~') || q.StartsWith('%')) &&
            (File.Exists(p) || Directory.Exists(p)))
            scored.Add((100_000, new GotoItem(p)));
        if (!pal.FoldersOnly)
            foreach (var a in Actions.PaletteActions)
                if (Text.Fuzzy(q, Actions.Name(a)) is { } s) scored.Add((s + 20, new ActionItem(a)));
        foreach (var (path, tag, bonus) in pal.Folders)
        {
            int? a1 = Text.Fuzzy(q, path), a2 = Text.Fuzzy(q, VPath.DisplayName(path)) + 20;
            int? best = a1 is null ? a2 : a2 is null ? a1 : Math.Max(a1.Value, a2.Value);
            if (best is { } s) scored.Add((s + bonus / 4, new FolderItem(path, tag)));
        }
        return scored.OrderByDescending(x => x.Item1).Select(x => x.Item2).ToList();
    }

    private TextBox? paletteBox;
    private StackPanel? paletteList;

    private void ShowPalette(bool fresh)
    {
        if (palette is not { } pal) return;
        if (fresh || paletteBox == null)
        {
            overlay.Children.Clear();
            overlay.Background = Brushes.Transparent;
            overlay.Visibility = Visibility.Visible;
            var card = Card(640);
            card.VerticalAlignment = VerticalAlignment.Top;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.Margin = new Thickness(0, 70, 0, 0);
            card.Padding = new Thickness(8);
            var stack = new StackPanel();
            paletteBox = new TextBox
            {
                FontSize = 17, Height = 34,
                Tag = pal.FoldersOnly ? "Jump to folder…  (Enter: here · Shift+Enter: new tab · Alt+Enter: other pane)" : "Type a command, folder, or paste a path…",
            };
            paletteBox.TextChanged += (_, _) =>
            {
                if (palette == null) return;
                palette.Query = paletteBox.Text;
                palette.Sel = 0;
                RenderPaletteItems();
            };
            stack.Children.Add(paletteBox);
            paletteList = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            stack.Children.Add(paletteList);
            card.Child = stack;
            overlayCard = card;
            overlay.Children.Add(card);
            // click outside closes
            overlay.MouseLeftButtonDown += OverlayClickOutside;
            Dispatcher.BeginInvoke(() => paletteBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        RenderPaletteItems();
    }

    private void OverlayClickOutside(object sender, MouseButtonEventArgs e)
    {
        if (palette != null && overlayCard != null && !overlayCard.IsMouseOver)
        {
            palette = null;
            overlay.MouseLeftButtonDown -= OverlayClickOutside;
            HideOverlay();
            Render();
        }
    }

    private void RenderPaletteItems()
    {
        if (palette is not { } pal || paletteList == null) return;
        var items = PaletteItems(pal);
        int last = Math.Max(0, Math.Min(items.Count - 1, 11));
        pal.Sel = Math.Min(pal.Sel, last);
        paletteList.Children.Clear();
        if (items.Count == 0) paletteList.Children.Add(Label("No matches", weak: true));
        for (int i = 0; i < Math.Min(12, items.Count); i++)
        {
            var (left, right) = items[i] switch
            {
                ActionItem a => (Actions.Name(a.Action), Actions.ShortcutLabel(a.Action)),
                FolderItem f => (f.Path, f.Tag),
                GotoItem g => ($"Go to {g.Path}", "path"),
                _ => ("", ""),
            };
            var row = new Border { Height = 24, CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand };
            bool sel = i == pal.Sel;
            row.Background = sel ? Paint.B(Paint.Mul(Paint.Accent, 0.45)) : Brushes.Transparent;
            row.MouseEnter += (_, _) => { if (!sel) row.Background = Paint.B(Paint.Tint(14)); };
            row.MouseLeave += (_, _) => { if (!sel) row.Background = Brushes.Transparent; };
            var d = new DockPanel();
            var r = new TextBlock { Text = right, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            r.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");
            DockPanel.SetDock(r, Dock.Right);
            d.Children.Add(r);
            var l = new TextBlock { Text = left, FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            l.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            d.Children.Add(l);
            row.Child = d;
            var item = items[i];
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; ExecPalette(item, OpenMode.Here); };
            paletteList.Children.Add(row);
        }
    }

    private void PaletteKeys(KeyEventArgs e)
    {
        if (palette is not { } pal) return;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var items = PaletteItems(pal);
        int last = Math.Max(0, Math.Min(items.Count - 1, 11));
        switch (key)
        {
            case Key.Escape:
                palette = null;
                HideOverlay();
                Render();
                e.Handled = true;
                break;
            case Key.Down:
                pal.Sel = Math.Min(pal.Sel + 1, last);
                RenderPaletteItems();
                e.Handled = true;
                break;
            case Key.Up:
                pal.Sel = Math.Max(0, pal.Sel - 1);
                RenderPaletteItems();
                e.Handled = true;
                break;
            case Key.Enter:
                if (pal.Sel < items.Count)
                    ExecPalette(items[pal.Sel], mods == ModifierKeys.Shift ? OpenMode.NewTab : mods == ModifierKeys.Alt ? OpenMode.OtherPane : OpenMode.Here);
                else
                {
                    palette = null;
                    HideOverlay();
                }
                e.Handled = true;
                break;
        }
    }

    private void ExecPalette(PaletteItem item, OpenMode mode)
    {
        palette = null;
        overlay.MouseLeftButtonDown -= OverlayClickOutside;
        HideOverlay();
        switch (item)
        {
            case ActionItem a:
                Run(a.Action);
                return;
            case FolderItem or GotoItem:
            {
                var p = item is FolderItem f ? f.Path : ((GotoItem)item).Path;
                if (!Fs.IsDirPath(p))
                {
                    // goto of a file: open its folder and focus the file
                    if (VPath.Parent(p) is { } parent && VPath.FileName(p) is { } name && Navigate(ActivePane, parent)) CurTab.Reload(name);
                    break;
                }
                switch (mode)
                {
                    case OpenMode.Here: Navigate(ActivePane, p); break;
                    case OpenMode.NewTab: OpenNewTab(ActivePane, p); break;
                    case OpenMode.OtherPane:
                        Navigate(Other, p);
                        ActivePane = Other;
                        break;
                }
                break;
            }
        }
        Render();
    }

    // ------------------------------------------------------------------ dialogs

    private Button? okButton;
    private TextBox? firstBox;

    private void ShowDialog()
    {
        if (dialog is not { } dlg) return;
        overlay.Children.Clear();
        overlay.Background = Paint.B(Color.FromArgb(40, 0, 0, 0));
        overlay.Visibility = Visibility.Visible;
        var card = Card(460);
        card.VerticalAlignment = VerticalAlignment.Center;
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.Margin = new Thickness(0, 0, 0, 80);
        var stack = new StackPanel();
        var title = dlg switch
        {
            NewFolderDialog => "New folder",
            NewFileDialog => "New file",
            ZipDialog => "Zip",
            ZipExistsDialog => "Archive already exists",
            SftpAliasDialog => "SFTP alias",
            ChownDialog => "Change owner",
            EditorDialog => "Editor for F4",
            ArchiveUpdateDialog => "Archive changed",
            DeleteDialog { Permanent: true } => "Delete permanently",
            DeleteDialog => "Delete",
            TransferDialog { Kind: OpKind.Move } => "Move",
            TransferDialog => "Copy",
            ConflictDialog => "File already exists",
            _ => "",
        };
        stack.Children.Add(Label(title, strong: true, size: 14));
        firstBox = null;
        okButton = null;
        TextBox Field(string text, int? selectChars = null, double width = double.NaN)
        {
            var tb = new TextBox { Text = text, Height = 28, Margin = new Thickness(0, 2, 0, 6), Width = width };
            if (firstBox == null)
            {
                firstBox = tb;
                Dispatcher.BeginInvoke(() =>
                {
                    tb.Focus();
                    tb.Select(0, selectChars ?? tb.Text.Length);
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
            return tb;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        Button Btn(string text, Action click, bool accent = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
            if (accent) b.Style = (Style)FindResource("AccentButton");
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
            return b;
        }
        void OkCancel(Action ok, bool confirm)
        {
            okButton = Btn("  OK  ", ok, accent: confirm);
            Btn("Cancel", CancelDialog);
            if (confirm || firstBox == null)
                Dispatcher.BeginInvoke(() => okButton?.Focus(), System.Windows.Threading.DispatcherPriority.Input); // OK is the default button
        }
        string Dn(string p) => VPath.DisplayName(p);
        void List(List<string> paths)
        {
            foreach (var p in paths.Take(6)) stack.Children.Add(Label(Dn(p), weak: true));
            if (paths.Count > 6) stack.Children.Add(Label($"… and {paths.Count - 6} more", weak: true));
        }

        switch (dlg)
        {
            case NewFolderDialog nf:
            {
                var tb = Field(nf.Text);
                stack.Children.Add(tb);
                OkCancel(() => { CloseDialog(); DoCreate(tb.Text.Trim(), true); Render(); }, false);
                break;
            }
            case NewFileDialog nfi:
            {
                var tb = Field(nfi.Text, Helpers.StemLen(nfi.Text));
                stack.Children.Add(tb);
                OkCancel(() => { CloseDialog(); DoCreate(tb.Text.Trim(), false); Render(); }, false);
                break;
            }
            case EditorDialog ed:
            {
                stack.Children.Add(Label("Command to edit a file (empty = Notepad)"));
                stack.Children.Add(Label("Placeholders: {file} {dir} {line}.  The file is appended when {file} is missing.", weak: true));
                var cmd = Field(ed.Cmd);
                stack.Children.Add(cmd);
                stack.Children.Add(Label("e.g.  code {file}      \"C:\\Program Files\\Notepad++\\notepad++.exe\" -n{line} {file}", weak: true));
                stack.Children.Add(Label("Per-extension rules – one per line:  ext1,ext2 = command   (first match wins, * = any)"));
                var rules = new TextBox { Text = ed.Rules, AcceptsReturn = true, Height = 110, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalContentAlignment = VerticalAlignment.Top, Tag = "rs,toml = code {file}\nmd = typora {file}" };
                stack.Children.Add(rules);
                card.Width = 560;
                OkCancel(() =>
                {
                    Store.Editor = cmd.Text.Trim();
                    Store.EditorRules = rules.Text;
                    Store.Save();
                    Say("Editor saved – F4 now uses it");
                    CloseDialog();
                    Render();
                }, false);
                break;
            }
            case ArchiveUpdateDialog au:
            {
                stack.Children.Add(Label($"You changed the contents of \"{Dn(au.Zip)}\"."));
                stack.Children.Add(Label(au.Quit ? "Write the changes back into the archive before exiting?" : "Update the archive with your changes?"));
                var upd = Btn("  Update archive  ", () => ArchiveChoice(au, true), accent: true);
                Btn("Discard changes", () => ArchiveChoice(au, false));
                var later = Btn(au.Quit ? "Cancel" : "Later", CancelDialog);
                later.ToolTip = "Keep the changes for now (Esc)";
                Dispatcher.BeginInvoke(() => upd.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                break;
            }
            case ChownDialog ch:
            {
                stack.Children.Add(Label(ch.Paths.Count == 1 ? $"Change owner of \"{Dn(ch.Paths[0])}\"" : $"Change owner of {ch.Paths.Count} items"));
                stack.Children.Add(Label("Runs chown on the server. A name or a number; leave a field empty to keep it.", weak: true));
                var g = new Grid { Margin = new Thickness(0, 2, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                g.RowDefinitions.Add(new RowDefinition());
                g.RowDefinitions.Add(new RowDefinition());
                var owner = Field(ch.Owner, width: 220);
                var group = new TextBox { Text = ch.Group, Height = 28, Width = 220, Margin = new Thickness(0, 2, 0, 6) };
                void Cell(UIElement el, int r, int c) { Grid.SetRow(el, r); Grid.SetColumn(el, c); g.Children.Add(el); }
                var lo = Label("Owner"); lo.VerticalAlignment = VerticalAlignment.Center;
                var lg = Label("Group"); lg.VerticalAlignment = VerticalAlignment.Center;
                Cell(lo, 0, 0); Cell(owner, 0, 1); Cell(lg, 1, 0); Cell(group, 1, 1);
                stack.Children.Add(g);
                var rec = new CheckBox { Content = "Recursive (-R) – include everything inside folders", IsChecked = ch.Recursive, Margin = new Thickness(0, 2, 0, 4) };
                var sudo = new CheckBox { Content = "Use sudo (passwordless sudo must be allowed)", IsChecked = ch.Sudo };
                stack.Children.Add(rec);
                stack.Children.Add(sudo);
                OkCancel(() =>
                {
                    var (o, gr, r, s) = (owner.Text, group.Text, rec.IsChecked == true, sudo.IsChecked == true);
                    if (o.Trim().Length == 0 && gr.Trim().Length == 0)
                    {
                        Say("Enter an owner and/or a group");
                        Render();
                        return;
                    }
                    CloseDialog();
                    Say("Changing owner…");
                    var paths = ch.Paths;
                    Task.Run(() =>
                    {
                        string msg;
                        bool ok = true;
                        try { msg = Sftp.Chown(paths, o, gr, r, s); }
                        catch (Exception ex) { msg = $"⚠ {ex.Message}"; ok = false; }
                        Dispatcher.BeginInvoke(() => { Say(msg); if (ok) ReloadAll(); Render(); });
                    });
                    Render();
                }, false);
                break;
            }
            case SftpAliasDialog sa:
            {
                stack.Children.Add(Label($"Alias for {sa.Key}"));
                stack.Children.Add(Label("Use it like sftp://alias/path . Leave empty to remove the alias.", weak: true));
                var tb = Field(sa.Text);
                stack.Children.Add(tb);
                OkCancel(() => { CloseDialog(); SetAlias(sa.Key, tb.Text.Trim()); Render(); }, false);
                break;
            }
            case ZipExistsDialog ze:
            {
                stack.Children.Add(Label($"\"{ze.Name}\" already exists in this folder."));
                var keep = Btn(" Keep both ", () => ZipChoice(ze, true), accent: true);
                keep.ToolTip = "Create \"name (2).zip\"";
                Btn("Replace", () => ZipChoice(ze, false));
                Btn("Cancel", CancelDialog);
                Dispatcher.BeginInvoke(() => keep.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                break;
            }
            case ZipDialog z:
            {
                stack.Children.Add(Label(z.Paths.Count == 1 ? $"Zip \"{Dn(z.Paths[0])}\" into:" : $"Zip {z.Paths.Count} items into:"));
                stack.Children.Add(Label("Format by extension: .zip, .tar.gz / .tgz, .tar, or .gz (single file)", weak: true));
                var tb = Field(z.Text, Helpers.StemLen(z.Text));
                stack.Children.Add(tb);
                OkCancel(() => ZipOk(z, tb.Text), false);
                break;
            }
            case TransferDialog tr:
            {
                var verb = tr.Kind == OpKind.Move ? "Move" : "Copy";
                stack.Children.Add(Label(tr.Paths.Count == 1 ? $"{verb} \"{Dn(tr.Paths[0])}\" to:" : $"{verb} {tr.Paths.Count} items to:"));
                stack.Children.Add(Label(tr.Dest, strong: true));
                if (tr.Paths.Count > 1) List(tr.Paths);
                OkCancel(() => { CloseDialog(); StartOp(tr.Kind, tr.Paths, tr.Dest); Render(); }, true);
                break;
            }
            case ConflictDialog cf:
            {
                var src = cf.Queue[0];
                var name = VPath.FileName(src) ?? "";
                stack.Children.Add(Label($"\"{name}\" already exists in:"));
                stack.Children.Add(Label(cf.Dest, strong: true));
                static string Info(string p)
                {
                    try
                    {
                        if (Directory.Exists(p)) return $"folder, {Text.FmtTime(Directory.GetLastWriteTimeUtc(p))}";
                        var fi = new FileInfo(p);
                        return fi.Exists ? $"{Text.FmtSize(fi.Length)}, {Text.FmtTime(fi.LastWriteTimeUtc)}" : "?";
                    }
                    catch { return "?"; }
                }
                var ex = cf.Existing.FirstOrDefault(e => e.Name == name);
                var existing = ex == null ? Info(VPath.Join(cf.Dest, name)) : ex.IsDir ? $"folder, {Text.FmtTime(ex.Modified)}" : $"{Text.FmtSize(ex.Size)}, {Text.FmtTime(ex.Modified)}";
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.RowDefinitions.Add(new RowDefinition());
                g.RowDefinitions.Add(new RowDefinition());
                void Cell(UIElement el, int r, int c) { Grid.SetRow(el, r); Grid.SetColumn(el, c); g.Children.Add(el); }
                Cell(Label("Existing:", weak: true), 0, 0); Cell(Label(existing), 0, 1);
                Cell(Label("New:", weak: true), 1, 0); Cell(Label(Info(src)), 1, 1);
                stack.Children.Add(g);
                var keep = Btn(" Keep both ", () => ConflictChoice(cf, Policy.Rename), accent: true);
                keep.ToolTip = "Save as \"name (2)\"";
                Btn("Overwrite", () => ConflictChoice(cf, Policy.Overwrite));
                Btn("Skip", () => ConflictChoice(cf, Policy.Skip));
                if (!Sftp.IsRemote(cf.Dest))
                {
                    var nb = Btn("Overwrite if newer", () => ConflictChoice(cf, Policy.Newer));
                    nb.ToolTip = "Replace only when the new file is newer";
                }
                Btn("Cancel", CancelDialog);
                Dispatcher.BeginInvoke(() => keep.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                if (cf.Queue.Count > 1)
                {
                    var all = new CheckBox { Content = $"Do this for all {cf.Queue.Count - 1} remaining conflicts", IsChecked = cf.All, Margin = new Thickness(0, 8, 0, 0) };
                    all.Click += (_, _) => cf.All = all.IsChecked == true;
                    stack.Children.Add(buttons);
                    stack.Children.Add(all);
                    card.Width = 560;
                    card.Child = stack;
                    overlayCard = card;
                    overlay.Children.Add(card);
                    return;
                }
                card.Width = 560;
                break;
            }
            case DeleteDialog del:
            {
                if (del.Permanent)
                {
                    var warn = Label("This cannot be undone.");
                    warn.SetResourceReference(TextBlock.ForegroundProperty, "ErrorFg");
                    stack.Children.Add(warn);
                }
                var verb = del.Permanent ? "Permanently delete" : "Move to Recycle Bin:";
                stack.Children.Add(Label(del.Paths.Count == 1 ? $"{verb} \"{Dn(del.Paths[0])}\"?" : $"{verb} {del.Paths.Count} items?"));
                List(del.Paths);
                OkCancel(() => { CloseDialog(); StartOp(del.Permanent ? OpKind.Delete : OpKind.Trash, del.Paths, null); Render(); }, true);
                break;
            }
        }
        stack.Children.Add(buttons);
        card.Child = stack;
        overlayCard = card;
        overlay.Children.Add(card);
    }

    private void CloseDialog()
    {
        dialog = null;
        HideOverlay();
    }

    private void CancelDialog()
    {
        if (dialog is ArchiveUpdateDialog { Quit: false } au) archSkip.Add(au.Zip.ToLowerInvariant());
        CloseDialog();
        Render();
    }

    /// <summary>Esc cancels; Enter = OK in text dialogs (in confirm dialogs Enter is left to the focused button).</summary>
    private void DialogKeys(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelDialog();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || dialog == null) return;
        bool keepsEnter = dialog is DeleteDialog or TransferDialog or ConflictDialog or EditorDialog or ArchiveUpdateDialog or ZipExistsDialog;
        if (Keyboard.FocusedElement is Button b)
        {
            b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            e.Handled = true;
            return;
        }
        if (dialog is EditorDialog && Keyboard.FocusedElement is TextBox { AcceptsReturn: true }) return; // newline in the rules box
        if (!keepsEnter || dialog is EditorDialog)
        {
            okButton?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            e.Handled = true;
        }
    }

    private void ConflictChoice(ConflictDialog cf, Policy policy)
    {
        if (cf.All)
        {
            foreach (var q in cf.Queue) cf.Chosen[q] = policy;
            cf.Queue.Clear();
        }
        else
        {
            cf.Chosen[cf.Queue[0]] = policy;
            cf.Queue.RemoveAt(0);
        }
        if (cf.Queue.Count == 0)
        {
            CloseDialog();
            SpawnOp(cf.Kind, cf.Srcs, cf.Dest, cf.Chosen);
        }
        else
        {
            cf.All = false;
            ShowDialog();
        }
        Render();
    }

    private void ZipChoice(ZipExistsDialog ze, bool keepBoth)
    {
        CloseDialog();
        var target = keepBoth ? Ops.UniqueDest(ze.Dir, ze.Name) : VPath.Join(ze.Dir, ze.Name);
        var srcs = ze.Paths.Where(p => !VPath.Same(p, target)).ToList();
        if (srcs.Count == 0) Say("⚠ Nothing to zip");
        else SpawnOp(OpKind.Zip, srcs, target, new Dictionary<string, Policy>(StringComparer.OrdinalIgnoreCase));
        Render();
    }

    private void ZipOk(ZipDialog z, string text)
    {
        var name = text.Trim();
        if (Helpers.ValidateName(name) is { } err)
        {
            Say($"⚠ {err}");
            Render();
            return;
        }
        if (!Archive.IsArchiveName(name)) name += ".zip";
        var target = VPath.Join(z.Dir, name);
        if (Archive.IsPlainGz(target) && !(z.Paths.Count == 1 && File.Exists(z.Paths[0])))
        {
            Say("⚠ A .gz holds a single file – use .tar.gz (or .tgz) for folders and several files");
            Render();
            return;
        }
        if (z.Paths.Any(p => VPath.StartsWith(target, p)))
        {
            CloseDialog();
            Say("⚠ The archive can't be created inside a folder being zipped");
            Render();
            return;
        }
        if (File.Exists(target) || Directory.Exists(target))
        {
            dialog = new ZipExistsDialog { Paths = z.Paths, Dir = z.Dir, Name = name };
            ShowDialog();
        }
        else
        {
            CloseDialog();
            SpawnOp(OpKind.Zip, z.Paths, target, new Dictionary<string, Policy>(StringComparer.OrdinalIgnoreCase));
        }
        Render();
    }

    private void ArchiveChoice(ArchiveUpdateDialog au, bool update)
    {
        if (update)
        {
            try
            {
                Archive.Save(au.Zip);
                Say($"Archive {VPath.DisplayName(au.Zip)} updated");
            }
            catch (Exception e)
            {
                Say($"⚠ Could not update {VPath.DisplayName(au.Zip)}: {e.Message}");
                CloseDialog();
                Render();
                return; // the session (and the changes) stay
            }
        }
        else
        {
            Archive.Discard(au.Zip);
            Say($"Changes to {VPath.DisplayName(au.Zip)} discarded");
        }
        CloseDialog();
        ReloadAll();
        if (au.Quit) RequestQuit();
        Render();
    }
}
