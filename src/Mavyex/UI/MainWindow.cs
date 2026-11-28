using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

/// <summary>
/// The application window and controller (port of app.rs App). State lives here and in the Tabs; the WPF elements are
/// refreshed from it by <see cref="Render"/> after every event – the same "state → view" flow as the egui version.
/// </summary>
public sealed partial class MainWindow : Window
{
    public const string TabFormat = "mavyex-tab";
    public const string PathsFormat = "mavyex-paths";
    public const string FilesFormat = PathsFormat; // marks an internal drag (Rust: internal drags pick move/copy by volume)

    public Pane[] Panes { get; } = [new Pane(), new Pane()];
    public int ActivePane { get; private set; }
    public Store Store { get; }
    public List<string> Drives { get; private set; } = Helpers.ListDrives();
    public InlineRename? Inline { get; private set; }
    public HashSet<string>? CutSet => clipboard is { Cut: true } c ? new HashSet<string>(c.Paths, StringComparer.OrdinalIgnoreCase) : null;

    private readonly PaneView[] views = new PaneView[2];
    private readonly List<Job> jobs = [];
    private bool jobsOpen = true;
    private JobsWindow? jobsWindow;
    private (List<string> Paths, bool Cut)? clipboard;
    private string status = "Ready — Ctrl+P opens the command palette";
    private readonly List<(int Pi, string Path)> closedTabs = [];
    private readonly HashSet<string> archSkip = [];
    private readonly List<RemoteEdit> remoteEdits = [];
    private DateTime editPoll = DateTime.Now;
    private bool preview;
    private bool compare;
    private readonly Dictionary<string, Diff>[] diff = [[], []];
    private (string Text, DateTime At) typeahead = ("", DateTime.MinValue);
    private DateTime drivesPoll = DateTime.Now;
    private bool dirty;
    private DateTime lastSave = DateTime.Now;
    private bool quit;
    private readonly int[] seenActive = [-1, -1];
    private PaletteState? palette;
    private Dialog? dialog;
    private TextDiffWindow? textDiff;
    // file system watching
    private readonly Dictionary<string, FileSystemWatcher> watched = [];
    private readonly HashSet<string> watchFailed = [];
    private readonly Dictionary<string, DateTime> fsDirty = [];
    private DateTime? fsLast;
    private readonly object fsLock = new();
    // slow-click rename
    private (int Pi, string Dir, string Path, DateTime At)? renameTimer;
    private int prevActive;

    // UI parts
    private readonly Grid body = new();
    private readonly Grid panesGrid = new() { Margin = new Thickness(6, 6, 6, 2) };
    private readonly PreviewPanel previewPanel = new();
    private readonly GridSplitter splitter = new() { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
    private readonly DockPanel statusBar = new() { Margin = new Thickness(8, 2, 8, 4), Height = 24 };
    private readonly StackPanel statusLeft = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel statusChips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid overlay = new() { Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer renameTicker = new() { Interval = TimeSpan.FromMilliseconds(460) };

    public MainWindow(Store store, string? arg)
    {
        Store = store;
        Style = (Style)Application.Current.FindResource("BaseWindow");
        Title = "mavyex";
        Width = 1360;
        Height = 820;
        MinWidth = 760;
        MinHeight = 420;
        AllowDrop = true;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/mavyex.ico"));
        if (store.Window is { } w && w.OnScreen())
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = w.X;
            Top = w.Y;
            Width = w.W;
            Height = w.H;
            if (w.Maximized) WindowState = WindowState.Maximized;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Sftp.SetAliases(new Dictionary<string, string>(store.Aliases));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) home = @"C:\";
        var downloads = Path.Combine(home, "Downloads");
        var alt = Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (alt.Length == 0) alt = home;
        MakePane(0, home);
        MakePane(1, alt);
        ActivePane = Math.Min(store.ActivePane, 1);

        BuildLayout();
        // `mavyex <folder>` opens the folder in a new tab of the active pane
        if (arg != null && VPath.ParseUserPath(arg, Environment.CurrentDirectory) is { } p && Directory.Exists(p)) OpenNewTab(ActivePane, p);

        PreviewKeyDown += OnPreviewKey;
        PreviewTextInput += OnTextInput;
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { Run(Act.Back); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { Run(Act.Forward); e.Handled = true; }
        };
        Drop += (_, e) => e.Handled = true;
        Closing += OnClosing;
        SourceInitialized += (_, _) => Theme.TitleBar(this);
        ticker.Tick += (_, _) => Tick();
        renameTicker.Tick += (_, _) => RenameTimerFired();
        previewPanel.CloseRequested += () => { preview = false; Render(); };
        Loaded += (_, _) =>
        {
            ticker.Start();
            Render();
            FocusList();
        };
    }

    private void MakePane(int idx, string defaultPath)
    {
        var pane = Panes[idx];
        if (idx < Store.Panes.Count)
        {
            var ps = Store.Panes[idx];
            foreach (var ts in ps.Tabs)
            {
                if (!Sftp.IsRemote(ts.Path) && Directory.Exists(ts.Path))
                {
                    var t = NewTabObj(ts.Path);
                    t.SetSortRaw(ts.Sort, ts.Asc);
                    t.RebuildView();
                    pane.Tabs.Add(t);
                }
            }
            pane.Active = Math.Min(ps.Active, Math.Max(0, pane.Tabs.Count - 1));
        }
        if (pane.Tabs.Count == 0) pane.Tabs.Add(NewTabObj(defaultPath));
    }

    private Tab NewTabObj(string path)
    {
        var t = new Tab(path, Store.ShowHidden) { Search = Store.Search, Exclude = Store.Exclude };
        t.DeepResultsAvailable = () => Dispatcher.BeginInvoke(PollDeepAll);
        t.RebuildView();
        return t;
    }

    private void BuildLayout()
    {
        var root = new Grid();
        var dock = new DockPanel();
        // status bar
        DockPanel.SetDock(statusChips, Dock.Right);
        statusBar.Children.Add(statusChips);
        statusBar.Children.Add(statusLeft);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        dock.Children.Add(statusBar);
        // body: panes | splitter | preview
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        panesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        panesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 2; i++)
        {
            views[i] = new PaneView(this, i);
            Grid.SetColumn(views[i], i * 2);
            panesGrid.Children.Add(views[i]);
            int pi = i;
            views[i].PreviewMouseDown += (_, _) =>
            {
                // clicking anywhere in a pane focuses it (before the click is applied)
                prevActive = ActivePane;
                if (ActivePane != pi)
                {
                    ActivePane = pi;
                    Render();
                }
            };
            WirePaneKeys(views[i]);
        }
        body.Children.Add(panesGrid);
        Grid.SetColumn(splitter, 1);
        body.Children.Add(splitter);
        Grid.SetColumn(previewPanel, 2);
        previewPanel.Margin = new Thickness(0, 6, 6, 2);
        body.Children.Add(previewPanel);
        dock.Children.Add(body);
        root.Children.Add(dock);
        root.Children.Add(overlay);
        Content = root;
    }

    // ------------------------------------------------------------------ helpers

    public Tab CurTab => Panes[ActivePane].Tab;
    private int Other => 1 - ActivePane;
    public void Say(string s) => status = s;

    public Dictionary<string, Diff> DiffFor(int pi) => diff[pi];

    public static string ViewLabel(ViewMode v) => v switch
    {
        ViewMode.Details => "Details",
        ViewMode.Compact => "Compact",
        _ => "Grid (thumbnails)",
    };

    private static string ThemeLabel(ThemeChoice t) => t.ToString();

    public static MenuItem MenuItem(string label, Action click, bool enabled = true)
    {
        var mi = new MenuItem { Header = label, IsEnabled = enabled };
        mi.Click += (_, _) => click();
        return mi;
    }

    public void FocusList()
    {
        // keys must reach the file list: take the focus away from text boxes
        if (Keyboard.FocusedElement is TextBox && dialog == null && palette == null) Keyboard.ClearFocus();
        Focus();
    }

    private void SaveState()
    {
        Store.Panes = Panes.Select(p => new PaneSession
        {
            Active = p.Active,
            Tabs = p.Tabs.Select(t => new TabSession { Path = t.Path, Sort = t.Sort, Asc = t.Asc }).ToList(),
        }).ToList();
        Store.ActivePane = ActivePane;
        Store.Save();
        dirty = false;
        lastSave = DateTime.Now;
    }

    private bool Navigate(int pi, string to)
    {
        var err = Panes[pi].Tab.Go(to);
        if (err != null)
        {
            Say(err);
            return false;
        }
        if (Panes[pi].Tab.LoadingTo == null)
        {
            Store.RecordVisit(to);
            dirty = true;
        }
        return true;
    }

    private void OpenNewTab(int pi, string path)
    {
        var t = NewTabObj(path);
        var pane = Panes[pi];
        if (pane.Tabs.Count > pane.Active)
        {
            var cur = pane.Tab;
            t.SetSortRaw(cur.Sort, cur.Asc);
            t.RebuildView();
        }
        pane.Tabs.Insert(Math.Min(pane.Active + 1, pane.Tabs.Count), t);
        pane.Active = Math.Min(pane.Active + 1, pane.Tabs.Count - 1);
        Store.RecordVisit(path);
        dirty = true;
    }

    private void ReloadAll()
    {
        foreach (var p in Panes) p.Tab.Reload(null);
    }

    // ------------------------------------------------------------------ render

    public void Render()
    {
        if (quit) return;
        for (int i = 0; i < 2; i++) views[i].Render();
        RenderStatus();
        Title = $"mavyex — {CurTab.Path}";
        // Quick Look panel
        if (preview)
        {
            if (body.ColumnDefinitions[2].Width.Value == 0) body.ColumnDefinitions[2].Width = new GridLength(360);
            previewPanel.Visibility = Visibility.Visible;
            splitter.Visibility = Visibility.Visible;
            previewPanel.ShowEntry(CurTab.Current);
        }
        else
        {
            body.ColumnDefinitions[2].Width = new GridLength(0);
            previewPanel.Visibility = Visibility.Collapsed;
            splitter.Visibility = Visibility.Collapsed;
        }
        RenderJobs();
    }

    private string statusKey = "";

    private void RenderStatus()
    {
        var running = jobs.Where(j => j.State == JobState.Running).ToList();
        string key;
        if (running.Count > 0)
        {
            var j = running[^1];
            long total = Interlocked.Read(ref j.Total), done = Interlocked.Read(ref j.Done);
            double frac = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
            key = $"P{frac:0.00}{j.Title}{running.Count}";
            if (key != statusKey)
            {
                statusLeft.Children.Clear();
                var bar = new Grid { Width = 240, Height = 18, VerticalAlignment = VerticalAlignment.Center };
                bar.Children.Add(new Border { Background = Paint.B(Paint.Tint(30)), CornerRadius = new CornerRadius(4) });
                bar.Children.Add(new Border { Background = Paint.B(Paint.Mul(Paint.Accent, 0.8)), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = 240 * frac });
                var t = new TextBlock { Text = j.Title + (running.Count > 1 ? $"  (+{running.Count - 1} more)" : ""), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                bar.Children.Add(t);
                statusLeft.Children.Add(bar);
                var details = new Chip("Details", tip: "Show / hide the operations window") { Margin = new Thickness(6, 0, 0, 0) };
                details.Click += () => { jobsOpen = !jobsOpen; Render(); };
                statusLeft.Children.Add(details);
            }
        }
        else
        {
            key = "S" + status;
            if (key != statusKey)
            {
                statusLeft.Children.Clear();
                var t = new TextBlock { Text = status, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                statusLeft.Children.Add(t);
            }
        }
        key += Store.Theme + Theme.IsDark.ToString();
        if (key == statusKey) return;
        statusKey = key;
        statusChips.Children.Clear();
        foreach (var (label, a) in new[]
        {
            ("F2 Rename", Act.Rename), ("F4 Edit", Act.EditFile), ("F5 Copy", Act.Copy), ("F6 Move", Act.Move),
            ("F7 New folder", Act.NewFolder), ("F8 Delete", Act.Delete), ($"Theme: {ThemeLabel(Store.Theme)}", Act.ThemeCycle), ("Ctrl+P Palette", Act.Palette),
        })
        {
            var c = new Chip(label);
            var act = a;
            c.Click += () => Run(act);
            statusChips.Children.Add(c);
        }
    }

    private void RenderJobs()
    {
        if (jobs.Count == 0 || !jobsOpen)
        {
            if (jobsWindow != null)
            {
                jobsWindow.AllowClose = true;
                jobsWindow.Close();
                jobsWindow = null;
            }
            return;
        }
        if (jobsWindow == null)
        {
            jobsWindow = new JobsWindow(this, Store.JobsWin);
            jobsWindow.Closed += (_, _) =>
            {
                if (jobsWindow != null && !jobsWindow.AllowClose) jobsOpen = false;
                jobsWindow = null;
            };
            jobsWindow.GeometryChanged += g =>
            {
                Store.JobsWin = g;
                dirty = true;
            };
            jobsWindow.Show();
            Activate(); // do not steal focus from the file manager
        }
        jobsWindow.Update(jobs, id =>
        {
            jobs.RemoveAll(j => j.Id == id);
            Render();
        });
    }

    // ------------------------------------------------------------------ tick (app.rs tick)

    private void Tick()
    {
        if (quit) return;
        bool changed = false;
        // removable drives come and go: re-read the drive list every 1.5 s
        if ((DateTime.Now - drivesPoll).TotalMilliseconds >= 1500)
        {
            drivesPoll = DateTime.Now;
            var now = Helpers.ListDrives();
            if (!now.SequenceEqual(Drives))
            {
                Drives = now;
                changed = true;
            }
        }
        changed |= PollJobs();
        changed |= CheckArchives();
        changed |= PollEdits();
        SyncWatches();
        changed |= ApplyFsEvents();
        if (compare)
        {
            var d0 = Helpers.DiffMap(Panes[0].Tab, Panes[1].Tab);
            var d1 = Helpers.DiffMap(Panes[1].Tab, Panes[0].Tab);
            if (!SameMap(d0, diff[0]) || !SameMap(d1, diff[1]))
            {
                diff[0] = d0;
                diff[1] = d1;
                changed = true;
            }
        }
        else if (diff[0].Count > 0 || diff[1].Count > 0)
        {
            diff[0] = [];
            diff[1] = [];
            changed = true;
        }
        // pending slow navigations
        foreach (var p in Panes)
        {
            foreach (var t in p.Tabs)
            {
                if (t.LoadingTo == null) continue;
                var lp = t.PollLoad();
                if (lp.Pending)
                {
                    changed = true; // the footer shows the elapsed time
                    continue;
                }
                if (lp.Arrived is { } a)
                {
                    Store.RecordVisit(a);
                    dirty = true;
                    changed = true;
                }
                if (lp.Err is { } e)
                {
                    Say(e);
                    changed = true;
                }
            }
        }
        changed |= PollDeepCore();
        // folders we could not watch (network shares, archives): slow mtime poll
        foreach (var p in Panes)
        {
            var t = p.Tab;
            if (watched.ContainsKey(Helpers.PathKey(t.Path))) continue;
            if ((DateTime.Now - t.LastPoll).TotalMilliseconds > 2000)
            {
                t.LastPoll = DateTime.Now;
                if (!Sftp.IsRemote(t.Path) && Fs.DirMtime(t.Path) != t.Mtime)
                {
                    t.Reload(null);
                    changed = true;
                }
            }
        }
        // a tab that just became active may be stale (only active tabs are watched)
        for (int i = 0; i < 2; i++)
        {
            if (seenActive[i] != -1 && seenActive[i] != Panes[i].Active)
            {
                Panes[i].Tab.Reload(null);
                changed = true;
            }
            seenActive[i] = Panes[i].Active;
        }
        if (Inline is { } ir && !VPath.Same(Panes[ir.Pi].Tab.Path, ir.Dir))
        {
            Inline = null; // navigated away while editing
            changed = true;
        }
        SaveWindowGeom();
        if (dirty && (DateTime.Now - lastSave).TotalSeconds >= 1) SaveState();
        if (changed) Render();
        else if (jobs.Count > 0) RenderJobs();
    }

    private static bool SameMap(Dictionary<string, Diff> a, Dictionary<string, Diff> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private void PollDeepAll()
    {
        if (PollDeepCore()) Render();
    }

    private bool PollDeepCore()
    {
        bool any = false;
        foreach (var p in Panes)
        {
            foreach (var t in p.Tabs)
            {
                if (t.DeepJob == null) continue;
                int before = t.View.Count;
                var (r0, tr0) = (t.DeepJob.Running, t.DeepJob.Truncated);
                t.PollDeep();
                if (t.View.Count != before || t.DeepJob == null || t.DeepJob.Running != r0 || t.DeepJob.Truncated != tr0) any = true;
            }
        }
        return any;
    }

    private void SaveWindowGeom()
    {
        if (WindowState == WindowState.Minimized || !IsLoaded) return;
        var prev = Store.Window;
        WinGeom next = WindowState == WindowState.Maximized
            ? new WinGeom { X = RestoreBounds.Left, Y = RestoreBounds.Top, W = RestoreBounds.Width, H = RestoreBounds.Height, Maximized = true }
            : new WinGeom { X = Left, Y = Top, W = Width, H = Height, Maximized = false };
        if (double.IsInfinity(next.X) || double.IsNaN(next.X)) return;
        if (prev == null || prev.X != next.X || prev.Y != next.Y || prev.W != next.W || prev.H != next.H || prev.Maximized != next.Maximized)
        {
            Store.Window = next;
            dirty = true;
        }
    }

    // ------------------------------------------------------------------ jobs

    private void StartOp(OpKind kind, List<string> srcs, string? dest)
    {
        if (srcs.Count == 0) return;
        if (kind is OpKind.Copy or OpKind.Move && dest != null)
        {
            bool virtualDest = Sftp.IsRemote(dest) || Archive.InArchive(dest);
            // remote / archive folders are listed once; local ones are probed name by name
            List<Entry> existing = [];
            if (virtualDest)
            {
                try { existing = Fs.ReadDir(dest); } catch { }
            }
            var queue = srcs.Where(s =>
            {
                var n = VPath.FileName(s);
                if (n == null) return false;
                if (kind == OpKind.Move && VPath.Parent(s) is { } par && VPath.Same(par, dest)) return false;
                if (virtualDest) return existing.Any(e => !e.IsUp && string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase));
                var t = Path.Combine(dest, n);
                return (File.Exists(t) || Directory.Exists(t)) && !VPath.Same(t, s);
            }).ToList();
            if (queue.Count > 0)
            {
                dialog = new ConflictDialog { Kind = kind, Srcs = srcs, Dest = dest, Queue = queue, Existing = existing };
                ShowDialog();
                return;
            }
        }
        SpawnOp(kind, srcs, dest, new Dictionary<string, Policy>(StringComparer.OrdinalIgnoreCase));
    }

    private void SpawnOp(OpKind kind, List<string> srcs, string? dest, Dictionary<string, Policy> policies)
    {
        var job = Ops.SpawnWith(kind, srcs, dest, policies);
        job.Completed += _ => Dispatcher.BeginInvoke(() => { PollJobs(); Render(); });
        jobs.Add(job);
        jobsOpen = true; // show the operations window
    }

    private readonly HashSet<long> reported = [];

    private bool PollJobs()
    {
        bool changed = false;
        (OpKind Kind, JobState State, string? Err)? last = null;
        foreach (var j in jobs)
        {
            if (j.State != JobState.Running && reported.Add(j.Id))
            {
                last = (j.Kind, j.State, j.Error);
            }
        }
        if (last is { } l)
        {
            foreach (var p in Panes) p.Tab.Selected.Clear();
            ReloadAll();
            Say(l.State switch
            {
                JobState.Done => l.Kind switch
                {
                    OpKind.Copy => "Copied.",
                    OpKind.Move => "Moved.",
                    OpKind.Trash => "Moved to Recycle Bin.",
                    OpKind.Delete => "Deleted.",
                    _ => "Zip created.",
                },
                JobState.Cancelled => "Cancelled.",
                _ => $"⚠ {l.Err}",
            });
            changed = true;
        }
        // successful / cancelled entries disappear after a few seconds; failures stay until dismissed
        int n = jobs.RemoveAll(j => j.State is JobState.Done or JobState.Cancelled && j.Finished is { } f && (DateTime.Now - f).TotalSeconds > 3);
        if (n > 0) changed = true;
        if (jobs.Any(j => j.State == JobState.Running)) changed = true; // progress bar
        return changed;
    }

    public void CancelJob(Job j)
    {
        j.Cancel();
        Render();
    }

    /// <summary>`mavyex &lt;path&gt;` from another terminal: show the folder as a tab in this window.</summary>
    public void HandleExternalOpen(string? p)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (p == null) return;
        string dir;
        string? focus = null;
        if (Directory.Exists(p)) dir = p;
        else if (File.Exists(p) && VPath.Parent(p) is { } par && VPath.FileName(p) is { } n)
        {
            dir = par;
            focus = n;
        }
        else
        {
            Say($"⚠ Path not found: {p}");
            Render();
            return;
        }
        // already open somewhere? just switch to it
        bool found = false;
        for (int pi = 0; pi < 2 && !found; pi++)
        {
            int ti = Panes[pi].Tabs.FindIndex(t => VPath.Same(t.Path, dir));
            if (ti >= 0)
            {
                Panes[pi].Active = ti;
                ActivePane = pi;
                found = true;
            }
        }
        if (!found) OpenNewTab(ActivePane, dir);
        if (focus != null) CurTab.Reload(focus);
        dirty = true;
        Render();
    }

    // ------------------------------------------------------------------ fs watching

    /// <summary>Keeps exactly the folders of the two active tabs watched.</summary>
    private void SyncWatches()
    {
        var want = new Dictionary<string, string>();
        foreach (var p in Panes) want[Helpers.PathKey(p.Tab.Path)] = p.Tab.Path;
        foreach (var k in watched.Keys.Where(k => !want.ContainsKey(k)).ToList())
        {
            watched[k].Dispose();
            watched.Remove(k);
        }
        watchFailed.RemoveWhere(k => !want.ContainsKey(k));
        foreach (var (k, path) in want)
        {
            if (watched.ContainsKey(k) || watchFailed.Contains(k)) continue;
            if (Sftp.IsRemote(path) || Archive.SplitInner(path) != null || Archive.Split(path) != null || !Directory.Exists(path))
            {
                watchFailed.Add(k);
                continue;
            }
            try
            {
                var w = new FileSystemWatcher(path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes };
                void Mark(string? full)
                {
                    lock (fsLock)
                    {
                        var now = DateTime.Now;
                        if (full != null)
                        {
                            fsDirty.TryAdd(Helpers.PathKey(full), now);
                            if (VPath.Parent(full) is { } par) fsDirty.TryAdd(Helpers.PathKey(par), now);
                        }
                        fsLast = now;
                    }
                }
                w.Changed += (_, e) => Mark(e.FullPath);
                w.Created += (_, e) => Mark(e.FullPath);
                w.Deleted += (_, e) => Mark(e.FullPath);
                w.Renamed += (_, e) => Mark(e.FullPath);
                w.Error += (_, _) => Mark(path + "\\x");
                w.EnableRaisingEvents = true;
                watched[k] = w;
            }
            catch
            {
                watchFailed.Add(k);
            }
        }
    }

    /// <summary>Debounced reload of folders that changed outside of mavyex: waits until the burst of events calms
    /// down (200 ms), but never longer than 1 s.</summary>
    private bool ApplyFsEvents()
    {
        Dictionary<string, DateTime> dirtyNow;
        lock (fsLock)
        {
            if (fsDirty.Count == 0) return false;
            bool calm = fsLast is { } t && (DateTime.Now - t).TotalMilliseconds >= 200;
            bool overdue = fsDirty.Values.Any(t0 => (DateTime.Now - t0).TotalMilliseconds >= 1000);
            if (!(calm || overdue)) return false;
            dirtyNow = new Dictionary<string, DateTime>(fsDirty);
            fsDirty.Clear();
            fsLast = null;
        }
        bool any = false;
        foreach (var p in Panes)
        {
            if (dirtyNow.ContainsKey(Helpers.PathKey(p.Tab.Path)))
            {
                p.Tab.Reload(null);
                any = true;
            }
        }
        return any;
    }

    // ------------------------------------------------------------------ quit

    private void RequestQuit()
    {
        if (Archive.Sessions().FirstOrDefault(Archive.IsDirty) is { } zip)
        {
            dialog = new ArchiveUpdateDialog { Zip = zip, Quit = true };
            ShowDialog();
            return;
        }
        quit = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!quit && Archive.Sessions().Any(Archive.IsDirty))
        {
            e.Cancel = true;
            RequestQuit();
            return;
        }
        quit = true;
        ticker.Stop();
        foreach (var z in Archive.Sessions()) Archive.Discard(z); // unsaved changes were declined / confirmed earlier
        SaveWindowGeom();
        SaveState();
        foreach (var w in watched.Values) w.Dispose();
        Sftp.DisconnectAll();
        if (jobsWindow != null)
        {
            jobsWindow.AllowClose = true;
            jobsWindow.Close();
        }
        textDiff?.Close();
    }
}
