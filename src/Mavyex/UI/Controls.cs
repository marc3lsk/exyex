using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Shape = System.Windows.Shapes.Shape;
using ShapePath = System.Windows.Shapes.Path;
using Mavyex.Core;

namespace Mavyex.UI;

/// <summary>TextBlock that marks character ranges [start, end) of <see cref="Source"/> (HlFg / HlBg), e.g. search matches.</summary>
public sealed class HighlightText : TextBlock
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(HighlightText), new PropertyMetadata("", (d, _) => ((HighlightText)d).Rebuild()));

    public static readonly DependencyProperty RangesProperty = DependencyProperty.Register(
        nameof(Ranges), typeof(IReadOnlyList<(int Start, int End)>), typeof(HighlightText),
        new PropertyMetadata(null, (d, _) => ((HighlightText)d).Rebuild()));

    public string Source
    {
        get => (string)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IReadOnlyList<(int Start, int End)>? Ranges
    {
        get => (IReadOnlyList<(int Start, int End)>?)GetValue(RangesProperty);
        set => SetValue(RangesProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();
        var text = Source ?? "";
        int pos = 0;
        foreach (var (s, e) in Ranges ?? [])
        {
            int start = Math.Clamp(s, pos, text.Length), end = Math.Clamp(e, start, text.Length);
            if (end <= start) continue;
            int len = end - start;
            if (start > pos) Inlines.Add(new Run(text[pos..start]));
            var mark = new Run(text.Substring(start, len));
            mark.SetResourceReference(TextElement.ForegroundProperty, "HlFg");
            mark.SetResourceReference(TextElement.BackgroundProperty, "HlBg");
            Inlines.Add(mark);
            pos = start + len;
        }
        if (pos < text.Length) Inlines.Add(new Run(text[pos..]));
    }
}

/// <summary>Vector icons for the top-bar buttons: exactly centered, unlike font glyphs.</summary>
internal static class Icons
{
    public enum Kind { Plus, Gear, System, Sun, Moon }

    public static UIElement Make(Kind kind)
    {
        var g = new Grid { Width = 24, Height = 24, SnapsToDevicePixels = false };
        ShapePath Stroke(string data, double t)
        {
            var p = new ShapePath { Data = Geometry.Parse(data), StrokeThickness = t, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            p.SetResourceReference(Shape.StrokeProperty, "Fg");
            g.Children.Add(p);
            return p;
        }
        ShapePath Fill(Geometry geo)
        {
            var p = new ShapePath { Data = geo };
            p.SetResourceReference(Shape.FillProperty, "Fg");
            g.Children.Add(p);
            return p;
        }
        static string Rays(double r1, double r2)
        {
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                double c = Math.Cos(a), s = Math.Sin(a);
                sb.Append(System.FormattableString.Invariant($"M {12 + c * r1:0.##},{12 + s * r1:0.##} L {12 + c * r2:0.##},{12 + s * r2:0.##} "));
            }
            return sb.ToString();
        }
        switch (kind)
        {
            case Kind.Plus:
                Stroke("M 12,5 L 12,19 M 5,12 L 19,12", 1.8);
                break;
            case Kind.Gear:
                Stroke(Rays(5.5, 8.6), 2.8).StrokeStartLineCap = PenLineCap.Flat;
                Stroke("M 6.4,12 A 5.6,5.6 0 1 1 17.6,12 A 5.6,5.6 0 1 1 6.4,12 Z", 2.0);
                Stroke("M 10,12 A 2,2 0 1 1 14,12 A 2,2 0 1 1 10,12 Z", 1.4);
                break;
            case Kind.System:
                Stroke("M 5,12 A 7,7 0 1 1 19,12 A 7,7 0 1 1 5,12 Z", 1.6);
                Fill(Geometry.Parse("M 12,5 A 7,7 0 0 0 12,19 Z"));
                break;
            case Kind.Sun:
                Fill(new EllipseGeometry(new Point(12, 12), 3.8, 3.8));
                Stroke(Rays(6.2, 8.6), 1.6);
                break;
            case Kind.Moon:
                Fill(new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(12, 12), 7.5, 7.5),
                    new EllipseGeometry(new Point(15.6, 9), 6.2, 6.2)));
                break;
        }
        // drawn on a 24-unit grid, shown at 18 px (buttons are 32 px)
        return new Viewbox { Width = 18, Height = 18, Child = g };
    }
}
