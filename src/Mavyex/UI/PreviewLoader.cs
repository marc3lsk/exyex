using System.Globalization;
using System.Text;
using System.Windows.Media.Imaging;
using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

/// <summary>What Quick Look shows for one entry (preview.rs PvData).</summary>
internal abstract record PvData
{
    public sealed record Text(string Body, bool Truncated) : PvData;
    /// <summary>Decoded (possibly downscaled) image plus its original size.</summary>
    public sealed record Image(BitmapSource Img, int W, int H) : PvData;
    /// <summary>Rendered vector image plus its XML source (the user can switch between them).</summary>
    public sealed record Svg(BitmapSource Img, int W, int H, string Source) : PvData;
    public sealed record Folder(int Dirs, int Files, long Bytes, List<string> Names) : PvData;
    public sealed record Hex(string Dump) : PvData;
    public sealed record Note(string Message) : PvData;
}

/// <summary>Blocking Quick Look loader (preview.rs load) – runs on a worker thread.</summary>
internal static class PreviewLoader
{
    public const int TextBytes = 64 * 1024;
    public const int TextLines = 1200;
    public const long ImageMaxFile = 60L * 1024 * 1024;
    public const int RemoteImageMax = 8 * 1024 * 1024;
    public const int SvgMaxFile = 8 * 1024 * 1024;
    public const int ImageMaxSide = 1600;
    public static readonly string[] ImageExts = ["png", "jpg", "jpeg", "gif", "bmp", "webp", "ico"];

    public static PvData Load(Entry e)
    {
        var path = e.Path;
        try
        {
            if (Sftp.IsRemote(path))
            {
                if (e.IsDir) return FolderSummary(Sftp.List(path));
                if (ImageExts.Contains(e.Ext) && e.Size <= RemoteImageMax)
                {
                    var b = Sftp.ReadHead(path, RemoteImageMax);
                    return DecodeImage(() => new MemoryStream(b));
                }
                return BytesPreview(Sftp.ReadHead(path, TextBytes), e.Size);
            }
            if (Archive.Split(path) is { } a)
            {
                if (e.IsDir || a.Inner.Length == 0) return FolderSummary(Archive.List(a.Zip, a.Inner));
                return BytesPreview(Archive.ReadHead(a.Zip, a.Inner, TextBytes), e.Size);
            }
            if (e.IsDir) return FolderSummary(Fs.ReadDir(path));
            if (e.Ext == "svg") return LoadSvg(path);
            if (ImageExts.Contains(e.Ext))
            {
                if (e.Size > ImageMaxFile) return new PvData.Note("Image is too large to preview");
                return DecodeImage(() => OpenShared(path));
            }
            byte[] head;
            using (var f = OpenShared(path)) head = ReadUpTo(f, TextBytes);
            return BytesPreview(head, e.Size);
        }
        catch (Exception err) when (err is not OutOfMemoryException)
        {
            // Rust: PvData::Note(err.to_string()) for every io / listing / remote error
            return new PvData.Note(err.Message);
        }
    }

    private static FileStream OpenShared(string p) => new(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal static byte[] ReadUpTo(Stream s, int max)
    {
        var buf = new byte[max];
        int n = 0, r;
        while (n < max && (r = s.Read(buf, n, max - n)) > 0) n += r;
        return n == max ? buf : buf[..n];
    }

    private static PvData DecodeImage(Func<Stream> open)
    {
        try
        {
            var (img, w, h) = ImageDecode.Decode(open, ImageMaxSide);
            return new PvData.Image(img, w, h);
        }
        catch (Exception err) when (err is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException
                                        or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return new PvData.Note($"Can't decode image: {err.Message}");
        }
    }

    internal static PvData LoadSvg(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PvData.Note(e.Message);
        }
        if (data.Length > SvgMaxFile) return new PvData.Note("SVG is too large to preview");
        var source = ImageDecode.Utf8Lossy(data).Replace("\t", "    ");
        try
        {
            var (img, w, h) = ImageDecode.RenderSvg(data, 1200);
            return new PvData.Svg(img, w, h, source);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // text preview that explains why the richer view is missing
            return new PvData.Text($"<!-- Can't render SVG: {e.Message} -->\n{source}", false);
        }
    }

    internal static PvData.Folder FolderSummary(IReadOnlyList<Entry> v)
    {
        int dirs = v.Count(e => e.IsDir);
        int files = v.Count(e => !e.IsDir && !e.IsUp);
        long bytes = v.Where(e => !e.IsDir).Sum(e => e.Size);
        var names = v.Where(e => !e.IsUp)
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .Take(60)
            .Select(e => e.IsDir ? $"📁 {e.Name}" : e.Name)
            .ToList();
        return new PvData.Folder(dirs, files, bytes, names);
    }

    internal static PvData BytesPreview(byte[] b, long fullSize)
    {
        if (b.Length == 0) return new PvData.Note("Empty file");
        bool binary = Array.IndexOf(b, (byte)0, 0, Math.Min(b.Length, 8192)) >= 0;
        if (binary) return new PvData.Hex(HexDump(b));
        var text = ImageDecode.Utf8Lossy(b);
        var outSb = new StringBuilder();
        int lines = 0;
        foreach (var l in RustLines(text))
        {
            if (lines >= TextLines) break;
            outSb.Append(l.Replace("\t", "    ")).Append('\n');
            lines++;
        }
        return new PvData.Text(outSb.ToString(), lines >= TextLines || b.Length < fullSize);
    }

    /// <summary>First 24 rows of 16 bytes: "000010  4d 5a ...  MZ..".</summary>
    internal static string HexDump(byte[] b)
    {
        var s = new StringBuilder();
        for (int row = 0; row < 24 && row * 16 < b.Length; row++)
        {
            var chunk = b.AsSpan(row * 16, Math.Min(16, b.Length - row * 16));
            var hex = string.Join(' ', chunk.ToArray().Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            var asc = new string(chunk.ToArray().Select(x => x is >= 32 and < 127 ? (char)x : '.').ToArray());
            s.Append((row * 16).ToString("x6", CultureInfo.InvariantCulture)).Append("  ").Append(hex.PadRight(47)).Append("  ").Append(asc).Append('\n');
        }
        return s.ToString();
    }

    /// <summary>Rust str::lines: split on '\n' or "\r\n", no final empty line (a bare trailing '\r' stays).</summary>
    internal static IEnumerable<string> RustLines(string text)
    {
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            int end = nl < 0 ? text.Length : nl;
            var line = text[start..end];
            if (nl >= 0 && line.EndsWith('\r')) line = line[..^1];
            yield return line;
            if (nl < 0) break;
            start = nl + 1;
        }
    }

    /// <summary>"12.3 KB  ·  2024-05-01 13:45" header line (size only for files, time only when known).</summary>
    internal static string Meta(Entry e)
    {
        var meta = new List<string>();
        if (!e.IsDir) meta.Add(Text.FmtSize(e.Size));
        if (e.Modified is not null) meta.Add(Text.FmtTime(e.Modified));
        return string.Join("  ·  ", meta);
    }
}
