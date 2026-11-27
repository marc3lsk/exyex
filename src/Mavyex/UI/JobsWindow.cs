using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

/// <summary>Operations list in its own native window (app.rs ui_jobs): own title bar, movable to another monitor,
/// never blocks the main window. Closing it only hides it (Details chip in the status bar shows it again).</summary>
public sealed class JobsWindow : Window
{
    private readonly MainWindow app;
    private readonly TextBlock header = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly StackPanel list = new();
    public bool AllowClose;
    public event Action<WinGeom>? GeometryChanged;

    public JobsWindow(MainWindow app, WinGeom? saved)
    {
        this.app = app;
        Style = (Style)Application.Current.FindResource("BaseWindow");
        Title = "mavyex — Operations";
        Width = 440;
        Height = 300;
        MinWidth = 340;
        MinHeight = 150;
        ShowActivated = false; // do not steal focus from the file manager
        ShowInTaskbar = true;
        Icon = app.Icon;
        if (saved != null && saved.OnScreen())
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.X;
            Top = saved.Y;
            Width = saved.W;
            Height = saved.H;
        }
        else
        {
            // first placement: bottom-right corner of the main window
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = app.Left + app.ActualWidth - Width - 24;
            Top = app.Top + app.ActualHeight - Height - 70;
        }
        header.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");
        var dock = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        var sep = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 4) };
        sep.SetResourceReference(Border.BackgroundProperty, "Line");
        DockPanel.SetDock(sep, Dock.Top);
        dock.Children.Add(sep);
        dock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;
        SourceInitialized += (_, _) => Theme.TitleBar(this);
        LocationChanged += (_, _) => Report();
        SizeChanged += (_, _) => Report();
    }

    private void Report()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        GeometryChanged?.Invoke(new WinGeom { X = Left, Y = Top, W = Width, H = Height });
    }

    private static TextBlock T(string s, string brush = "Fg", bool strong = false)
    {
        var t = new TextBlock { Text = s, TextTrimming = TextTrimming.CharacterEllipsis };
        if (strong) t.FontWeight = FontWeights.SemiBold;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public void Update(List<Job> jobs, Action<long> dismiss)
    {
        int running = jobs.Count(j => j.State == JobState.Running);
        header.Text = $"{jobs.Count} operation(s), {running} running";
        list.Children.Clear();
        for (int i = 0; i < jobs.Count; i++)
        {
            var j = jobs[i];
            if (i > 0)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
                sep.SetResourceReference(Border.BackgroundProperty, "Line");
                list.Children.Add(sep);
            }
            var row = new StackPanel();
            var top = new DockPanel();
            if (j.State == JobState.Running)
            {
                var b = new Button { Content = j.Cancelling ? "Cancelling…" : "Cancel", IsEnabled = !j.Cancelling, Padding = new Thickness(10, 2, 10, 2) };
                b.Click += (_, _) => app.CancelJob(j);
                DockPanel.SetDock(b, Dock.Right);
                top.Children.Add(b);
            }
            else if (j.State == JobState.Failed)
            {
                var b = new Button { Content = "Dismiss", Padding = new Thickness(10, 2, 10, 2) };
                long id = j.Id;
                b.Click += (_, _) => dismiss(id);
                DockPanel.SetDock(b, Dock.Right);
                top.Children.Add(b);
            }
            top.Children.Add(T(j.Title, strong: true));
            row.Children.Add(top);
            if (j.Dest != null) row.Children.Add(T($"to {j.Dest}", "WeakFg"));
            switch (j.State)
            {
                case JobState.Running:
                {
                    bool indeterminate = j.Kind == OpKind.Trash;
                    long total = Interlocked.Read(ref j.Total), done = Interlocked.Read(ref j.Done);
                    var bar = new ProgressBar { Height = 8, Margin = new Thickness(0, 4, 0, 2), Minimum = 0, Maximum = 1, IsIndeterminate = indeterminate,
                        Value = total > 0 && !indeterminate ? Math.Clamp((double)done / total, 0, 1) : 0 };
                    bar.Foreground = Paint.B(Paint.Accent);
                    bar.SetResourceReference(BackgroundProperty, "ButtonBg");
                    bar.BorderThickness = new Thickness(0);
                    row.Children.Add(bar);
                    double el = Math.Max(0.001, (DateTime.Now - j.Started).TotalSeconds);
                    string info;
                    if (j.Kind is OpKind.Copy or OpKind.Move or OpKind.Zip && total > 0)
                    {
                        double rate = done / el;
                        double left = rate > 1 && total > done ? (total - done) / rate : 0;
                        var eta = left > 0.5 && el > 1.5 ? $" · {Helpers.FmtDur(left)} left" : "";
                        info = $"{Text.FmtSize(done)} of {Text.FmtSize(total)} · {Text.FmtSize((long)rate)}/s{eta}";
                    }
                    else if (j.Kind == OpKind.Delete) info = $"{done} of {total} items";
                    else info = "Working…";
                    row.Children.Add(T(info, "WeakFg"));
                    var cur = Volatile.Read(ref j.Current);
                    if (cur.Length > 0 && !indeterminate) row.Children.Add(T(cur, "WeakFg"));
                    break;
                }
                case JobState.Done:
                {
                    var t = T("✓ Done");
                    t.Foreground = Paint.B(Color.FromRgb(70, 170, 90));
                    row.Children.Add(t);
                    break;
                }
                case JobState.Cancelled:
                    row.Children.Add(T("Cancelled", "WeakFg"));
                    break;
                case JobState.Failed:
                {
                    var t = T($"⚠ {j.Error}", "ErrorFg");
                    t.TextWrapping = TextWrapping.Wrap;
                    row.Children.Add(t);
                    break;
                }
            }
            list.Children.Add(row);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) => base.OnClosing(e);
}
