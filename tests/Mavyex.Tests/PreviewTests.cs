using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mavyex.Core;
using Mavyex.UI;
using Xunit;

namespace Mavyex.Tests;

public class PreviewTests
{
    /// <summary>WPF imaging (SharpVectors, RenderTargetBitmap) wants an STA thread.</summary>
    private static T Sta<T>(Func<T> f)
    {
        T result = default!;
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { result = f(); }
            catch (Exception e) { err = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw new Exception("STA work failed", err);
        return result;
    }

    private static string TempFile(string ext, byte[] data)
    {
        var p = Path.Combine(Path.GetTempPath(), $"mavyex-test-{Guid.NewGuid():N}.{ext}");
        File.WriteAllBytes(p, data);
        return p;
    }

    private static byte[] Pixels(BitmapSource b)
    {
        var c = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        var px = new byte[c.PixelWidth * c.PixelHeight * 4];
        c.CopyPixels(px, c.PixelWidth * 4, 0);
        return px;
    }

    [Fact]
    public void SvgRendersToImageAndKeepsSource()
    {
        var p = TempFile("svg", Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><rect width="40" height="20" fill="#f00"/></svg>"""));
        try
        {
            var d = Sta(() =>
            {
                var r = PreviewLoader.LoadSvg(p);
                return r is PvData.Svg s ? (s.W, s.H, s.Img.PixelWidth, s.Img.PixelHeight, Pixels(s.Img), s.Source) : default;
            });
            Assert.Equal((40, 20), (d.W, d.H));
            Assert.Equal(d.PixelWidth * 20, d.PixelHeight * 40); // aspect ratio kept
            Assert.Equal(320, d.PixelWidth);                    // target 1200, but the scale is capped at 8x
            bool red = false;
            for (int i = 0; i < d.Item5.Length; i += 4) red |= d.Item5[i + 2] > 200 && d.Item5[i + 1] < 50 && d.Item5[i + 3] > 200;
            Assert.True(red);
            Assert.Contains("<rect", d.Source);
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Theory]
    [InlineData("""width="40" height="20" """)]
    [InlineData("""width="40" height="20" viewBox="0 0 4 2" """)]
    [InlineData("""viewBox="0 0 40 20" """)]
    public void SvgViewportMapsToBitmap(string attrs)
    {
        // right half red: the left half must stay transparent, i.e. the drawing is placed at the viewport origin
        bool viewBox = attrs.Contains("0 0 4 2");
        var rect = viewBox ? """<rect x="2" width="2" height="2" fill="#f00"/>""" : """<rect x="20" width="20" height="20" fill="#f00"/>""";
        var svg = Encoding.UTF8.GetBytes($"""<svg xmlns="http://www.w3.org/2000/svg" {attrs}>{rect}</svg>""");
        var (w, h, px, pw) = Sta(() =>
        {
            var r = ImageDecode.RenderSvg(svg, 80);
            return (r.W, r.H, Pixels(r.Img), r.Img.PixelWidth);
        });
        Assert.Equal((40, 20), (w, h));
        Assert.Equal(80, pw);
        int row = 20 * pw * 4;
        Assert.True(px[row + 10 * 4 + 3] < 20);                                // left: transparent
        Assert.True(px[row + 70 * 4 + 2] > 200 && px[row + 70 * 4 + 3] > 200); // right: red
    }

    [Fact]
    public void SvgSizeFallbacks()
    {
        Assert.Equal((100.0, 100.0), ImageDecode.SvgSize(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")));
        Assert.Equal((96.0, 48.0), ImageDecode.SvgSize(Encoding.UTF8.GetBytes("<svg width=\"1in\" viewBox=\"0 0 10 5\"/>")));
        Assert.Equal((100.0, 100.0), ImageDecode.SvgSize(Encoding.UTF8.GetBytes("not xml")));
    }

    [Fact]
    public void BrokenSvgFallsBackToSourceText()
    {
        var p = TempFile("svg", Encoding.UTF8.GetBytes("<svg\tbroken"));
        try
        {
            var r = Sta(() => PreviewLoader.LoadSvg(p));
            var t = Assert.IsType<PvData.Text>(r);
            Assert.StartsWith("<!-- Can't render SVG: ", t.Body);
            Assert.EndsWith("-->\n<svg    broken", t.Body);
            Assert.False(t.Truncated);
        }
        finally
        {
            File.Delete(p);
        }
    }

    [Fact]
    public void ImagesAreDownscaledAndKeepTheirSize()
    {
        var png = Sta(() =>
        {
            var src = BitmapSource.Create(400, 100, 72, 72, PixelFormats.Bgra32, null, new byte[400 * 100 * 4], 400 * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        });
        var (iw, ih, w, h, dpi) = Sta(() =>
        {
            var r = ImageDecode.Decode(() => new MemoryStream(png), 192);
            return (r.Img.PixelWidth, r.Img.PixelHeight, r.W, r.H, r.Img.DpiX);
        });
        Assert.Equal((400, 100), (w, h));
        Assert.Equal((192, 48), (iw, ih));
        Assert.Equal(96, dpi, 1);
        var small = Sta(() => ImageDecode.Decode(() => new MemoryStream(png), 1600).Img.PixelWidth);
        Assert.Equal(400, small); // never upscaled
        Assert.Throws<Exception>(() => Sta(() => ImageDecode.Decode(() => new MemoryStream([1, 2, 3]), 192)));

        var p = TempFile("png", png);
        try
        {
            var t = Sta(() => Thumbs.Decode(p)?.PixelWidth);
            Assert.Equal(192, t);
            var e = new Entry { Name = "x.png", Path = p, Ext = "png", Size = png.Length };
            var pv = Sta(() => PreviewLoader.Load(e) is PvData.Image i ? (i.W, i.H) : default);
            Assert.Equal((400, 100), pv);
        }
        finally
        {
            File.Delete(p);
        }
        Assert.Null(Sta(() => Thumbs.Decode(@"C:\nonexistent-mavyex\a.png")));
    }

    [Fact]
    public void BytesPreviewText()
    {
        Assert.Equal(new PvData.Note("Empty file"), PreviewLoader.BytesPreview([], 0));
        var t = Assert.IsType<PvData.Text>(PreviewLoader.BytesPreview(Encoding.UTF8.GetBytes("a\tb\r\nc\n\nd"), 9));
        Assert.Equal("a    b\nc\n\nd\n", t.Body);
        Assert.False(t.Truncated);
        // shorter than the file: truncated
        Assert.True(Assert.IsType<PvData.Text>(PreviewLoader.BytesPreview(Encoding.UTF8.GetBytes("x"), 100)).Truncated);
        // invalid UTF-8 becomes U+FFFD (from_utf8_lossy)
        Assert.Equal("a\uFFFDb\n", Assert.IsType<PvData.Text>(PreviewLoader.BytesPreview([0x61, 0xFF, 0x62], 3)).Body);
        // line limit
        var many = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("l\n", 1500)));
        var lim = Assert.IsType<PvData.Text>(PreviewLoader.BytesPreview(many, many.Length));
        Assert.True(lim.Truncated);
        Assert.Equal(PreviewLoader.TextLines, lim.Body.Count(c => c == '\n'));
    }

    [Fact]
    public void RustLinesSemantics()
    {
        Assert.Equal(["a", "b"], PreviewLoader.RustLines("a\nb\n"));
        Assert.Equal(["a", "", "b"], PreviewLoader.RustLines("a\r\n\nb"));
        Assert.Equal(["a\r"], PreviewLoader.RustLines("a\r"));
        Assert.Empty(PreviewLoader.RustLines(""));
    }

    [Fact]
    public void BinaryGetsHexDump()
    {
        var b = new byte[400];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)i;
        var h = Assert.IsType<PvData.Hex>(PreviewLoader.BytesPreview(b, 400));
        var lines = h.Dump.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(24, lines.Length);
        Assert.Equal("000000  00 01 02 03 04 05 06 07 08 09 0a 0b 0c 0d 0e 0f  ................", lines[0]);
        Assert.Equal("000020  20 21 22 23 24 25 26 27 28 29 2a 2b 2c 2d 2e 2f   !\"#$%&'()*+,-./", lines[2]);
        // short last row is padded so the ASCII column stays aligned
        Assert.Equal("000000  4d 5a 00" + new string(' ', 47 - 8) + "  MZ.\n", PreviewLoader.HexDump([0x4d, 0x5a, 0]));
    }

    [Fact]
    public void FolderSummarySortsAndCounts()
    {
        List<Entry> v =
        [
            new() { Name = "b.txt", Size = 10 },
            new() { Name = "Zdir", IsDir = true },
            new() { Name = "A.txt", Size = 5 },
            new() { Name = "adir", IsDir = true },
            new() { Name = "..", IsDir = true, IsUp = true },
        ];
        var f = PreviewLoader.FolderSummary(v.Where(e => !e.IsUp).ToList());
        Assert.Equal((2, 2, 15L), (f.Dirs, f.Files, f.Bytes));
        Assert.Equal(["📁 adir", "📁 Zdir", "A.txt", "b.txt"], f.Names);
        var many = Enumerable.Range(0, 100).Select(i => new Entry { Name = $"f{i:000}" }).ToList();
        Assert.Equal(60, PreviewLoader.FolderSummary(many).Names.Count);
    }

    [Fact]
    public void LoadLocalFilesAndFolders()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mavyex-pv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello\n");
        File.WriteAllBytes(Path.Combine(dir, "big.png"), []);
        try
        {
            var folder = Assert.IsType<PvData.Folder>(PreviewLoader.Load(new Entry { Name = "x", Path = dir, IsDir = true }));
            Assert.Equal((1, 2), (folder.Dirs, folder.Files));
            var txt = Assert.IsType<PvData.Text>(PreviewLoader.Load(new Entry { Name = "a.txt", Path = Path.Combine(dir, "a.txt"), Ext = "txt", Size = 6 }));
            Assert.Equal("hello\n", txt.Body);
            var big = PreviewLoader.Load(new Entry { Name = "big.png", Path = Path.Combine(dir, "big.png"), Ext = "png", Size = 61L * 1024 * 1024 });
            Assert.Equal(new PvData.Note("Image is too large to preview"), big);
            Assert.IsType<PvData.Note>(PreviewLoader.Load(new Entry { Name = "gone", Path = Path.Combine(dir, "gone.txt"), Ext = "txt" }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MetaLine()
    {
        Assert.Equal("1.5 KB", PreviewLoader.Meta(new Entry { Size = 1536 }));
        Assert.Equal("", PreviewLoader.Meta(new Entry { IsDir = true }));
        var t = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal($"0 B  ·  {Text.FmtTime(t)}", PreviewLoader.Meta(new Entry { Modified = t }));
    }
}

public class ThumbsTests
{
    [Theory]
    [InlineData("png", 100, true)]
    [InlineData("tiff", 100, true)]
    [InlineData("svg", 40L * 1024 * 1024, true)]
    [InlineData("jpg", 40L * 1024 * 1024 + 1, false)]
    [InlineData("txt", 1, false)]
    [InlineData("PNG", 1, false)] // callers pass the lower-case Entry.Ext
    public void Thumbnailable(string ext, long size, bool expected) => Assert.Equal(expected, Thumbs.IsThumbnailable(ext, size));
}

public class PreviewPanelTests
{
    [Fact]
    public void PanelBuildsAndAcceptsEntries()
    {
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                var p = new PreviewPanel(); // parses its ScrollBar style
                p.ShowEntry(null);
                var e = new Entry { Name = "a.txt", Path = @"C:\nonexistent-mavyex\a.txt", Ext = "txt" };
                p.ShowEntry(e);
                p.ShowEntry(e); // same entry: no reload
                p.ShowEntry(new Entry { Name = "..", Path = @"C:\", IsDir = true, IsUp = true });
                bool closed = false;
                p.CloseRequested += () => closed = true;
                p.RaiseClose();
                Assert.True(closed);
            }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(err);
    }
}

public class ShellIconsTests
{
    [Fact]
    public void IconsAreLoadedAndCached()
    {
        var (small, large, again, dir) = (default(ImageSource), default(ImageSource), default(ImageSource), default(ImageSource));
        var t = new Thread(() =>
        {
            small = ShellIcons.Get("txt", false);
            large = ShellIcons.Get("txt", false, true);
            again = ShellIcons.Get("txt", false);
            dir = ShellIcons.Get("", true);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        var s = Assert.IsAssignableFrom<BitmapSource>(small);
        Assert.True(s.PixelWidth >= 16); // 16 px at 96 dpi, more on scaled displays
        Assert.True(Assert.IsAssignableFrom<BitmapSource>(large).PixelWidth >= 32);
        Assert.Same(small, again);
        Assert.NotNull(dir);
        Assert.True(s.IsFrozen);
    }
}
