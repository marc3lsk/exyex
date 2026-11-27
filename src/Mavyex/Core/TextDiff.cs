using System.Text;
using DiffPlex;
using DiffPlex.Model;

namespace Mavyex.Core;

/// <summary>Kind of an aligned diff row.</summary>
public enum DiffKind { Same, Del, Ins, Chg }

/// <summary>One aligned side-by-side row: line indices into the left / right file, -1 = no line on that side.</summary>
public readonly record struct DiffRow(DiffKind Kind, int L, int R);

/// <summary>Pure text-compare logic of the diff window (no UI): decoding, aligned rows, hunks, inline char segments.</summary>
public static class TextDiff
{
    /// <summary>Files bigger than this are refused (the diff is computed in memory).</summary>
    public const int Max = 4 * 1024 * 1024;

    /// <summary>Lines longer than this (UTF-8 bytes, either side) are marked as changed as a whole: the char diff is quadratic-ish.</summary>
    public const int SegmentLimit = 1500;

    private static readonly UTF8Encoding Utf8Lossy = new(false, false);

    /// <summary>Bytes -> text (UTF-16 by BOM, otherwise UTF-8 lossy without BOM). Returns null and an error for
    /// binary (NUL in the first 8 KiB) or too large input.</summary>
    public static string? Decode(byte[] b, out string? error)
    {
        error = null;
        if (b.Length > Max)
        {
            error = $"File is larger than {Max / 1024 / 1024} MB";
            return null;
        }
        if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)))
        {
            // Odd trailing byte is dropped (chunks_exact in the original); lone surrogates become U+FFFD.
            int n = (b.Length - 2) & ~1;
            var enc = b[0] == 0xFF ? new UnicodeEncoding(false, false, false) : new UnicodeEncoding(true, false, false);
            return enc.GetString(b, 2, n);
        }
        int scan = Math.Min(b.Length, 8192);
        if (Array.IndexOf(b, (byte)0, 0, scan) >= 0)
        {
            error = "Binary file – text compare needs text files";
            return null;
        }
        int start = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        return Utf8Lossy.GetString(b, start, b.Length - start);
    }

    /// <summary>Rust <c>str::lines</c>: split at \n, strip one trailing \r per line, no extra empty line after a final newline.</summary>
    public static List<string> Lines(string s)
    {
        var out_ = new List<string>();
        int pos = 0;
        while (pos < s.Length)
        {
            int nl = s.IndexOf('\n', pos);
            int end = nl < 0 ? s.Length : nl;
            int e = end > pos && s[end - 1] == '\r' && nl >= 0 ? end - 1 : end;
            out_.Add(s[pos..e]);
            if (nl < 0) break;
            pos = nl + 1;
        }
        return out_;
    }

    /// <summary>Comparison key of a line: whitespace runs collapsed (and trimmed) when ws, lower-cased when ignoreCase.</summary>
    public static string Norm(string s, bool ws, bool ignoreCase)
    {
        var t = ws ? string.Join(' ', SplitWhitespace(s)) : s;
        return ignoreCase ? t.ToLowerInvariant() : t;
    }

    private static IEnumerable<string> SplitWhitespace(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            int st = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            if (i > st) yield return s[st..i];
        }
    }

    /// <summary>Hands DiffPlex pre-split pieces: the first call gets the old side, the second the new side. Joining
    /// and re-splitting would be ambiguous ([] vs [""]) and lines may contain a lone '\r'.</summary>
    private sealed class Preset(IReadOnlyList<string> a, IReadOnlyList<string> b) : IChunker
    {
        private int calls;
        public IReadOnlyList<string> Chunk(string text) => calls++ == 0 ? a : b;
    }

    /// <summary>Aligned side-by-side rows plus added / removed line counts. A block that both deletes and inserts is
    /// paired row by row (Chg), the longer side's remainder becomes Del / Ins rows.</summary>
    public static (List<DiffRow> Rows, int Added, int Removed) DiffRows(IReadOnlyList<string> left, IReadOnlyList<string> right, bool ws, bool ignoreCase)
    {
        var a = left.Select(s => Norm(s, ws, ignoreCase)).ToArray();
        var b = right.Select(s => Norm(s, ws, ignoreCase)).ToArray();
        var res = Differ.Instance.CreateDiffs("a", "b", false, false, new Preset(a, b));
        var rows = new List<DiffRow>(Math.Max(a.Length, b.Length));
        int added = 0, removed = 0, ia = 0, ib = 0;
        void Equal(int upToA)
        {
            while (ia < upToA) rows.Add(new DiffRow(DiffKind.Same, ia++, ib++));
        }
        foreach (var blk in res.DiffBlocks.OrderBy(x => x.DeleteStartA))
        {
            Equal(blk.DeleteStartA);
            ib = blk.InsertStartB; // equal runs have the same length on both sides, so this is a no-op sanity sync
            int dl = blk.DeleteCountA, il = blk.InsertCountB;
            removed += dl;
            added += il;
            for (int k = 0; k < Math.Max(dl, il); k++)
            {
                int l = k < dl ? ia + k : -1, r = k < il ? ib + k : -1;
                var kind = l >= 0 && r >= 0 ? DiffKind.Chg : l >= 0 ? DiffKind.Del : DiffKind.Ins;
                rows.Add(new DiffRow(kind, l, r));
            }
            ia += dl;
            ib += il;
        }
        Equal(a.Length);
        return (rows, added, removed);
    }

    /// <summary>Row index where each block of differences starts.</summary>
    public static List<int> Hunks(IReadOnlyList<DiffRow> rows)
    {
        var h = new List<int>();
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Kind != DiffKind.Same && (i == 0 || rows[i - 1].Kind == DiffKind.Same)) h.Add(i);
        return h;
    }

    /// <summary>Code points (not UTF-16 units) as diff pieces, so a surrogate pair is never split.</summary>
    private static string[] Chars(string s)
    {
        var l = new List<string>(s.Length);
        foreach (var r in s.EnumerateRunes()) l.Add(r.ToString());
        return [.. l];
    }

    /// <summary>Splits <paramref name="mine"/> into (text, changed) pieces by a character diff against
    /// <paramref name="other"/>; left = mine is the old side (deleted chars marked), else the new side (inserted marked).</summary>
    public static List<(string Text, bool Changed)> Segments(string mine, string other, bool left)
    {
        var (a, b) = left ? (mine, other) : (other, mine);
        if (Encoding.UTF8.GetByteCount(a) > SegmentLimit || Encoding.UTF8.GetByteCount(b) > SegmentLimit)
            return [(mine, true)];
        var res = Differ.Instance.CreateDiffs("a", "b", false, false, new Preset(Chars(a), Chars(b)));
        var pieces = left ? res.PiecesOld : res.PiecesNew;
        // Mark the changed pieces of my side, then merge runs with the same flag.
        var changed = new bool[pieces.Count];
        foreach (var blk in res.DiffBlocks)
        {
            int st = left ? blk.DeleteStartA : blk.InsertStartB, n = left ? blk.DeleteCountA : blk.InsertCountB;
            for (int k = 0; k < n; k++) changed[st + k] = true;
        }
        var out_ = new List<(string, bool)>();
        var sb = new StringBuilder();
        for (int i = 0; i < pieces.Count; i++)
        {
            if (i > 0 && changed[i] != changed[i - 1])
            {
                out_.Add((sb.ToString(), changed[i - 1]));
                sb.Clear();
            }
            sb.Append(pieces[i]);
        }
        if (pieces.Count > 0) out_.Add((sb.ToString(), changed[^1]));
        return out_;
    }

    /// <summary>Reads at most Max + 1 bytes of a local, SFTP or archive-inner file and decodes it. I/O errors come back
    /// as their message, decode errors prefixed with the file name (like the original).</summary>
    public static string ReadText(string p, out string? error)
    {
        error = null;
        byte[] bytes;
        try
        {
            if (Services.Sftp.IsRemote(p)) bytes = Services.Sftp.ReadHead(p, Max + 1);
            else if (Services.Archive.SplitInner(p) is var (zip, inner)) bytes = Services.Archive.ReadHead(zip, inner, Max + 1);
            else bytes = ReadLocalHead(p, Max + 1);
        }
        catch (Exception e)
        {
            error = e.Message;
            return "";
        }
        var s = Decode(bytes, out var de);
        if (s == null)
        {
            error = $"{VPath.FileName(p) ?? ""}: {de}";
            return "";
        }
        return s;
    }

    private static byte[] ReadLocalHead(string p, int max)
    {
        using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[(int)Math.Min(max, Math.Max(0, f.Length))];
        int n = 0;
        while (n < buf.Length)
        {
            int k = f.Read(buf, n, buf.Length - n);
            if (k == 0) break;
            n += k;
        }
        return n == buf.Length ? buf : buf[..n];
    }
}
