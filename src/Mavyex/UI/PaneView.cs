using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

/// <summary>
/// One pane (app.rs ui_pane): tab strip, drives + favorite, nav buttons + path bar (breadcrumbs / editor with
/// suggestions), search bar, column header, file list (details / compact / grid), footer.
/// Persistent WPF elements are updated from the state in <see cref="Render"/> (called by MainWindow after every change).
/// </summary>
public sealed class PaneView : Border
{
    private readonly MainWindow app;
    public int Pi { get; }
    public Pane Pane => app.Panes[Pi];
    public Tab Tab => Pane.Tab;

    public readonly HashSet<FrameworkElement> Realized = [];
    public RowContext Ctx { get; }

    // tab strip
    private readonly ScrollViewer tabScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly StackPanel tabStrip = new() { Orientation = Orientation.Horizontal };
    private readonly List<TabPill> pills = [];
    private (int, int) seenTabs = (-1, 0);
    private string tabsKey = "";
    // drives row
    private readonly WrapPanel drivesRow = new() { Margin = new Thickness(0, 2, 0, 2) };
    private string drivesKey = "";
    // nav + path bar
    private readonly IconBtn backBtn = new(Paint.Ico.Back, "Back (Alt+←)"), fwdBtn = new(Paint.Ico.Fwd, "Forward (Alt+→)"),
        upBtn = new(Paint.Ico.Up, "Up (Backspace)"), rootBtn = new(Paint.Ico.Root, "Root of drive (Ctrl+Backspace)"),
        refreshBtn = new(Paint.Ico.Refresh, "Refresh (Ctrl+R)");
    private readonly Border pathBar = new() { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 1), MinHeight = 26 };
    private readonly DockPanel crumbs = new() { LastChildFill = true };
    private string crumbsKey = "";
    public readonly TextBox PathBox = new() { Padding = new Thickness(0), FontSize = 14, Background = Brushes.Transparent };
    private readonly Popup suggPopup = new() { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Bottom };
    private readonly StackPanel suggList = new();
    // search bar
    private readonly DockPanel searchBar = new() { Margin = new Thickness(0, 3, 0, 3) };
    public readonly TextBox FilterBox = new() { Height = 24, Padding = new Thickness(6, 0, 6, 0) };
    private readonly Border filterBorder = new() { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.5) };
    private readonly Chip[] modeChips;
    private readonly Chip caseChip = new("Aa", tip: "Match case (Alt+C)");
    private readonly Chip subChip = new("↳", tip: "Subfolders (Alt+S)\nSearch inside subfolders too.\nResults show the path relative to this folder.");
    private readonly Chip contentChip = new("¶", tip: "Content (Alt+T)\nSearch inside the files, in all subfolders.\nShows the first matching line. Binary files and files over 8 MB are skipped.\nCombine with :ext=rs, :type=code, :size<1mb, :age<7d");
    public readonly TextBox ExcludeBox = new() { Width = 160, Height = 24, Padding = new Thickness(6, 0, 6, 0), Tag = "exclude: node_modules, .git, *.tmp",
        ToolTip = "Names or wildcards to skip while searching subfolders,\nseparated by comma. Nothing is excluded by default." };
    private readonly TextBlock countText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
    private readonly Chip closeSearch = new("✕", tip: "Close search (Esc)");
    private bool syncingSearch;
    // header + list
    private readonly Grid header = new() { Height = Tab.RowH };
    private readonly ListBox list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Focusable = false };
    private readonly Canvas overlay = new() { ClipToBounds = true };
    public readonly TextBox InlineBox = new() { Padding = new Thickness(3, 0, 3, 0), FontSize = 14, Visibility = Visibility.Collapsed };
    private readonly TextBlock emptyText = new() { Margin = new Thickness(6, 4, 0, 0), IsHitTestVisible = false };
    private object? itemsFor;
    private ViewMode itemsMode;
    private int itemsCols;
    // footer
    private readonly TextBlock footer = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock viewBtn = new() { Cursor = Cursors.Hand, FontSize = 14, Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };

    // mouse state
    private Point? pressAt;
    private int pressVi = -1;
    private bool pressDouble;

    public PaneView(MainWindow app, int pi)
    {
        this.app = app;
        Pi = pi;
        Ctx = new RowContext { Pane = this };
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(4);
        AllowDrop = true;
        modeChips = SearchModes.All.Select(m => new Chip(m.Icon(), tip: m.Tooltip())).ToArray();

        var root = new DockPanel();
        // --- tab strip
        var tabRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var allBtn = new IconBtn(Paint.Ico.Down, "All tabs");
        var plusBtn = new IconBtn(Paint.Ico.Plus, "New tab (Ctrl+T)");
        DockPanel.SetDock(allBtn, Dock.Right);
        DockPanel.SetDock(plusBtn, Dock.Right);
        tabRow.Children.Add(allBtn);
        tabRow.Children.Add(plusBtn);
        tabScroll.Content = tabStrip;
        tabScroll.PreviewMouseWheel += (_, e) =>
        {
            tabScroll.ScrollToHorizontalOffset(tabScroll.HorizontalOffset - e.Delta / 2.0); // the wheel scrolls the strip sideways
            e.Handled = true;
        };
        tabRow.Children.Add(tabScroll);
        plusBtn.Click += () => app.NewTabIn(Pi);
        allBtn.Click += () => ShowAllTabsMenu(allBtn);
        tabStrip.AllowDrop = true;
        tabStrip.DragOver += TabStripDragOver;
        tabStrip.Drop += TabStripDrop;
        tabStrip.DragLeave += (_, _) => app.TabInsertMark(Pi, null);
        DockPanel.SetDock(tabRow, Dock.Top);
        root.Children.Add(tabRow);

        // --- drives + favorite
        DockPanel.SetDock(drivesRow, Dock.Top);
        root.Children.Add(drivesRow);

        // --- nav + path bar
        var nav = new DockPanel { Margin = new Thickness(0, 1, 0, 3) };
        foreach (var b in new[] { backBtn, fwdBtn, upBtn, rootBtn, refreshBtn })
        {
            DockPanel.SetDock(b, Dock.Left);
            b.Margin = new Thickness(0, 0, 1, 0);
            nav.Children.Add(b);
        }
        refreshBtn.Margin = new Thickness(0, 0, 4, 0);
        backBtn.Click += () => app.Run(Act.Back, Pi);
        fwdBtn.Click += () => app.Run(Act.Forward, Pi);
        upBtn.Click += () => app.Run(Act.Up, Pi);
        rootBtn.Click += () => app.Run(Act.Root, Pi);
        refreshBtn.Click += () => app.ReloadPane(Pi);
        pathBar.SetResourceReference(BackgroundProperty, "InputBg");
        pathBar.SetResourceReference(BorderBrushProperty, "Line");
        pathBar.Child = crumbs;
        PathBox.Style = null;
        PathBox.BorderThickness = new Thickness(0);
        PathBox.SetResourceReference(Control.ForegroundProperty, "Fg");
        PathBox.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        PathBox.VerticalContentAlignment = VerticalAlignment.Center;
        PathBox.TextChanged += (_, _) => app.PathEditChanged(Pi, PathBox.Text);
        PathBox.LostKeyboardFocus += (_, e) => app.PathEditLostFocus(Pi, e.NewFocus as DependencyObject);
        suggPopup.PlacementTarget = pathBar;
        suggPopup.Child = new Border { Child = new ScrollViewer { Content = suggList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 380 } };
        nav.Children.Add(pathBar);
        DockPanel.SetDock(nav, Dock.Top);
        root.Children.Add(nav);

        // --- search bar
        var rightPart = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (c, m) in modeChips.Zip(SearchModes.All))
        {
            var mode = m;
            c.Click += () => app.SetSearch(Tab.Search with { Mode = mode });
            rightPart.Children.Add(c);
        }
        caseChip.Click += () => app.SetSearch(Tab.Search with { MatchCase = !Tab.Search.MatchCase });
        subChip.Click += ToggleSub;
        contentChip.Click += ToggleContent;
        rightPart.Children.Add(caseChip);
        rightPart.Children.Add(subChip);
        rightPart.Children.Add(contentChip);
        ExcludeBox.Margin = new Thickness(0, 0, 4, 0);
        ExcludeBox.TextChanged += (_, _) =>
        {
            if (syncingSearch || ExcludeBox.Text == Tab.Exclude) return;
            Tab.Exclude = ExcludeBox.Text;
            Tab.RebuildView();
            app.SetExclude(Tab.Exclude);
        };
        rightPart.Children.Add(ExcludeBox);
        rightPart.Children.Add(countText);
        rightPart.Children.Add(closeSearch);
        closeSearch.Click += () => app.CloseSearch(Pi);
        DockPanel.SetDock(rightPart, Dock.Right);
        searchBar.Children.Add(rightPart);
        filterBorder.Child = FilterBox;
        filterBorder.Margin = new Thickness(0, 0, 4, 0);
        FilterBox.TextChanged += (_, _) =>
        {
            if (syncingSearch || FilterBox.Text == Tab.Filter) return;
            Tab.Filter = FilterBox.Text;
            Tab.RebuildView();
            Tab.SetCursor(0);
            app.Render();
            ScrollToTop();
        };
        searchBar.Children.Add(filterBorder);
        DockPanel.SetDock(searchBar, Dock.Top);
        root.Children.Add(searchBar);

        // --- column header
        header.MouseRightButtonUp += (_, e) => { app.ShowViewMenu(header); e.Handled = true; };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // --- footer
        var foot = new DockPanel { Height = 22 };
        DockPanel.SetDock(viewBtn, Dock.Right);
        foot.Children.Add(viewBtn);
        foot.Children.Add(footer);
        viewBtn.MouseLeftButtonUp += (_, e) => { app.Run(Act.ViewCycle, Pi); e.Handled = true; };
        viewBtn.MouseRightButtonUp += (_, e) => { app.ShowViewMenu(viewBtn); e.Handled = true; };
        footer.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");
        viewBtn.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);

        // --- list
        var listHost = new Grid();
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        list.ItemContainerStyle = ContainerStyle();
        list.PreviewMouseLeftButtonDown += ListMouseDown;
        list.PreviewMouseMove += ListMouseMove;
        list.PreviewMouseLeftButtonUp += ListMouseUp;
        list.PreviewMouseDown += ListOtherDown;
        list.PreviewMouseRightButtonUp += ListRightUp;
        listHost.Children.Add(list);
        emptyText.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");
        listHost.Children.Add(emptyText);
        overlay.Children.Add(InlineBox);
        listHost.Children.Add(overlay);
        InlineBox.LostKeyboardFocus += (_, _) => app.InlineCommitOnBlur(Pi);
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => PositionInline()));
        root.Children.Add(listHost);

        Child = root;
        DragOver += OnDragOver;
        Drop += OnDrop;
        DragLeave += (_, _) => SetDropTarget(null);
        SizeChanged += (_, _) => { if (app.Store.View == ViewMode.Grid) app.Render(); };
    }

    private static Style ContainerStyle()
    {
        var s = new Style(typeof(ListBoxItem));
        s.Setters.Add(new Setter(FocusableProperty, false));
        s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        var t = new ControlTemplate(typeof(ListBoxItem));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        s.Setters.Add(new Setter(Control.TemplateProperty, t));
        return s;
    }

    private DataTemplate RowTemplate(bool grid)
    {
        var f = new FrameworkElementFactory(grid ? typeof(GridRow) : typeof(FileRow));
        f.SetValue(grid ? GridRow.TagProperty : FileRow.TagProperty, Ctx);
        var dt = new DataTemplate { VisualTree = f };
        return dt;
    }

    // ------------------------------------------------------------------ render

    public void Render()
    {
        var tab = Tab;
        bool active = app.ActivePane == Pi;
        BorderThickness = new Thickness(active ? 1.5 : 1);
        BorderBrush = active ? Paint.B(Paint.Accent) : Paint.B(Paint.Line);
        RenderTabs();
        RenderDrives();
        backBtn.IsEnabled = tab.Back.Count > 0;
        fwdBtn.IsEnabled = tab.Fwd.Count > 0;
        upBtn.IsEnabled = rootBtn.IsEnabled = VPath.Parent(tab.Path) != null;
        RenderPathBar();
        RenderSearch();
        RenderHeader();
        RenderList();
        RenderFooter();
    }

    private void RenderTabs()
    {
        var pane = Pane;
        int n = pane.Tabs.Count;
        var key = string.Join("|", pane.Tabs.Select(t => t.Title + "\u0001" + t.Path)) + "#" + pane.Active + Theme.IsDark;
        if (key != tabsKey)
        {
            tabsKey = key;
            tabStrip.Children.Clear();
            pills.Clear();
            for (int i = 0; i < n; i++)
            {
                var t = pane.Tabs[i];
                var label = t.Title;
                if (label.Length > 20) label = label[..19] + "…";
                var pill = new TabPill(label, i == pane.Active, n > 1, t.Path);
                int idx = i;
                pill.Click += () => app.SelectTab(Pi, idx);
                pill.CloseClick += () => app.CloseTabAt(Pi, idx);
                pill.MiddleClick += () => app.CloseTabAt(Pi, idx);
                pill.DragOut += () => app.StartTabDrag(Pi, idx, pill);
                pill.MouseRightButtonUp += (_, e) => { ShowTabMenu(pill, idx); e.Handled = true; };
                pills.Add(pill);
                tabStrip.Children.Add(pill);
            }
        }
        if (seenTabs != (pane.Active, n))
        {
            seenTabs = (pane.Active, n);
            if (pane.Active < pills.Count)
            {
                var p = pills[pane.Active];
                Dispatcher.BeginInvoke(() => p.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }

    public List<Rect> TabRects() =>
        pills.Select(p => p.IsLoaded ? new Rect(p.TranslatePoint(new Point(0, 0), tabStrip), p.RenderSize) : Rect.Empty).ToList();

    private void ShowTabMenu(FrameworkElement target, int i)
    {
        int n = Pane.Tabs.Count;
        var m = new ContextMenu();
        void Item(string label, bool enabled, TabCmd cmd) =>
            m.Items.Add(MainWindow.MenuItem(label, () => app.TabMenu(Pi, i, cmd), enabled));
        Item("Close", n > 1, TabCmd.Close);
        Item("Close other tabs", n > 1, TabCmd.Others);
        Item("Close tabs to the left", i > 0, TabCmd.Left);
        Item("Close tabs to the right", i + 1 < n, TabCmd.Right);
        m.Items.Add(new Separator());
        Item("Duplicate tab", true, TabCmd.Duplicate);
        Item("Open in other pane", true, TabCmd.OpenOther);
        Item("Move to other pane", n > 1, TabCmd.MoveOther);
        m.Items.Add(new Separator());
        Item("Copy path", true, TabCmd.CopyPath);
        m.PlacementTarget = target;
        m.IsOpen = true;
    }

    private void ShowAllTabsMenu(FrameworkElement target)
    {
        var m = new ContextMenu { MaxHeight = 360 };
        for (int i = 0; i < Pane.Tabs.Count; i++)
        {
            var t = Pane.Tabs[i];
            int idx = i;
            var mi = MainWindow.MenuItem($"{t.Title}   {t.Path}", () => app.SelectTab(Pi, idx));
            if (i == Pane.Active) mi.FontWeight = FontWeights.SemiBold;
            m.Items.Add(mi);
        }
        m.PlacementTarget = target;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    private void RenderDrives()
    {
        var tab = Tab;
        bool fav = app.Store.IsFavorite(tab.Path);
        var key = string.Join("|", app.Drives) + "#" + tab.Path + fav + Theme.IsDark;
        if (key == drivesKey) return;
        drivesKey = key;
        drivesRow.Children.Clear();
        foreach (var d in app.Drives)
        {
            var chip = new Chip(d.TrimEnd('\\'), VPath.StartsWith(tab.Path, d));
            var drive = d;
            chip.Click += () => app.Go(Pi, drive);
            drivesRow.Children.Add(chip);
        }
        var star = new Chip(fav ? "★" : "☆", false, fav ? Color.FromRgb(240, 180, 0) : null, "Favorite this folder (Ctrl+B) · jump with Ctrl+D");
        star.Click += () => app.ToggleFavoriteOf(Pi);
        drivesRow.Children.Add(star);
    }

    // --- path bar

    private void RenderPathBar()
    {
        var pe = Pane.PathEdit;
        if (pe != null)
        {
            if (pathBar.Child != PathBox)
            {
                pathBar.Child = PathBox;
                crumbsKey = "";
            }
            if (PathBox.Text != pe.Text) PathBox.Text = pe.Text;
            if (pe.Fresh)
            {
                pe.Fresh = false;
                Dispatcher.BeginInvoke(() =>
                {
                    PathBox.Focus();
                    if (pe.CaretAtEnd) PathBox.Select(PathBox.Text.Length, 0);
                    else PathBox.SelectAll();
                    pe.CaretAtEnd = false;
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
            RenderSuggestions(pe);
            return;
        }
        suggPopup.IsOpen = false;
        if (pathBar.Child != crumbs) pathBar.Child = crumbs;
        var key = Tab.Path + "#" + pathBar.ActualWidth + Theme.IsDark;
        if (key == crumbsKey) return;
        crumbsKey = key;
        BuildCrumbs();
    }

    private void BuildCrumbs()
    {
        crumbs.Children.Clear();
        var path = Tab.Path;
        bool remote = Sftp.IsRemote(path);
        var list = new List<(string Label, string Target)>();
        string acc = "";
        foreach (var c in VPath.Components(path))
        {
            acc = acc.Length == 0 ? c : VPath.Join(acc, c);
            var label = c.TrimEnd('\\');
            if (remote && list.Count == 1 && Sftp.CrumbLabel(path) is { } l) label = l; // the server segment shows its alias
            var target = acc.Length == 2 && acc[1] == ':' ? acc + "\\" : acc;
            list.Add((label, target));
        }
        // keep the tail that fits
        double avail = Math.Max(0, pathBar.ActualWidth - 52);
        double used = 0;
        int start = list.Count;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            double w = list[i].Label.Length * 7.5 + 20;
            if (used + w > avail && start < list.Count) break;
            used += w;
            start = i;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        TextBlock Weak(string s)
        {
            var t = new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");
            return t;
        }
        if (start > 0) row.Children.Add(Weak("…"));
        for (int i = start; i < list.Count; i++)
        {
            if (i > start) row.Children.Add(Weak("›"));
            var (label, target) = list[i];
            var b = new Button { Content = label, Padding = new Thickness(5, 1, 5, 1), Background = Brushes.Transparent, Focusable = false };
            b.Click += (_, _) => app.Go(Pi, target);
            row.Children.Add(b);
        }
        DockPanel.SetDock(row, Dock.Left);
        crumbs.Children.Add(row);
        var rest = new Border { Background = Brushes.Transparent, MinWidth = 20, Cursor = Cursors.IBeam, ToolTip = "Click to edit path (Ctrl+L)" };
        rest.MouseLeftButtonUp += (_, _) => app.Run(Act.EditPath, Pi);
        crumbs.Children.Add(rest);
    }

    private void RenderSuggestions(PathEdit pe)
    {
        suggList.Children.Clear();
        if (pe.Sugg.Count == 0)
        {
            suggPopup.IsOpen = false;
            return;
        }
        var border = (Border)suggPopup.Child;
        border.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        border.BorderThickness = new Thickness(1);
        border.CornerRadius = new CornerRadius(8);
        border.Padding = new Thickness(4);
        border.Width = Math.Clamp(pathBar.ActualWidth, 260, 560);
        for (int i = 0; i < pe.Sugg.Count; i++)
        {
            var sg = pe.Sugg[i];
            int idx = i;
            var rowB = new Border { Height = 24, CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand };
            bool sel = pe.Sel == i;
            rowB.Background = sel ? Paint.B(Paint.Mul(Paint.Accent, Paint.Dark ? 0.5 : 0.3)) : Brushes.Transparent;
            rowB.MouseEnter += (_, _) => { if (!sel) rowB.Background = Paint.B(Paint.Tint(14)); };
            rowB.MouseLeave += (_, _) => { if (!sel) rowB.Background = Brushes.Transparent; };
            var g = new DockPanel { LastChildFill = true };
            bool removable = sg.Tag.Length > 0, canStar = sg.Tag == "recent";
            if (removable)
            {
                var x = SmallBtn("✕", sg.Tag == "recent" ? "Remove from recent folders" : "Remove from favorites", () => app.ForgetSuggestion(Pi, sg));
                DockPanel.SetDock(x, Dock.Right);
                g.Children.Add(x);
            }
            if (canStar)
            {
                var s = SmallBtn("☆", "Add to favorites", () => app.StarSuggestion(Pi, sg));
                DockPanel.SetDock(s, Dock.Right);
                g.Children.Add(s);
            }
            var tag = new TextBlock { Text = sg.Tag.Length == 0 ? "folder" : sg.Tag, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            tag.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");
            DockPanel.SetDock(tag, Dock.Right);
            g.Children.Add(tag);
            var label = new TextBlock
            {
                Text = sg.Tag.Length == 0 ? VPath.DisplayName(sg.Path) : (Sftp.Pretty(sg.Path) ?? sg.Path),
                FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            g.Children.Add(label);
            rowB.Child = g;
            rowB.MouseLeftButtonUp += (_, e) => { app.SuggestionClicked(Pi, idx); e.Handled = true; };
            suggList.Children.Add(rowB);
        }
        suggPopup.IsOpen = true;
    }

    private FrameworkElement SmallBtn(string glyph, string tip, Action click)
    {
        var t = new TextBlock { Text = glyph, Width = 18, Height = 18, TextAlignment = TextAlignment.Center, FontSize = 12, ToolTip = tip, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");
        var b = new Border { Child = t, CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        b.MouseEnter += (_, _) => b.Background = Paint.B(Paint.Tint(34));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return b;
    }

    public bool IsMouseOverSuggestions() => suggPopup.IsOpen && suggPopup.Child.IsMouseOver;

    public bool IsInSuggestions(DependencyObject? d)
    {
        while (d != null)
        {
            if (d == suggPopup.Child) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // --- search bar

    private void ToggleSub()
    {
        var o = Tab.Search;
        bool on = o.Deep || o.Content;
        app.SetSearch(o with { Deep = !on, Content = false });
    }

    private void ToggleContent()
    {
        var o = Tab.Search;
        app.SetSearch(o with { Content = !o.Content, Deep = o.Deep || !o.Content });
    }

    public void KeyToggleSub() => ToggleSub();
    public void KeyToggleContent() => ToggleContent();

    private void RenderSearch()
    {
        var tab = Tab;
        searchBar.Visibility = tab.FilterOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!tab.FilterOpen) return;
        var o = tab.Search;
        syncingSearch = true;
        if (FilterBox.Text != tab.Filter) FilterBox.Text = tab.Filter;
        if (ExcludeBox.Text != tab.Exclude) ExcludeBox.Text = tab.Exclude;
        syncingSearch = false;
        FilterBox.Tag = o.Content ? "text to find inside files  ·  re:regex  ·  :ext=rs  :size<1mb  :age<7d" : o.Mode.Hint();
        var err = tab.Matcher.Error;
        filterBorder.BorderBrush = err != null ? Paint.B(Paint.ErrorFg) : Brushes.Transparent;
        foreach (var (c, m) in modeChips.Zip(SearchModes.All)) c.Set(m.Icon(), o.Mode == m);
        caseChip.Set("Aa", o.MatchCase);
        bool subOn = o.Deep || o.Content;
        subChip.Set("↳", subOn);
        contentChip.Set("¶", o.Content);
        ExcludeBox.Visibility = subOn ? Visibility.Visible : Visibility.Collapsed;
        int found = tab.View.Count - (tab.HasUp && tab.Matcher.IsAll ? 1 : 0);
        if (err != null)
        {
            countText.Text = "invalid";
            countText.ToolTip = err;
            countText.Foreground = Paint.B(Paint.ErrorFg);
        }
        else
        {
            countText.ToolTip = null;
            countText.Foreground = Paint.B(Paint.Weak);
            if (tab.DeepStatus is { } ds)
                countText.Text = $"{found}{(ds.Truncated ? "+ (limit)" : ds.Running ? " …" : "")}";
            else
                countText.Text = $"{found} / {tab.Entries.Count(e => !e.IsUp && !e.Deep)}";
        }
        if (Pane.FocusFilter)
        {
            Pane.FocusFilter = false;
            Dispatcher.BeginInvoke(() =>
            {
                FilterBox.Focus();
                FilterBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    // --- header

    private string headerKey = "";

    private void RenderHeader()
    {
        var view = app.Store.View;
        header.Visibility = view == ViewMode.Grid ? Visibility.Collapsed : Visibility.Visible;
        if (view == ViewMode.Grid) return;
        var c = app.Store.Cols;
        var key = $"{c.Ext}{c.Size}{c.Date}{Tab.Sort}{Tab.Asc}{Theme.IsDark}";
        if (key == headerKey) return;
        headerKey = key;
        header.Children.Clear();
        header.ColumnDefinitions.Clear();
        // name * | ext 54 | size 76 | date 128 (+8 gaps, 6 right margin) – same as RowPaint.Columns
        void Col(GridLength w) => header.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        Col(new GridLength(1, GridUnitType.Star));
        Col(new GridLength(c.Ext ? 54 + 8 : 0));
        Col(new GridLength(c.Size ? 76 + 8 : 0));
        Col(new GridLength(c.Date ? 128 + 8 : 0));
        Col(new GridLength(4));
        void Hdr(int col, SortKey k, string label, bool right)
        {
            var arrow = Tab.Sort == k ? (Tab.Asc ? " ↑" : " ↓") : "";
            var t = new TextBlock { Text = label + arrow, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(k == SortKey.Name ? 26 : (right ? 0 : 8), 0, right ? 2 : 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");
            var b = new Border { Child = t, Background = Brushes.Transparent, CornerRadius = new CornerRadius(2) };
            b.MouseEnter += (_, _) => b.Background = Paint.B(Paint.Tint(10));
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, e) => { app.SortPane(Pi, k); e.Handled = true; };
            Grid.SetColumn(b, col);
            header.Children.Add(b);
        }
        Hdr(0, SortKey.Name, "Name", false);
        if (c.Ext) Hdr(1, SortKey.Ext, "Type", false);
        if (c.Size) Hdr(2, SortKey.Size, "Size", true);
        if (c.Date) Hdr(3, SortKey.Modified, "Modified", false);
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Paint.B(Paint.Line) };
        Grid.SetColumnSpan(line, 5);
        header.Children.Add(line);
    }

    // --- list

    private void RenderList()
    {
        var tab = Tab;
        var view = app.Store.View;
        Ctx.IsActive = app.ActivePane == Pi;
        Ctx.View = view;
        Ctx.Cols = app.Store.Cols;
        Ctx.Diff = app.DiffFor(Pi);
        Ctx.Cut = app.CutSet;
        Ctx.EditingPath = app.Inline is { } ir && ir.Pi == Pi ? ir.Path : null;
        int gcols = Math.Max(1, (int)Math.Floor((list.ActualWidth - 14) / GridRow.CellW));
        Ctx.GridCols = view == ViewMode.Grid ? gcols : 1;
        if (tab.Error != null)
        {
            emptyText.Text = "⚠ " + tab.Error;
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorFg");
        }
        else if (tab.View.All(i => tab.Entries[i].IsUp))
        {
            emptyText.Text = tab.Filter.Length == 0 ? "Empty folder" : "Nothing matches the filter";
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");
        }
        else emptyText.Text = "";
        // below the ".." row (the error replaces the list, so it starts at the top)
        int upRows = tab.Error != null ? 0 : tab.View.Count(i => tab.Entries[i].IsUp);
        emptyText.Margin = new Thickness(6, 4 + (view == ViewMode.Grid ?(upRows > 0 ? GridRow.CellH : 0) : upRows * Tab.RowH), 0, 0);
        // the error replaces the list (like Rust)
        list.Visibility = tab.Error != null ? Visibility.Hidden : Visibility.Visible;
        bool grid = view == ViewMode.Grid;
        if (!ReferenceEquals(itemsFor, tab.View) || itemsMode != view || (grid && itemsCols != gcols))
        {
            bool modeChanged = itemsMode != view || list.ItemTemplate == null;
            itemsFor = tab.View;
            itemsMode = view;
            itemsCols = gcols;
            if (modeChanged)
            {
                var f = new FrameworkElementFactory(grid ? typeof(GridRow) : typeof(FileRow));
                f.AddHandler(LoadedEvent, new RoutedEventHandler((s, _) =>
                {
                    if (s is FileRow fr) fr.Ctx = Ctx;
                    else if (s is GridRow gr) gr.Ctx = Ctx;
                    ((FrameworkElement)s).InvalidateMeasure();
                    ((FrameworkElement)s).InvalidateVisual();
                    Realized.Add((FrameworkElement)s);
                }));
                list.ItemTemplate = new DataTemplate { VisualTree = f };
            }
            int count = grid ? (tab.View.Count + gcols - 1) / gcols : tab.View.Count;
            var items = new List<RowVm>(count);
            for (int i = 0; i < count; i++) items.Add(new RowVm(i));
            list.ItemsSource = items;
        }
        foreach (var r in Realized)
        {
            if (r is FileRow fr) fr.Ctx = Ctx;
            else if (r is GridRow gr) gr.Ctx = Ctx;
            r.InvalidateVisual();
        }
        PositionInline();
    }

    public void ScrollToTop()
    {
        if (list.Items.Count > 0) list.ScrollIntoView(list.Items[0]);
    }

    /// <summary>Keeps the cursor row visible (after keyboard moves).</summary>
    public void EnsureCursorVisible()
    {
        var tab = Tab;
        int idx = Ctx.View == ViewMode.Grid ? tab.Cursor / Math.Max(1, Ctx.GridCols) : tab.Cursor;
        if (idx >= 0 && idx < list.Items.Count) list.ScrollIntoView(list.Items[idx]);
    }

    /// <summary>Rows that fit in the list (for PageUp / PageDown).</summary>
    public int VisibleRows
    {
        get
        {
            double h = Ctx.View == ViewMode.Grid ? GridRow.CellH : Ctx.View == ViewMode.Compact ? 19 : Tab.RowH;
            return Math.Max(1, (int)(list.ActualHeight / h) - 1);
        }
    }

    public int GridCols => Ctx.View == ViewMode.Grid ? Ctx.GridCols : 0;

    private (FrameworkElement? Row, int Vi) HitRow(Point pInList)
    {
        var hit = list.InputHitTest(pInList) as DependencyObject;
        while (hit != null && hit is not FileRow && hit is not GridRow) hit = VisualTreeHelper.GetParent(hit);
        if (hit is FileRow fr) return (fr, fr.Vi);
        if (hit is GridRow gr) return (gr, gr.ViAt(gr.TranslatePoint(pInList, gr) is var _ ? list.TranslatePoint(pInList, gr).X : 0));
        return (null, -1);
    }

    private bool OnScrollBar(MouseEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null)
        {
            if (d is ScrollBar) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : null;
        }
        return false;
    }

    private void ListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (OnScrollBar(e) || InlineBox.IsMouseOver) return;
        app.FocusList();
        var (_, vi) = HitRow(e.GetPosition(list));
        pressVi = vi;
        pressAt = vi >= 0 ? e.GetPosition(list) : null;
        pressDouble = e.ClickCount >= 2;
        if (pressDouble && vi >= 0)
        {
            app.OpenAt(Pi, vi);
            pressAt = null;
        }
        else if (vi < 0) app.ActivatePane(Pi);
        e.Handled = true;
    }

    private void ListMouseMove(object sender, MouseEventArgs e)
    {
        if (pressAt is { } p && e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(list) - p).Length > 5 && pressVi >= 0)
        {
            pressAt = null;
            var tab = Tab;
            if (pressVi < tab.View.Count)
            {
                var entry = tab.Entries[tab.View[pressVi]];
                if (!entry.IsUp)
                {
                    var paths = tab.Selected.Contains(entry.Path) ? tab.Targets() : [entry.Path];
                    app.StartFileDrag(Pi, paths, list);
                }
            }
        }
    }

    private void ListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (pressAt == null || pressDouble) return;
        pressAt = null;
        if (pressVi >= 0)
        {
            var m = Keyboard.Modifiers;
            app.ClickAt(Pi, pressVi, m.HasFlag(ModifierKeys.Control), m.HasFlag(ModifierKeys.Shift));
        }
        e.Handled = true;
    }

    private void ListOtherDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            var (_, vi) = HitRow(e.GetPosition(list));
            if (vi >= 0) app.MiddleOpenAt(Pi, vi);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            if (OnScrollBar(e)) return;
            app.FocusList();
            var (_, vi) = HitRow(e.GetPosition(list));
            if (vi >= 0) app.ContextAt(Pi, vi);
            else app.ActivatePane(Pi);
            e.Handled = true;
        }
    }

    private void ListRightUp(object sender, MouseButtonEventArgs e)
    {
        if (OnScrollBar(e)) return;
        var (_, vi) = HitRow(e.GetPosition(list));
        if (vi >= 0) app.ShowItemMenu(Pi, vi, list);
        else app.ShowBackgroundMenu(Pi, list);
        e.Handled = true;
    }

    // --- inline rename editor (overlay over the row)

    public void PositionInline()
    {
        var ir = app.Inline;
        if (ir == null || ir.Pi != Pi)
        {
            InlineBox.Visibility = Visibility.Collapsed;
            return;
        }
        var tab = Tab;
        int vi = tab.View.FindIndex(i => VPath.Same(tab.Entries[i].Path, ir.Path));
        FrameworkElement? row = null;
        int col = 0;
        foreach (var r in Realized)
        {
            if (r is FileRow fr && fr.Vi == vi) row = fr;
            else if (r is GridRow gr && Ctx.GridCols > 0 && gr.Row == vi / Ctx.GridCols)
            {
                row = gr;
                col = vi % Ctx.GridCols;
            }
        }
        if (row == null || !row.IsVisible)
        {
            InlineBox.Visibility = Visibility.Hidden;
            return;
        }
        Rect rect;
        if (row is GridRow g)
        {
            var cr = g.CellRect(col);
            rect = new Rect(cr.X + 3, cr.Y + 80, Math.Max(20, cr.Width - 6), 22);
        }
        else
        {
            var (cName, _, _, _) = RowPaint.Columns(new Rect(row.RenderSize), Ctx.Cols);
            rect = new Rect(cName.Left + 22, 1, Math.Max(20, cName.Width - 24), row.RenderSize.Height - 2);
        }
        var p = row.TranslatePoint(rect.TopLeft, overlay);
        Canvas.SetLeft(InlineBox, p.X);
        Canvas.SetTop(InlineBox, p.Y);
        InlineBox.Width = rect.Width;
        InlineBox.Height = rect.Height;
        InlineBox.Visibility = Visibility.Visible;
    }

    // --- drag & drop

    public void SetDropTarget(string? p)
    {
        if (Ctx.DropTarget == p) return;
        Ctx.DropTarget = p;
        foreach (var r in Realized) r.InvalidateVisual();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(MainWindow.TabFormat))
        {
            e.Effects = DragDropEffects.Move;
            app.TabDragOverPane(Pi);
            e.Handled = true;
            return;
        }
        var paths = MainWindow.DraggedPaths(e.Data);
        if (paths == null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        string? target = null;
        var (_, vi) = HitRow(e.GetPosition(list));
        if (vi >= 0 && vi < Tab.View.Count)
        {
            var en = Tab.Entries[Tab.View[vi]];
            if (en.IsDir && !paths.Any(p => VPath.Same(p, en.Path))) target = en.Path;
        }
        SetDropTarget(target);
        var dest = target ?? Tab.Path;
        e.Effects = app.DropMove(paths, dest, e.KeyStates, e.Data.GetDataPresent(MainWindow.FilesFormat)) ? DragDropEffects.Move : DragDropEffects.Copy;
        app.HighlightDropPane(Pi);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var target = Ctx.DropTarget;
        SetDropTarget(null);
        app.HighlightDropPane(null);
        if (e.Data.GetDataPresent(MainWindow.TabFormat))
        {
            app.TabDropOnPane(Pi, e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey));
            e.Handled = true;
            return;
        }
        var paths = MainWindow.DraggedPaths(e.Data);
        if (paths != null) app.DropFiles(Pi, paths, target ?? Tab.Path, e.KeyStates, e.Data.GetDataPresent(MainWindow.FilesFormat));
        e.Handled = true;
    }

    private void TabStripDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(MainWindow.TabFormat)) return;
        app.TabDragOverStrip(Pi, e.GetPosition(tabStrip).X);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void TabStripDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(MainWindow.TabFormat)) return;
        app.TabDropOnStrip(Pi, e.GetPosition(tabStrip).X);
        e.Handled = true;
    }

    /// <summary>Insertion mark while reordering tabs (null = hide).</summary>
    private readonly Border insertMark = new() { Width = 2.5, Background = Paint.B(Paint.Accent), IsHitTestVisible = false };

    public void ShowInsertMark(double? x)
    {
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(tabStrip);
        tabStrip.Tag = x;
        if (x == null)
        {
            if (insertAdorner != null) layer?.Remove(insertAdorner);
            insertAdorner = null;
            return;
        }
        if (insertAdorner == null && layer != null)
        {
            insertAdorner = new LineAdorner(tabStrip);
            layer.Add(insertAdorner);
        }
        insertAdorner?.InvalidateVisual();
    }

    private LineAdorner? insertAdorner;

    private sealed class LineAdorner(UIElement e) : System.Windows.Documents.Adorner(e)
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (((FrameworkElement)AdornedElement).Tag is double x)
                dc.DrawLine(new Pen(Paint.B(Paint.Accent), 2.5), new Point(x, 0), new Point(x, ((FrameworkElement)AdornedElement).ActualHeight));
        }
    }

    public double TabStripInsertX(int at)
    {
        var rects = TabRects();
        if (rects.Count == 0) return 0;
        return at < rects.Count ? rects[at].Left - 2 : rects[^1].Right + 2;
    }

    public int TabInsertIndex(double x) => TabRects().Count(r => r.Left + r.Width / 2 < x);

    // --- footer

    private void RenderFooter()
    {
        var tab = Tab;
        var parts = new List<string>();
        if (tab.LoadingTo is { } to)
        {
            var secs = (DateTime.Now - tab.LoadingSince!.Value).TotalSeconds;
            parts.Add(Sftp.ConnectingTo(to) is { } c
                ? $"{MainWindow.Spinner()} Connecting to {c.Label} – {c.Phase} … {secs:0}s"
                : $"{MainWindow.Spinner()} Loading {Sftp.Pretty(to) ?? VPath.DisplayName(to)} … {secs:0}s");
        }
        parts.Add(tab.SelectionSummary());
        if (tab.Current is { IsDir: false } cur) parts.Add("· " + cur.Name);
        footer.Text = string.Join("  ", parts);
        var view = app.Store.View;
        viewBtn.Text = view switch { ViewMode.Details => "☰", ViewMode.Compact => "≡", _ => "▦" };
        viewBtn.ToolTip = $"View: {MainWindow.ViewLabel(view)} (Alt+V) – right-click for options";
    }
}
