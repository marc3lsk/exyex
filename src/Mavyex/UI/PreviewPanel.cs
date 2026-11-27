using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mavyex.Core;

namespace Mavyex.UI;

/// <summary>Quick Look side panel (F3, preview.rs Previewer): text head, images, SVG (image / source), folder summary,
/// archive contents, hex head; loads in the background and ignores results for an entry the cursor already left.</summary>
public sealed class PreviewPanel : UserControl
{
    /// <summary>Raised when the user closes the panel with its ✕.</summary>
    public event Action? CloseRequested;

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
    // Rust started the loader on every path change; a short delay keeps key-repeat scrolling from spawning a
    // thread (and a file read / remote request) per row while still feeling instant.
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);

    private readonly TextBlock name = new() { FontWeight = FontWeights.Bold, FontSize = 15, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock meta = new() { Margin = new Thickness(0, 1, 0, 4), TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl body = new() { Focusable = false };
    private readonly DispatcherTimer timer;

    private string? path;          // entry currently shown (null = nothing)
    private Entry? pending;        // entry waiting for the debounce timer
    private int gen;               // bumps on every path change; stale loader results are dropped
    private PvData? data;
    private bool svgSource;        // SVG: show the XML source instead of the image (kept across entries, like Rust)

    public PreviewPanel()
    {
        Resources.Add(typeof(ScrollBar), ScrollBarStyle());
        SetResourceReference(ForegroundProperty, "Fg");
        meta.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");

        var title = new TextBlock { Text = "Quick Look", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { ToolTip = "Close (F3)", HorizontalAlignment = HorizontalAlignment.Right };
        close.SetResourceReference(StyleProperty, "XButton");
        close.Click += (_, _) => RaiseClose();
        var head = new Grid();
        head.Children.Add(title);
        head.Children.Add(close);

        var sep = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
        sep.SetResourceReference(Border.BackgroundProperty, "Line");

        var dock = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
        foreach (var top in new FrameworkElement[] { head, sep, name, meta })
        {
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);
        }
        dock.Children.Add(body);
        Content = dock;

        timer = new DispatcherTimer(Debounce, DispatcherPriority.Background, (_, _) => StartLoad(), Dispatcher);
        timer.Stop();
        ShowNothing();
    }

    /// <summary>Shows the entry under the cursor (null = nothing). Cheap to call repeatedly with the same entry.</summary>
    public void ShowEntry(Entry? e)
    {
        if (e is null || e.IsUp)
        {
            if (path is not null || data is not null || pending is not null)
            {
                path = null;
                pending = null;
                data = null;
                gen++;
                timer.Stop();
            }
            ShowNothing();
            return;
        }
        // header follows the live entry (size / time may change after a reload of the same path)
        SetText(name, e.Name);
        SetText(meta, PreviewLoader.Meta(e));
        name.Visibility = meta.Visibility = Visibility.Visible;
        if (string.Equals(path, e.Path, StringComparison.Ordinal)) return;
        path = e.Path;
        pending = e.Clone();
        data = null;
        gen++;
        body.Content = Weak("Loading…");
        timer.Stop();
        timer.Start();
    }

    internal void RaiseClose() => CloseRequested?.Invoke();

    private void StartLoad()
    {
        timer.Stop();
        if (pending is not { } ent) return;
        pending = null;
        int g = gen;
        Worker.RunSta(() =>
        {
            var d = PreviewLoader.Load(ent);
            Dispatcher.BeginInvoke(() =>
            {
                if (g != gen) return; // the cursor moved on meanwhile
                data = d;
                Render();
            });
        });
    }

    private void ShowNothing()
    {
        name.Visibility = meta.Visibility = Visibility.Collapsed;
        if (body.Content is not TextBlock { Tag: "nothing" }) body.Content = Weak("Nothing selected", "nothing");
    }

    private static void SetText(TextBlock t, string s)
    {
        if (t.Text != s) t.Text = s;
    }

    private void Render()
    {
        body.Content = data switch
        {
            PvData.Text t => TextView(t.Body, t.Truncated),
            PvData.Image i => ImageView(i),
            PvData.Svg s => SvgView(s),
            PvData.Folder f => FolderView(f),
            PvData.Hex h => HexView(h.Dump),
            PvData.Note n => Weak(n.Message),
            _ => null,
        };
    }

    // ------------------------------------------------------------------ views

    private static TextBlock Weak(string s, string? tag = null)
    {
        var t = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, Tag = tag };
        t.SetResourceReference(TextBlock.ForegroundProperty, "WeakFg");
        return t;
    }

    /// <summary>Monospace, selectable, not wrapped (egui's extended label inside a both-ways ScrollArea).</summary>
    private static TextBox Code(string s) => new()
    {
        Text = s,
        IsReadOnly = true,
        IsReadOnlyCaretVisible = false,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = Mono,
        FontSize = 12.5,
        Padding = new Thickness(0),
        Background = Brushes.Transparent,
        VerticalContentAlignment = VerticalAlignment.Top,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private static UIElement TextView(string text, bool truncated)
    {
        var dock = new DockPanel();
        if (truncated)
        {
            var t = Weak("… (truncated)");
            DockPanel.SetDock(t, Dock.Bottom);
            dock.Children.Add(t);
        }
        dock.Children.Add(Code(text));
        return dock;
    }

    /// <summary>Shrinks to the panel width, never enlarges (egui shrink_to_fit).</summary>
    private static Image Picture(BitmapSource img) => new()
    {
        Source = img,
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static UIElement ImageView(PvData.Image i)
    {
        var dock = new DockPanel();
        var size = Weak($"{i.W} × {i.H}");
        size.Margin = new Thickness(0, 0, 0, 4);
        DockPanel.SetDock(size, Dock.Top);
        dock.Children.Add(size);
        var pic = Picture(i.Img);
        RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.HighQuality);
        dock.Children.Add(pic);
        return dock;
    }

    private UIElement SvgView(PvData.Svg s)
    {
        var dock = new DockPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var asImage = new ToggleButton { Content = "Image", IsChecked = !svgSource, Focusable = false };
        var asSource = new ToggleButton { Content = "Source", IsChecked = svgSource, Focusable = false, Margin = new Thickness(4, 0, 8, 0) };
        asImage.Click += (_, _) => { svgSource = false; Render(); };
        asSource.Click += (_, _) => { svgSource = true; Render(); };
        var size = Weak($"{s.W} × {s.H}");
        size.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(asImage);
        bar.Children.Add(asSource);
        bar.Children.Add(size);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        if (svgSource) dock.Children.Add(Code(s.Source));
        else
        {
            // light backdrop so dark / transparent art stays visible in the dark theme too
            var frame = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = Picture(s.Img),
            };
            dock.Children.Add(frame);
        }
        return dock;
    }

    private static UIElement FolderView(PvData.Folder f)
    {
        var list = new StackPanel();
        list.Children.Add(new TextBlock
        {
            Text = $"{f.Dirs} folder(s), {f.Files} file(s), {Text.FmtSize(f.Bytes)}",
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var n in f.Names) list.Children.Add(new TextBlock { Text = n, TextTrimming = TextTrimming.CharacterEllipsis });
        if (f.Dirs + f.Files > f.Names.Count) list.Children.Add(Weak($"… and {f.Dirs + f.Files - f.Names.Count} more"));
        return new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    private static UIElement HexView(string dump)
    {
        var list = new StackPanel();
        list.Children.Add(Weak("Binary file – first bytes:"));
        list.Children.Add(new TextBlock { Text = dump, FontFamily = Mono, FontSize = 11.5, Foreground = Brushes.Gray });
        return new ScrollViewer
        {
            Content = list,
            Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>The app-wide ScrollBar style is vertical-only (fixed Width); this one also gives horizontal bars
    /// (unwrapped text) a proper shape.</summary>
    private static Style ScrollBarStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ScrollBar">
          <Setter Property="Width" Value="8" />
          <Setter Property="MinWidth" Value="8" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ScrollBar">
                <Track x:Name="PART_Track" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" IsDirectionReversed="True">
                  <Track.Thumb><Thumb Style="{DynamicResource ThumbStyle}" Margin="1,0" /></Track.Thumb>
                </Track>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
          <Style.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
              <Setter Property="Width" Value="NaN" />
              <Setter Property="MinWidth" Value="0" />
              <Setter Property="Height" Value="8" />
              <Setter Property="MinHeight" Value="8" />
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ScrollBar">
                    <Track x:Name="PART_Track" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                      <Track.Thumb><Thumb Style="{DynamicResource ThumbStyle}" Margin="0,1" /></Track.Thumb>
                    </Track>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Trigger>
          </Style.Triggers>
        </Style>
        """);
}
