using System.IO;
using System.Text;
using Mavyex.Core;
using Xunit;

namespace Mavyex.Tests;

public class TextDiffTests
{
    private static List<string> L(string s) => TextDiff.Lines(s);
    private static DiffKind[] Kinds(List<DiffRow> rows) => rows.Select(r => r.Kind).ToArray();
    private static string Marked(List<(string Text, bool Changed)> s) => string.Concat(s.Where(x => x.Changed).Select(x => x.Text));

    // ---- ported from textdiff.rs ----

    [Fact]
    public void AlignsChangesInsertsAndDeletes()
    {
        var (rows, added, removed) = TextDiff.DiffRows(L("a\nb\nc\nd"), L("a\nB\nc\nx\nd"), false, false);
        Assert.Equal([DiffKind.Same, DiffKind.Chg, DiffKind.Same, DiffKind.Ins, DiffKind.Same], Kinds(rows));
        Assert.Equal((2, 1), (added, removed));
    }

    [Fact]
    public void WhitespaceAndCaseCanBeIgnored()
    {
        var (l, r) = (L("Hello   world\nfoo"), L("hello world\nfoo"));
        Assert.Equal(1, TextDiff.DiffRows(l, r, false, false).Rows.Count(x => x.Kind != DiffKind.Same));
        Assert.All(TextDiff.DiffRows(l, r, true, true).Rows, x => Assert.Equal(DiffKind.Same, x.Kind));
    }

    [Fact]
    public void InlineSegmentsMarkOnlyTheChangedChars()
    {
        Assert.Equal("rd", Marked(TextDiff.Segments("color red", "color blue", true)));
        Assert.Null(TextDiff.Decode([0, 1, 2], out var err));
        Assert.NotNull(err);
        Assert.Equal("hi", TextDiff.Decode([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'], out _));
    }

    // ---- pairing ----

    [Fact]
    public void ReplaceBlockPairsRowsAndSpillsTheRest()
    {
        // two lines replaced by three: 2 x Chg + 1 x Ins, indices paired in order
        var (rows, added, removed) = TextDiff.DiffRows(L("x\na\nb\ny"), L("x\nA\nB\nC\ny"), false, false);
        Assert.Equal([DiffKind.Same, DiffKind.Chg, DiffKind.Chg, DiffKind.Ins, DiffKind.Same], Kinds(rows));
        Assert.Equal(new DiffRow(DiffKind.Chg, 1, 1), rows[1]);
        Assert.Equal(new DiffRow(DiffKind.Ins, -1, 3), rows[3]);
        Assert.Equal(new DiffRow(DiffKind.Same, 3, 4), rows[4]);
        Assert.Equal((3, 2), (added, removed));

        var (rows2, _, _) = TextDiff.DiffRows(L("x\na\nb\nc\ny"), L("x\nA\ny"), false, false);
        Assert.Equal([DiffKind.Same, DiffKind.Chg, DiffKind.Del, DiffKind.Del, DiffKind.Same], Kinds(rows2));
        Assert.Equal(new DiffRow(DiffKind.Del, 3, -1), rows2[3]);
    }

    [Fact]
    public void EmptyFilesAndSingleEmptyLineDiffer()
    {
        Assert.Empty(TextDiff.DiffRows([], [], false, false).Rows);
        var (rows, added, removed) = TextDiff.DiffRows([], [""], false, false);
        Assert.Equal([DiffKind.Ins], Kinds(rows));
        Assert.Equal((1, 0), (added, removed));
        Assert.Equal([DiffKind.Del, DiffKind.Del], Kinds(TextDiff.DiffRows(L("a\nb"), [], false, false).Rows));
    }

    [Fact]
    public void IdenticalFilesHaveNoHunks()
    {
        var (rows, added, removed) = TextDiff.DiffRows(L("a\nb"), L("a\nb"), false, false);
        Assert.Equal(2, rows.Count);
        Assert.Empty(TextDiff.Hunks(rows));
        Assert.Equal((0, 0), (added, removed));
    }

    [Fact]
    public void HunksStartAtEachBlock()
    {
        var (rows, _, _) = TextDiff.DiffRows(L("a\nb\nc\nd\ne\nf"), L("A\nB\nc\nd\nE\nf\ng"), false, false);
        Assert.Equal([0, 4, 6], TextDiff.Hunks(rows));
    }

    // ---- ignore options ----

    [Fact]
    public void NormCollapsesAndTrimsWhitespaceAndLowercases()
    {
        Assert.Equal("a b c", TextDiff.Norm("  a \t b  c  ", true, false));
        Assert.Equal("  a  b", TextDiff.Norm("  A  B", false, true));
        Assert.Equal(" X", TextDiff.Norm(" X", false, false));
    }

    [Fact]
    public void IgnoreOptionsAreIndependent()
    {
        var (l, r) = (L("a  b"), L("A b"));
        Assert.Equal(DiffKind.Chg, TextDiff.DiffRows(l, r, true, false).Rows[0].Kind);
        Assert.Equal(DiffKind.Chg, TextDiff.DiffRows(l, r, false, true).Rows[0].Kind);
        Assert.Equal(DiffKind.Same, TextDiff.DiffRows(l, r, true, true).Rows[0].Kind);
    }

    // ---- char segments ----

    [Fact]
    public void RightSideMarksInsertedChars()
    {
        var s = TextDiff.Segments("color blue", "color red", false);
        Assert.Equal("blu", Marked(s));
        Assert.Equal("color blue", string.Concat(s.Select(x => x.Text)));
        // merged runs alternate flags
        for (int i = 1; i < s.Count; i++) Assert.NotEqual(s[i - 1].Changed, s[i].Changed);
    }

    [Fact]
    public void SegmentsOfEqualOrEmptyText()
    {
        Assert.Equal([("same", false)], TextDiff.Segments("same", "same", true));
        Assert.Equal([("new", true)], TextDiff.Segments("new", "", false));
        Assert.Empty(TextDiff.Segments("", "old", false));
    }

    [Fact]
    public void LongLinesAreMarkedWhole()
    {
        var big = new string('x', TextDiff.SegmentLimit + 1);
        Assert.Equal([("short", true)], TextDiff.Segments("short", big, true));
    }

    [Fact]
    public void SurrogatePairsAreNotSplit()
    {
        var s = TextDiff.Segments("a\U0001F600b", "a\U0001F601b", true);
        Assert.Equal("\U0001F600", Marked(s));
    }

    // ---- decoding / lines ----

    [Fact]
    public void DecodeHandlesUtf16AndLimits()
    {
        var le = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("hé")).Append((byte)0x41).ToArray(); // odd byte dropped
        Assert.Equal("hé", TextDiff.Decode(le, out _));
        var be = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes("ok")).ToArray();
        Assert.Equal("ok", TextDiff.Decode(be, out _));

        Assert.Null(TextDiff.Decode(new byte[TextDiff.Max + 1], out var err));
        Assert.Equal("File is larger than 4 MB", err);
        Assert.Null(TextDiff.Decode([(byte)'a', 0], out err));
        Assert.Equal("Binary file – text compare needs text files", err);
        // NUL only after the first 8 KiB is not "binary"
        var late = Enumerable.Repeat((byte)'a', 8192).Append((byte)0).ToArray();
        Assert.NotNull(TextDiff.Decode(late, out _));
        Assert.Equal("a�b", TextDiff.Decode([(byte)'a', 0xC3, (byte)'b'], out _));
    }

    [Fact]
    public void LinesFollowRustSemantics()
    {
        Assert.Empty(TextDiff.Lines(""));
        Assert.Equal(["a", "b"], TextDiff.Lines("a\r\nb\n"));
        Assert.Equal(["", ""], TextDiff.Lines("\n\n"));
        Assert.Equal(["a", "b\r"], TextDiff.Lines("a\nb\r"));
        Assert.Equal(["a\rb"], TextDiff.Lines("a\rb"));
    }

    [Fact]
    public void ReadTextReportsErrors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mavyex-tdiff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bin = Path.Combine(dir, "x.bin");
            File.WriteAllBytes(bin, [1, 0, 2]);
            TextDiff.ReadText(bin, out var err);
            Assert.Equal("x.bin: Binary file – text compare needs text files", err);

            var txt = Path.Combine(dir, "t.txt");
            File.WriteAllText(txt, "hello\nworld\n");
            Assert.Equal("hello\nworld\n", TextDiff.ReadText(txt, out err));
            Assert.Null(err);

            TextDiff.ReadText(Path.Combine(dir, "missing.txt"), out err);
            Assert.False(string.IsNullOrEmpty(err));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LargeInputStaysFast()
    {
        var a = Enumerable.Range(0, 100_000).Select(i => "line " + i).ToList();
        var b = a.ToList();
        for (int i = 0; i < b.Count; i += 1000) b[i] += " changed";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (rows, added, removed) = TextDiff.DiffRows(a, b, false, false);
        Assert.Equal(100, TextDiff.Hunks(rows).Count);
        Assert.Equal((100, 100), (added, removed));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
