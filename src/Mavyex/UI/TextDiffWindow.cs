using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Mavyex.Core;
using Mavyex.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace Mavyex.UI;

/// <summary>Side-by-side text diff of two files in its own window (DiffPlex). F7/F8 prev/next difference, ignore
/// whitespace / case, Esc closes. Works on local, SFTP and archive-inner files (text only, ≤ 4 MB).</summary>
public sealed class TextDiffWindow : Window
{
    private readonly string pathL, pathR;
    private readonly Action<bool>? darkChanged;
    private bool dark;

    private List<string> left = [], right = [];
    private List<DiffRow> rows = [];
    private List<int> hunks = [];
    private int? cur;
    private int added, removed;
    private bool loaded;
    private int version;

    private readonly StackPanel bar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button themeBtn;
    private readonly CheckBox ignoreWs = new() { Content = "Ignore whitespace", Focusable = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox ignoreCase = new() { Content = "Ignore case", Focusable = false, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DiffView view = new();
    private readonly ScrollBar scroll = new() { Orientation = Orientation.Vertical };
    private readonly Grid body = new();

    /// <summary>Tracks an app-wide resource this window never overrides: Theme.Apply replaces all of them and then
    /// resets every window's title bar to the app theme, so we re-apply our own afterwards.</summary>
    private static readonly DependencyProperty AppThemeProbeProperty = DependencyProperty.Register(
        "AppThemeProbe", typeof(object), typeof(TextDiffWindow),
        new PropertyMetadata(null, (d, _) => ((TextDiffWindow)d).Dispatcher.BeginInvoke(((TextDiffWindow)d).TitleBar)));

    /// <summary>dark: own theme of the diff window (null = follow the app). darkChanged reports a user toggle (to persist).</summary>
    public TextDiffWindow(string a, string b, bool? dark, Action<bool>? darkChanged)
    {
        pathL = a;
        pathR = b;
        this.dark = dark ?? Theme.IsDark;
        this.darkChanged = darkChanged;
        SetResourceReference(StyleProperty, "BaseWindow");
        SetResourceReference(AppThemeProbeProperty, "RowSelBorder");
        static string Name(string p) => Sftp.Pretty(p) ?? p;
        Title = $"mavyex — Compare: {Name(a)}  ↔  {Name(b)}";
        Width = 1100;
        Height = 680;
        MinWidth = 480;
        MinHeight = 240;
        ApplyPalette();

        themeBtn = new Button { Style = (Style)FindResource("IconButton"), Margin = new Thickness(0, 0, 8, 0) };
        themeBtn.Click += (_, _) => ToggleTheme();
        UpdateThemeButton();

        view.OffsetChanged += SyncScrollBar;
        scroll.ValueChanged += (_, e) => view.Offset = e.NewValue;
        Grid.SetColumn(scroll, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(view);
        body.Children.Add(scroll);
        body.Visibility = Visibility.Collapsed;

        var sep = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 2) };
        sep.SetResourceReference(Border.BackgroundProperty, "Line");
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(sep, Dock.Top);
        Content = new DockPanel { Margin = new Thickness(8), Children = { bar, sep, body } };

        ignoreWs.Click += (_, _) => Rebuild();
        ignoreCase.Click += (_, _) => Rebuild();
        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => TitleBar();

        ShowLoading();
        RunInBackground(() =>
        {
            // Both files plus the first diff off the UI thread (big files take a moment).
            var ta = TextDiff.ReadText(a, out var err);
            var tb = err == null ? TextDiff.ReadText(b, out err) : "";
            if (err != null) return (Err: err, L: (List<string>?)null, R: (List<string>?)null, D: default((List<DiffRow>, int, int)));
            var l = TextDiff.Lines(ta);
            var r = TextDiff.Lines(tb);
            return (Err: (string?)null, L: l, R: r, D: TextDiff.DiffRows(l, r, false, false));
        }, t =>
        {
            if (t.IsFaulted) ShowError("Loading failed");
            else if (t.Result.Err != null) ShowError(t.Result.Err);
            else
            {
                left = t.Result.L!;
                right = t.Result.R!;
                loaded = true;
                SetRows(t.Result.D);
                ShowDiff();
            }
        });
    }

    /// <summary>Runs work on the thread pool and hands the finished task back on this window's dispatcher (works
    /// even when the window is created before the dispatcher loop runs, where there is no sync context yet).</summary>
    private void RunInBackground<T>(Func<T> work, Action<Task<T>> done) =>
        Task.Run(work).ContinueWith(t => Dispatcher.BeginInvoke(() => done(t)), TaskScheduler.Default);

    // ---- states ----

    private void ShowLoading()
    {
        bar.Children.Clear();
        bar.Children.Add(themeBtn);
        bar.Children.Add(Spinner());
        bar.Children.Add(new TextBlock { Text = "Loading…", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
    }

    private void ShowError(string e)
    {
        bar.Children.Clear();
        bar.Children.Add(themeBtn);
        var t = new TextBlock { Text = e, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "ErrorFg");
        bar.Children.Add(t);
    }

    private void ShowDiff()
    {
        bar.Children.Clear();
        bar.Children.Add(themeBtn);
        var prev = new Button { Content = "⏶ Previous (F7)", Focusable = false, Padding = new Thickness(10, 4, 10, 4) };
        var next = new Button { Content = "⏷ Next (F8)", Focusable = false, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        prev.Click += (_, _) => Goto(false);
        next.Click += (_, _) => Goto(true);
        var sep = new Border { Width = 1, Margin = new Thickness(12, 4, 12, 4) };
        sep.SetResourceReference(Border.BackgroundProperty, "Line");
        foreach (var c in new UIElement[] { prev, next, ignoreWs, ignoreCase, sep, status }) bar.Children.Add(c);
        body.Visibility = Visibility.Visible;
        UpdateStatus();
    }

    private static FrameworkElement Spinner()
    {
        var arc = new ShapePath
        {
            Data = Geometry.Parse("M 8,1.5 A 6.5,6.5 0 1 1 1.5,8"),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        arc.SetResourceReference(Shape.StrokeProperty, "Fg");
        var rot = new RotateTransform();
        arc.RenderTransform = rot;
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        return arc;
    }

    // ---- diff state ----

    private void SetRows((List<DiffRow> Rows, int Added, int Removed) d)
    {
        rows = d.Rows;
        added = d.Added;
        removed = d.Removed;
        hunks = TextDiff.Hunks(rows);
        cur = null;
        view.SetData(left, right, rows);
    }

    /// <summary>Recomputes after an ignore option changed (on a worker; a newer request wins).</summary>
    private void Rebuild()
    {
        if (!loaded) return;
        int v = ++version;
        bool ws = ignoreWs.IsChecked == true, ic = ignoreCase.IsChecked == true;
        var (l, r) = (left, right);
        RunInBackground(() => TextDiff.DiffRows(l, r, ws, ic), t =>
        {
            if (v != version || t.IsFaulted) return;
            SetRows(t.Result);
            UpdateStatus();
        });
    }

    private void UpdateStatus()
    {
        if (hunks.Count == 0)
        {
            status.Text = "Files are identical";
            status.FontWeight = FontWeights.Bold;
        }
        else
        {
            var at = cur is int c ? $"{c + 1} / " : "";
            status.Text = $"{at}{hunks.Count} differences   +{added} −{removed} lines";
            status.FontWeight = FontWeights.Normal;
        }
    }

    private void Goto(bool next)
    {
        int n = hunks.Count;
        if (n == 0) return;
        int i = (cur, next) switch
        {
            (null, true) => 0,
            (null, false) => n - 1,
            (int c, true) => (c + 1) % n,
            (int c, false) => (c + n - 1) % n,
        };
        cur = i;
        // Both sides share one row list, so one offset scrolls them in sync; two rows of context above.
        view.Offset = Math.Max(hunks[i] - 2.0, 0) * view.RowHeight;
        UpdateStatus();
    }

    private void SyncScrollBar()
    {
        scroll.Maximum = view.MaxOffset;
        scroll.ViewportSize = view.ActualHeight;
        scroll.LargeChange = Math.Max(view.ActualHeight - view.RowHeight, view.RowHeight);
        scroll.SmallChange = view.RowHeight;
        scroll.Value = view.Offset;
        scroll.Visibility = view.MaxOffset > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (!loaded || body.Visibility != Visibility.Visible) return;
        double page = Math.Max(view.ActualHeight - view.RowHeight, view.RowHeight);
        switch (key)
        {
            case Key.F7: Goto(false); break;
            case Key.F8: Goto(true); break;
            case Key.Up: view.Offset -= view.RowHeight; break;
            case Key.Down: view.Offset += view.RowHeight; break;
            case Key.PageUp: view.Offset -= page; break;
            case Key.PageDown: view.Offset += page; break;
            case Key.Home: view.Offset = 0; break;
            case Key.End: view.Offset = view.MaxOffset; break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- own theme ----

    private void ToggleTheme()
    {
        dark = !dark;
        ApplyPalette();
        UpdateThemeButton();
        TitleBar();
        view.InvalidateVisual();
        darkChanged?.Invoke(dark);
    }

    private void UpdateThemeButton()
    {
        themeBtn.Content = Icons.Make(dark ? Icons.Kind.Sun : Icons.Kind.Moon);
        themeBtn.ToolTip = dark ? "Switch this window to the light theme" : "Switch this window to the dark theme";
    }

    /// <summary>Same palette as Theme.Apply, but as this window's own resources so it can differ from the app.</summary>
    private void ApplyPalette()
    {
        bool d = dark;
        static Color Rgb(byte r, byte g, byte b, byte a = 255) => Color.FromArgb(a, r, g, b);
        static Color Gray(byte v) => Rgb(v, v, v);
        void Set(string key, Color c)
        {
            var br = new SolidColorBrush(c);
            br.Freeze();
            Resources[key] = br;
        }
        Set("PanelBg", d ? Rgb(28, 29, 33) : Rgb(246, 246, 248));
        Set("InputBg", d ? Rgb(20, 21, 24) : Colors.White);
        Set("MenuBg", d ? Rgb(36, 37, 42) : Colors.White);
        Set("Fg", d ? Gray(222) : Gray(20));
        Set("WeakFg", d ? Gray(140) : Gray(110));
        Set("Line", d ? Gray(52) : Gray(210));
        Set("Accent", Theme.Accent);
        Set("AccentFill", d ? Rgb(57, 104, 184) : Rgb(66, 125, 230));
        Set("ButtonBg", d ? Gray(46) : Gray(228));
        Set("ButtonPressed", d ? Gray(80) : Gray(196));
        Set("Overlay", d ? Rgb(255, 255, 255, 24) : Rgb(0, 0, 0, 20));
        Set("RowSel", d ? Rgb(76, 139, 245, 82) : Rgb(76, 139, 245, 56));
        Set("ErrorFg", d ? Rgb(255, 110, 110) : Rgb(200, 30, 30));
        view.Dark = d;
        view.Fg = (Brush)Resources["Fg"];
        view.WeakFg = (Brush)Resources["WeakFg"];
    }

    private void TitleBar()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(h, 20, ref v, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// <summary>Draws only the visible rows of the aligned diff (both sides in one element, so they always scroll
/// together); fast for files with hundreds of thousands of lines.</summary>
internal sealed class DiffView : FrameworkElement
{
    private const double NumW = 46, Gap = 6, FontSize = 13;
    /// <summary>Characters drawn per line at most (long lines are clipped anyway).</summary>
    private const int MaxChars = 600;

    private static readonly Typeface Mono = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private IReadOnlyList<string> left = [], right = [];
    private IReadOnlyList<DiffRow> rows = [];
    /// <summary>Inline char segments of Chg rows, computed lazily as rows become visible.</summary>
    private readonly Dictionary<int, (List<(string, bool)> L, List<(string, bool)> R)> segCache = [];
    private double offset;

    public bool Dark;
    public Brush Fg = Brushes.Black, WeakFg = Brushes.Gray;
    public event Action? OffsetChanged;

    public double RowHeight { get; } = Math.Ceiling(FontSize * Mono.FontFamily.LineSpacing) + 3;
    public double MaxOffset => Math.Max(0, rows.Count * RowHeight - ActualHeight);

    public double Offset
    {
        get => offset;
        set
        {
            var v = Math.Clamp(value, 0, MaxOffset);
            if (v == offset) return;
            offset = v;
            InvalidateVisual();
            OffsetChanged?.Invoke();
        }
    }

    public DiffView()
    {
        ClipToBounds = true;
        Focusable = false;
        SizeChanged += (_, _) =>
        {
            offset = Math.Clamp(offset, 0, MaxOffset);
            OffsetChanged?.Invoke();
        };
    }

    public void SetData(IReadOnlyList<string> l, IReadOnlyList<string> r, IReadOnlyList<DiffRow> rs)
    {
        (left, right, rows) = (l, r, rs);
        segCache.Clear();
        offset = Math.Clamp(offset, 0, MaxOffset);
        InvalidateVisual();
        OffsetChanged?.Invoke();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Offset -= e.Delta / 120.0 * Math.Max(1, SystemParameters.WheelScrollLines) * RowHeight;
        e.Handled = true;
    }

    private static Brush Solid(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    // (row background, changed-chars highlight) per kind, same colours as the original.
    private static readonly Brush ChgD = Solid(78, 62, 28), ChgDh = Solid(140, 105, 35), ChgL = Solid(255, 241, 212), ChgLh = Solid(255, 214, 140);
    private static readonly Brush DelD = Solid(92, 36, 42), DelDh = Solid(150, 50, 60), DelL = Solid(255, 226, 229), DelLh = Solid(255, 170, 180);
    private static readonly Brush InsD = Solid(30, 76, 46), InsDh = Solid(45, 120, 65), InsL = Solid(222, 247, 229), InsLh = Solid(150, 225, 170);
    private static readonly Brush EmptyD = Solid(34, 34, 34), EmptyL = Solid(238, 238, 238);

    private (Brush Bg, Brush Hl) Colors(DiffKind k) => (k, Dark) switch
    {
        (DiffKind.Chg, true) => (ChgD, ChgDh),
        (DiffKind.Chg, false) => (ChgL, ChgLh),
        (DiffKind.Del, true) => (DelD, DelDh),
        (DiffKind.Del, false) => (DelL, DelLh),
        (_, true) => (InsD, InsDh),
        _ => (InsL, InsLh),
    };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, rh = RowHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable for the mouse wheel
        if (rows.Count == 0) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double half = Math.Max(0, (w - Gap) / 2);
        int first = (int)(offset / rh);
        for (int ri = first; ri < rows.Count; ri++)
        {
            double y = ri * rh - offset;
            if (y >= h) break;
            var row = rows[ri];
            string? lt = row.L >= 0 ? left[row.L] : null, rt = row.R >= 0 ? right[row.R] : null;
            PaintSide(dc, new Rect(0, y, half, rh), ri, row.L, lt, rt, row.Kind, true, dip);
            PaintSide(dc, new Rect(half + Gap, y, half, rh), ri, row.R, rt, lt, row.Kind, false, dip);
        }
    }

    private static string Tabs(string s) => s.Replace("\t", "    ");

    private void PaintSide(DrawingContext dc, Rect rect, int ri, int idx, string? mine, string? other, DiffKind kind, bool isLeft, double dip)
    {
        if (rect.Width <= 0) return;
        if (idx < 0 || mine == null)
        {
            dc.DrawRectangle(Dark ? EmptyD : EmptyL, null, rect);
            return;
        }
        // Only the side that carries the change is tinted (Del: left, Ins: right, Chg: both).
        bool tinted = kind switch { DiffKind.Same => false, DiffKind.Chg => true, DiffKind.Del => isLeft, _ => !isLeft };
        var (bg, hl) = tinted ? Colors(kind) : (null, null);
        dc.PushClip(new RectangleGeometry(rect));
        if (bg != null) dc.DrawRectangle(bg, null, rect);

        var num = new FormattedText((idx + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Mono, FontSize, WeakFg, dip);
        dc.DrawText(num, new Point(rect.Left + NumW - 6 - num.Width, rect.Top + (rect.Height - num.Height) / 2));

        List<(string, bool)> segs;
        if (kind == DiffKind.Chg && other != null)
        {
            if (!segCache.TryGetValue(ri, out var pair))
            {
                if (segCache.Count > 4096) segCache.Clear(); // bound memory while scrolling through huge diffs
                string tl = Tabs(isLeft ? mine : other), tr = Tabs(isLeft ? other : mine);
                pair = (TextDiff.Segments(tl, tr, true), TextDiff.Segments(tr, tl, false));
                segCache[ri] = pair;
            }
            segs = isLeft ? pair.L : pair.R;
        }
        else segs = [(Tabs(mine), false)];

        // Concatenate up to MaxChars characters, remembering the changed ranges.
        var sb = new System.Text.StringBuilder();
        var marks = new List<(int Start, int Len)>();
        int used = 0;
        foreach (var (s, changed) in segs)
        {
            if (used > MaxChars) break;
            var part = TakeChars(s, MaxChars - Math.Min(used, MaxChars), out int n);
            used += n;
            if (changed && part.Length > 0) marks.Add((sb.Length, part.Length));
            sb.Append(part);
        }
        if (sb.Length > 0)
        {
            var ft = new FormattedText(sb.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, FontSize, Fg, dip);
            var origin = new Point(rect.Left + NumW, rect.Top + (rect.Height - ft.Height) / 2);
            if (hl != null)
                foreach (var (st, len) in marks)
                    if (ft.BuildHighlightGeometry(origin, st, len) is { } g) dc.DrawGeometry(hl, null, g);
            dc.DrawText(ft, origin);
        }
        dc.Pop();
    }

    /// <summary>First <paramref name="max"/> code points of s (the limit counts characters, not UTF-16 units).</summary>
    private static string TakeChars(string s, int max, out int count)
    {
        int i = 0;
        count = 0;
        while (i < s.Length && count < max)
        {
            i += char.IsSurrogatePair(s, i) ? 2 : 1;
            count++;
        }
        return s[..i];
    }
}
