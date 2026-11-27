using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mavyex.UI;

/// <summary>Shared drawing helpers (colours of app.rs: ACCENT, tint, selected_text, …).</summary>
public static class Paint
{
    public static readonly Color Accent = Color.FromRgb(76, 139, 245);
    public static Typeface Ui { get; } = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static Typeface UiBold { get; } = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    public static Typeface Mono { get; } = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static bool Dark => Theme.IsDark;

    /// <summary>egui gamma_multiply: scales all channels incl. alpha (premultiplied) – here: alpha only.</summary>
    public static Color Mul(Color c, double k) => Color.FromArgb((byte)Math.Clamp(c.A * k, 0, 255), c.R, c.G, c.B);
    /// <summary>Neutral overlay: white on dark, black on light.</summary>
    public static Color Tint(byte alpha) => Dark ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, 0, 0, 0);
    public static Color SelectedText => Dark ? Color.FromRgb(255, 196, 92) : Color.FromRgb(176, 84, 0);
    public static Color Res(string key) => Application.Current.Resources[key] is SolidColorBrush b ? b.Color : Colors.Magenta;
    public static Color Fg => Res("Fg");
    public static Color Weak => Res("WeakFg");
    public static Color ErrorFg => Res("ErrorFg");
    public static Color Line => Res("Line");

    private static readonly Dictionary<Color, SolidColorBrush> brushes = [];
    public static SolidColorBrush B(Color c)
    {
        if (!brushes.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            if (brushes.Count < 4096) brushes[c] = b;
        }
        return b;
    }

    public static Pen P(Color c, double w)
    {
        var p = new Pen(B(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    public static FormattedText Text(string s, double size, Color c, Visual v, Typeface? tf = null) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf ?? Ui, size, B(c), VisualTreeHelper.GetDpi(v).PixelsPerDip);

    public enum Ico { Back, Fwd, Up, Root, Refresh, Plus, Close, Down }

    /// <summary>Vector icons of app.rs paint_icon, centred on c.</summary>
    public static void Icon(DrawingContext dc, Point c, Ico ico, Color col)
    {
        var pen = P(col, 1.6);
        (double, double, double, double)[] segs = ico switch
        {
            Ico.Back => [(-5, 0, 5, 0), (-5, 0, -1, -4), (-5, 0, -1, 4)],
            Ico.Fwd => [(-5, 0, 5, 0), (5, 0, 1, -4), (5, 0, 1, 4)],
            Ico.Up => [(0, 5, 0, -5), (0, -5, -4, -1), (0, -5, 4, -1)],
            Ico.Root => [(-5, -5, 5, -5), (0, 5, 0, -3), (0, -3, -3.5, 0.5), (0, -3, 3.5, 0.5)],
            Ico.Plus => [(-4.5, 0, 4.5, 0), (0, -4.5, 0, 4.5)],
            Ico.Close => [(-3, -3, 3, 3), (-3, 3, 3, -3)],
            Ico.Down => [(-4, -2, 0, 2), (0, 2, 4, -2)],
            _ => [],
        };
        if (ico == Ico.Refresh)
        {
            // open circle (clockwise, gap at the top right) ending in an arrow head
            double r = 4.8, a0 = -40 * Math.PI / 180, sweep = 285 * Math.PI / 180;
            Point Pt(double t) => new(c.X + r * Math.Cos(t), c.Y + r * Math.Sin(t));
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(Pt(a0), false, false);
                for (int i = 1; i <= 20; i++) g.LineTo(Pt(a0 + sweep * i / 20), true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
            double end = a0 + sweep;
            var tang = new Vector(-Math.Sin(end), Math.Cos(end));
            var tip = Pt(end) + tang * 2.2;
            foreach (var side in new[] { -1.0, 1.0 })
            {
                double ang = side * 0.75;
                var rot = new Vector(tang.X * Math.Cos(ang) - tang.Y * Math.Sin(ang), tang.X * Math.Sin(ang) + tang.Y * Math.Cos(ang));
                dc.DrawLine(pen, tip, tip - rot * 4);
            }
            return;
        }
        foreach (var (x1, y1, x2, y2) in segs) dc.DrawLine(pen, new Point(c.X + x1, c.Y + y1), new Point(c.X + x2, c.Y + y2));
    }
}

/// <summary>Flat, rounded icon button with a vector glyph (app.rs icon_btn), 28×26.</summary>
public sealed class IconBtn : FrameworkElement
{
    private readonly Paint.Ico ico;
    public event Action? Click;
    private bool down;

    public IconBtn(Paint.Ico ico, string tip)
    {
        this.ico = ico;
        Width = 28;
        Height = 26;
        ToolTip = tip;
        ToolTipService.SetShowOnDisabled(this, false);
        IsEnabledChanged += (_, _) => InvalidateVisual();
        MouseEnter += (_, _) => InvalidateVisual();
        MouseLeave += (_, _) => { down = false; InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) => { down = true; CaptureMouse(); InvalidateVisual(); e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            bool hit = down && IsMouseOver;
            down = false;
            ReleaseMouseCapture();
            InvalidateVisual();
            if (hit && IsEnabled) Click?.Invoke();
            e.Handled = true;
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (IsEnabled && IsMouseOver)
            dc.DrawRoundedRectangle(Paint.B(Paint.Tint(down ? (byte)40 : (byte)22)), null, new Rect(RenderSize), 7, 7);
        var c = Paint.Fg;
        Paint.Icon(dc, new Point(RenderSize.Width / 2, RenderSize.Height / 2), ico, IsEnabled ? c : Paint.Mul(c, 0.3));
    }
}

/// <summary>Small rounded chip (drive letters, favorite star, search modes) – app.rs chip.</summary>
public sealed class Chip : FrameworkElement
{
    private string text;
    private bool selected;
    private Color? color;
    public event Action? Click;
    public event Action? RightClick;

    public Chip(string text, bool selected = false, Color? color = null, string? tip = null)
    {
        this.text = text;
        this.selected = selected;
        this.color = color;
        if (tip != null) ToolTip = tip;
        Height = 20;
        Cursor = Cursors.Hand;
        Margin = new Thickness(0, 0, 4, 0);
        MouseEnter += (_, _) => InvalidateVisual();
        MouseLeave += (_, _) => InvalidateVisual();
        MouseLeftButtonUp += (_, e) => { Click?.Invoke(); e.Handled = true; };
        MouseRightButtonUp += (_, e) => { if (RightClick != null) { RightClick(); e.Handled = true; } };
    }

    public void Set(string t, bool sel, Color? col = null)
    {
        if (t == text && sel == selected && col == color) return;
        text = t;
        selected = sel;
        color = col;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private FormattedText Ft() => Paint.Text(text, 12.5, color ?? (selected ? Paint.Fg : Paint.Weak), this);

    protected override Size MeasureOverride(Size available) => new(Ft().WidthIncludingTrailingWhitespace + 14, 20);

    protected override void OnRender(DrawingContext dc)
    {
        var r = new Rect(RenderSize);
        if (selected) dc.DrawRoundedRectangle(Paint.B(Paint.Mul(Paint.Accent, Paint.Dark ? 0.38 : 0.28)), null, r, 10, 10);
        else if (IsMouseOver) dc.DrawRoundedRectangle(Paint.B(Paint.Tint(18)), null, r, 10, 10);
        else dc.DrawRectangle(Brushes.Transparent, null, r);
        var ft = Ft();
        dc.DrawText(ft, new Point((r.Width - ft.Width) / 2, (r.Height - ft.Height) / 2));
    }
}

/// <summary>Pill-shaped tab (app.rs tab_pill) with an optional close cross.</summary>
public sealed class TabPill : FrameworkElement
{
    public string Label { get; }
    public bool Selected { get; }
    public bool Closable { get; }
    public event Action? Click;
    public event Action? CloseClick;
    public event Action? MiddleClick;
    public event Action? DragOut;
    private Point? pressAt;
    private bool overX;

    public TabPill(string label, bool selected, bool closable, string tip)
    {
        Label = label;
        Selected = selected;
        Closable = closable;
        ToolTip = tip;
        Height = 26;
        Margin = new Thickness(0, 0, 4, 0);
        MouseEnter += (_, _) => InvalidateVisual();
        MouseLeave += (_, _) => { overX = false; InvalidateVisual(); };
        MouseMove += (_, e) =>
        {
            bool x = XRect().Contains(e.GetPosition(this));
            if (x != overX)
            {
                overX = x;
                InvalidateVisual();
            }
            if (pressAt is { } p && e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(this) - p).Length > 6)
            {
                pressAt = null;
                DragOut?.Invoke();
            }
        };
        MouseLeftButtonDown += (_, e) => { pressAt = e.GetPosition(this); e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            if (pressAt == null) return;
            pressAt = null;
            if (Closable && (Selected || IsMouseOver) && XRect().Contains(e.GetPosition(this))) CloseClick?.Invoke();
            else Click?.Invoke();
            e.Handled = true;
        };
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle && Closable)
            {
                MiddleClick?.Invoke();
                e.Handled = true;
            }
        };
    }

    private Rect XRect() => new(RenderSize.Width - 13 - 8, RenderSize.Height / 2 - 1 - 8, 16, 16);

    private FormattedText Ft() => Paint.Text(Label, 13.5, Selected ? Paint.Fg : Paint.Weak, this);

    protected override Size MeasureOverride(Size available) => new(Ft().WidthIncludingTrailingWhitespace + 22 + (Closable ? 18 : 0), 26);

    protected override void OnRender(DrawingContext dc)
    {
        var r = new Rect(RenderSize);
        bool hovered = IsMouseOver;
        if (Selected)
        {
            dc.DrawRoundedRectangle(Paint.B(Paint.Tint(20)), null, r, 7, 7);
            dc.DrawRoundedRectangle(Paint.B(Paint.Accent), null, new Rect(new Point(9, r.Height - 3), new Point(r.Width - 9, r.Height - 1)), 1, 1);
        }
        else if (hovered) dc.DrawRoundedRectangle(Paint.B(Paint.Tint(12)), null, r, 7, 7);
        else dc.DrawRectangle(Brushes.Transparent, null, r);
        var ft = Ft();
        dc.DrawText(ft, new Point(11, (r.Height - ft.Height) / 2));
        if (Closable && (Selected || hovered))
        {
            var xr = XRect();
            if (overX) dc.DrawRoundedRectangle(Paint.B(Paint.Tint(30)), null, xr, 4, 4);
            Paint.Icon(dc, new Point(xr.X + 8, xr.Y + 8), Paint.Ico.Close, Selected ? Paint.Fg : Paint.Weak);
        }
    }
}
