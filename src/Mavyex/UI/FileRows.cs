using System.Windows;
using System.Windows.Media;
using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

/// <summary>Item of the list: index into Tab.View (rows) or a grid row index (grid view).</summary>
public sealed class RowVm(int index)
{
    public int Index { get; } = index;
}

/// <summary>Per-pane drawing state shared by all realized rows (set by PaneView before invalidating them).</summary>
public sealed class RowContext
{
    public required PaneView Pane;
    public Tab Tab => Pane.Tab;
    public bool IsActive;
    public ViewMode View;
    public Cols Cols = new();
    public Dictionary<string, Diff> Diff = [];
    public HashSet<string>? Cut;
    public string? DropTarget;
    public string? EditingPath;
    public int GridCols = 1;
}

/// <summary>Row / cell drawing shared by the list rows and grid cells (app.rs ui_pane).</summary>
public static class RowPaint
{
    /// <summary>Splits a row into (name, type, size, date) rects; hidden columns get a zero-width rect.</summary>
    public static (Rect Name, Rect Ext, Rect Size, Rect Date) Columns(Rect r, Cols c)
    {
        double x = r.Right - 6;
        Rect Take(double w, bool on)
        {
            if (!on) return new Rect(x, r.Top, 0, r.Height);
            var rc = new Rect(x - w, r.Top, w, r.Height);
            x -= w + 8;
            return rc;
        }
        var date = Take(128, c.Date);
        var size = Take(76, c.Size);
        var ext = Take(54, c.Ext);
        var name = new Rect(r.Left, r.Top, Math.Max(0, x + 2 - r.Left), r.Height);
        return (name, ext, size, date);
    }

    public static Color CursorColor(bool active) =>
        active ? (Paint.Dark ? Paint.Mul(Paint.Accent, 0.55) : Color.FromRgb(200, 221, 253)) : Paint.Tint(28);

    /// <summary>Text colour of an entry (selection, hidden / cut dim, compare marks, white on the dark accent cursor).</summary>
    public static Color TextColor(RowContext ctx, Entry e, bool isCursor, bool selected, bool dropTarget, out Diff? diff)
    {
        var col = selected ? Paint.SelectedText : Paint.Fg;
        if (e.Hidden || (ctx.Cut?.Contains(e.Path) ?? false)) col = Paint.Mul(col, 0.55);
        bool onAccent = Paint.Dark && isCursor && ctx.IsActive && !dropTarget;
        if (onAccent && !selected) col = Colors.White;
        diff = null;
        if (ctx.Diff.Count > 0 && !e.IsUp && ctx.Diff.TryGetValue(e.Name.ToLowerInvariant(), out var d))
        {
            diff = d;
            if (!selected && !onAccent) col = d.Style(Paint.Dark).Color;
        }
        return col;
    }
}

/// <summary>One list row (Details / Compact), drawn directly like the Rust version.</summary>
public sealed class FileRow : FrameworkElement
{
    public RowContext? Ctx { get; set; }

    public FileRow()
    {
        MouseEnter += (_, _) => InvalidateVisual();
        MouseLeave += (_, _) => InvalidateVisual();
        Loaded += (_, _) => Ctx?.Pane.Realized.Add(this);
        Unloaded += (_, _) => Ctx?.Pane.Realized.Remove(this);
        DataContextChanged += (_, _) => InvalidateVisual();
    }

    public int Vi => DataContext is RowVm r ? r.Index : -1;

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 100 : available.Width, Ctx?.View == ViewMode.Compact ? 19 : Tab.RowH);

    protected override void OnRender(DrawingContext dc)
    {
        var ctx = Ctx;
        var rect = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.Transparent, null, rect);
        if (ctx == null) return;
        var tab = ctx.Tab;
        int vi = Vi;
        if (vi < 0 || vi >= tab.View.Count) return;
        var e = tab.Entries[tab.View[vi]];
        bool isCursor = vi == tab.Cursor;
        bool selected = tab.Selected.Contains(e.Path);
        bool dropTarget = ctx.DropTarget != null && e.IsDir && VPath.Same(ctx.DropTarget, e.Path);
        if (selected) dc.DrawRectangle(Paint.B(Paint.Mul(Paint.SelectedText, 0.13)), null, rect);
        if (dropTarget) dc.DrawRoundedRectangle(Paint.B(Paint.Mul(Paint.Accent, 0.5)), null, rect, 3, 3);
        else if (isCursor) dc.DrawRectangle(Paint.B(RowPaint.CursorColor(ctx.IsActive)), null, rect);
        else if (IsMouseOver) dc.DrawRectangle(Paint.B(Paint.Tint(10)), null, rect);

        var (cName, cExt, cSize, cDate) = RowPaint.Columns(rect, ctx.Cols);
        double cy = rect.Height / 2;
        if (ShellIcons.Get(e.Ext, e.IsDir) is { } icon) dc.DrawImage(icon, new Rect(14 - 8, cy - 8, 16, 16));
        var col = RowPaint.TextColor(ctx, e, isCursor, selected, dropTarget, out var diff);
        bool onAccent = Paint.Dark && isCursor && ctx.IsActive && !dropTarget;
        if (diff is { } d) dc.DrawRectangle(Paint.B(d.Style(Paint.Dark).Color), null, new Rect(0, 0, 4, rect.Height));

        double fs = ctx.View == ViewMode.Compact ? 13 : 14;
        if (ctx.EditingPath == null || !VPath.Same(ctx.EditingPath, e.Path))
        {
            double nameX = cName.Left + 26;
            dc.PushClip(new RectangleGeometry(cName));
            var ft = Paint.Text(e.Name, fs, col, this);
            double top = cy - ft.Height / 2;
            if (tab.Filter.Length > 0 && !(tab.Search.Content && e.Deep))
            {
                var hl = Paint.Dark ? Color.FromArgb(80, 255, 200, 0) : Color.FromArgb(120, 255, 190, 0);
                foreach (var (a, b) in tab.Matcher.Ranges(e.Name))
                {
                    int s = Math.Clamp(a, 0, e.Name.Length), t = Math.Clamp(b, s, e.Name.Length);
                    if (t <= s) continue;
                    double x0 = s == 0 ? 0 : Paint.Text(e.Name[..s], fs, col, this).WidthIncludingTrailingWhitespace;
                    double x1 = Paint.Text(e.Name[..t], fs, col, this).WidthIncludingTrailingWhitespace;
                    dc.DrawRoundedRectangle(Paint.B(hl), null, new Rect(nameX + x0 - 1, top, x1 - x0 + 2, ft.Height), 2, 2);
                }
            }
            dc.DrawText(ft, new Point(nameX, top));
            if (e.Snippet.Length > 0)
            {
                var sc = onAccent ? Color.FromRgb(225, 225, 225) : Paint.Weak;
                var st = Paint.Text(e.Snippet, fs - 1.5, sc, this, Paint.Mono);
                dc.DrawText(st, new Point(nameX + ft.WidthIncludingTrailingWhitespace + 14, cy - st.Height / 2));
            }
            dc.Pop();
        }
        var dim = onAccent ? Color.FromRgb(225, 225, 225) : Paint.Weak;
        void Cell(Rect r, string s, bool right)
        {
            if (r.Width < 1 || s.Length == 0) return;
            var ft = Paint.Text(s, 12.5, dim, this);
            dc.PushClip(new RectangleGeometry(r));
            dc.DrawText(ft, new Point(right ? r.Right - ft.Width : r.Left, cy - ft.Height / 2));
            dc.Pop();
        }
        if (!e.IsDir && !e.IsUp) Cell(cExt, e.Ext, false);
        Cell(cSize, e.IsUp ? "" : e.IsDir ? "<DIR>" : Text.FmtSize(e.Size), true);
        Cell(cDate, e.IsUp ? "" : Text.FmtTime(e.Modified), false);
    }
}

/// <summary>One row of the grid view: GridCols cells with a thumbnail / large icon and the name below.</summary>
public sealed class GridRow : FrameworkElement
{
    public const double CellH = 116, CellW = 120;
    public RowContext? Ctx { get; set; }
    private int hoverCell = -1;

    public GridRow()
    {
        MouseMove += (_, e) =>
        {
            int c = CellAt(e.GetPosition(this).X);
            if (c != hoverCell)
            {
                hoverCell = c;
                InvalidateVisual();
            }
        };
        MouseLeave += (_, _) => { hoverCell = -1; InvalidateVisual(); };
        Loaded += (_, _) => Ctx?.Pane.Realized.Add(this);
        Unloaded += (_, _) => Ctx?.Pane.Realized.Remove(this);
        DataContextChanged += (_, _) => InvalidateVisual();
    }

    public int Row => DataContext is RowVm r ? r.Index : -1;

    /// <summary>View index of the cell at x, or -1.</summary>
    public int ViAt(double x)
    {
        if (Ctx == null || Row < 0) return -1;
        int vi = Row * Ctx.GridCols + CellAt(x);
        return vi < Ctx.Tab.View.Count ? vi : -1;
    }

    private int CellAt(double x)
    {
        int n = Math.Max(1, Ctx?.GridCols ?? 1);
        return Math.Clamp((int)(x / (RenderSize.Width / n)), 0, n - 1);
    }

    public Rect CellRect(int c)
    {
        int n = Math.Max(1, Ctx?.GridCols ?? 1);
        double cw = RenderSize.Width / n;
        var r = new Rect(c * cw, 0, cw, CellH);
        r.Inflate(-2, -2);
        return r;
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 100 : available.Width, CellH);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var ctx = Ctx;
        if (ctx == null || Row < 0) return;
        var tab = ctx.Tab;
        for (int c = 0; c < ctx.GridCols; c++)
        {
            int vi = Row * ctx.GridCols + c;
            if (vi >= tab.View.Count) break;
            var e = tab.Entries[tab.View[vi]];
            var rect = CellRect(c);
            bool isCursor = vi == tab.Cursor;
            bool selected = tab.Selected.Contains(e.Path);
            bool dropTarget = ctx.DropTarget != null && e.IsDir && VPath.Same(ctx.DropTarget, e.Path);
            if (dropTarget) dc.DrawRoundedRectangle(Paint.B(Paint.Mul(Paint.Accent, 0.5)), null, rect, 6, 6);
            else if (isCursor) dc.DrawRoundedRectangle(Paint.B(RowPaint.CursorColor(ctx.IsActive)), null, rect, 6, 6);
            else if (selected) dc.DrawRoundedRectangle(Paint.B(Paint.Mul(Paint.SelectedText, 0.13)), null, rect, 6, 6);
            else if (c == hoverCell) dc.DrawRoundedRectangle(Paint.B(Paint.Tint(10)), null, rect, 6, 6);
            // thumbnail (images) or a large shell icon
            var area = new Rect(rect.X + 4, rect.Y + 4, Math.Max(1, rect.Width - 8), 74);
            bool drawn = false;
            if (!e.IsDir && !e.IsUp && !Sftp.IsRemote(e.Path) && Thumbs.IsThumbnailable(e.Ext, e.Size))
            {
                if (Thumbs.Get(e.Path, InvalidateVisual) is { } img && img.Width > 0 && img.Height > 0)
                {
                    double k = Math.Min(1, Math.Min(area.Width / img.Width, area.Height / img.Height));
                    var sz = new Size(img.Width * k, img.Height * k);
                    dc.DrawImage(img, new Rect(area.X + (area.Width - sz.Width) / 2, area.Y + (area.Height - sz.Height) / 2, sz.Width, sz.Height));
                    drawn = true;
                }
            }
            if (!drawn && ShellIcons.Get(e.Ext, e.IsDir, large: true) is { } icon)
                dc.DrawImage(icon, new Rect(area.X + area.Width / 2 - 16, area.Y + area.Height / 2 - 16, 32, 32));
            var col = RowPaint.TextColor(ctx, e, isCursor, selected, dropTarget, out _);
            if (Paint.Dark && isCursor && ctx.IsActive && !dropTarget && !selected) col = Colors.White;
            if (ctx.EditingPath != null && VPath.Same(ctx.EditingPath, e.Path)) continue;
            var nameRect = new Rect(rect.X + 3, rect.Y + 80, Math.Max(1, rect.Width - 6), Math.Max(1, rect.Height - 82));
            var ft = Paint.Text(e.Name, 12.5, col, this);
            ft.MaxTextWidth = nameRect.Width;
            ft.MaxTextHeight = nameRect.Height;
            ft.TextAlignment = TextAlignment.Center;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(ft, nameRect.TopLeft);
        }
    }
}
