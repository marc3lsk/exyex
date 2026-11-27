using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using SharpVectors.Converters;
using SharpVectors.Dom;
using SharpVectors.Renderers.Wpf;

namespace Mavyex.UI;

/// <summary>Raster / SVG decoding to frozen 96-dpi bitmaps, shared by Thumbs and Quick Look. Safe on worker threads
/// (run it via <see cref="Worker.RunSta"/> – SharpVectors and RenderTargetBitmap want an STA thread).</summary>
internal static class ImageDecode
{
    /// <summary>Decodes an image so that it fits into <paramref name="max"/>×<paramref name="max"/> (never upscaled);
    /// also returns the original size. Icons use their largest frame. Throws on undecodable data.</summary>
    public static (BitmapSource Img, int W, int H) Decode(Func<Stream> open, int max)
    {
        int w, h;
        bool multi;
        using (var s = open())
        {
            // DelayCreation: only the header is parsed here, the pixels are decoded (scaled) below
            var dec = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            multi = dec is IconBitmapDecoder && dec.Frames.Count > 1;
            if (!multi)
            {
                w = dec.Frames[0].PixelWidth;
                h = dec.Frames[0].PixelHeight;
            }
            else w = h = 0;
        }
        if (multi)
        {
            using var s = open();
            var dec = BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var f = dec.Frames.OrderByDescending(x => (long)x.PixelWidth * x.PixelHeight).First();
            w = f.PixelWidth;
            h = f.PixelHeight;
            BitmapSource src = f;
            double k = Scale(w, h, max);
            if (k < 1) src = new TransformedBitmap(f, new ScaleTransform(k, k));
            return (Normalize(src), w, h);
        }
        using (var s = open())
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = s;
            double k = Scale(w, h, max);
            // DecodePixelWidth/Height lets WIC decode scaled (JPEG: much faster); the other side keeps the aspect
            if (k < 1)
            {
                if (w >= h) bi.DecodePixelWidth = Math.Max(1, (int)Math.Round(w * k));
                else bi.DecodePixelHeight = Math.Max(1, (int)Math.Round(h * k));
            }
            bi.EndInit();
            return (Normalize(bi), w, h);
        }
    }

    private static double Scale(int w, int h, int max) => Math.Max(w, h) > max ? (double)max / Math.Max(w, h) : 1;

    /// <summary>Frozen copy at 96 dpi so that WPF shows one image pixel per DIP (72-dpi JPEGs would grow by 33 %).</summary>
    private static BitmapSource Normalize(BitmapSource src)
    {
        if (Math.Abs(src.DpiX - 96) > 0.5 || Math.Abs(src.DpiY - 96) > 0.5)
        {
            var fmt = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int stride = fmt.PixelWidth * 4;
            var px = new byte[stride * fmt.PixelHeight];
            fmt.CopyPixels(px, stride, 0);
            src = BitmapSource.Create(fmt.PixelWidth, fmt.PixelHeight, 96, 96, PixelFormats.Bgra32, null, px, stride);
        }
        else if (src is not BitmapImage) src = new WriteableBitmap(src); // materialize (drops the decoder chain)
        src.Freeze();
        return src;
    }

    /// <summary>Renders SVG data so that its longer side is <paramref name="target"/> pixels; also returns the nominal
    /// size (preview.rs render_svg). Throws with a readable message when the SVG can't be parsed.</summary>
    public static (BitmapSource Img, int W, int H) RenderSvg(byte[] data, double target)
    {
        var (w, h) = SvgSize(data);
        var settings = new WpfDrawingSettings
        {
            IncludeRuntime = false,
            TextAsGeometry = false,
            // like resvg loading from memory: no external files / URLs
            ExternalResourcesAccessMode = ExternalResourcesAccessModes.Ignore,
        };
        DrawingGroup drawing;
        using (var reader = new FileSvgReader(settings, false))
        {
            drawing = reader.Read(new MemoryStream(data)) ?? throw new InvalidDataException("not an SVG document");
        }
        w = Math.Max(w, 1);
        h = Math.Max(h, 1);
        double scale = Math.Clamp(target / Math.Max(w, h), 0.01, 8.0);
        int pw = Math.Max(1, (int)Math.Ceiling(w * scale)), ph = Math.Max(1, (int)Math.Ceiling(h * scale));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, pw, ph)));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(drawing);
        }
        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return (rtb, (int)Math.Round(w), (int)Math.Round(h));
    }

    /// <summary>Nominal SVG size from the root's width/height (absolute units), else its viewBox, else 100×100
    /// (usvg's defaults).</summary>
    internal static (double W, double H) SvgSize(byte[] data)
    {
        try
        {
            using var xr = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element) continue;
                double[]? vb = null;
                var vbs = xr.GetAttribute("viewBox");
                if (vbs != null)
                {
                    var p = vbs.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 4 && p.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                        vb = p.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                }
                double? w = Length(xr.GetAttribute("width")), h = Length(xr.GetAttribute("height"));
                if (vb is { } v && v[2] > 0 && v[3] > 0)
                {
                    // one side given: the other follows the viewBox aspect
                    if (w is { } ww && h is null) h = ww * v[3] / v[2];
                    if (h is { } hh && w is null) w = hh * v[2] / v[3];
                    return (w ?? v[2], h ?? v[3]);
                }
                return (w ?? 100, h ?? 100);
            }
        }
        catch (XmlException) { }
        return (100, 100);
    }

    private static double? Length(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.EndsWith('%')) return null;
        (string unit, double k)[] units = [("px", 1), ("pt", 4.0 / 3), ("pc", 16), ("mm", 96 / 25.4), ("cm", 96 / 2.54), ("in", 96), ("em", 16), ("ex", 8)];
        double mul = 1;
        foreach (var (u, k) in units)
        {
            if (s.EndsWith(u, StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^u.Length].Trim();
                mul = k;
                break;
            }
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v * mul : null;
    }

    /// <summary>Lossy UTF-8 like Rust's String::from_utf8_lossy (invalid sequences become U+FFFD).</summary>
    public static string Utf8Lossy(byte[] b, int count = -1) => new UTF8Encoding(false, false).GetString(b, 0, count < 0 ? b.Length : count);
}

/// <summary>Background threads for WPF imaging work.</summary>
internal static class Worker
{
    /// <summary>Runs <paramref name="work"/> on a new background STA thread (Rust spawned a thread per job as well).
    /// The thread's dispatcher, if WPF created one, is shut down afterwards so it doesn't linger.</summary>
    public static void RunSta(Action work)
    {
        var t = new Thread(() =>
        {
            try { work(); }
            finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
        })
        { IsBackground = true, Name = "mavyex-imaging" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}
