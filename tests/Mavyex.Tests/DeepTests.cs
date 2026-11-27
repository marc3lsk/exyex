using System.IO;
using Mavyex.Core;
using Mavyex.Services;
using Xunit;

namespace Mavyex.Tests;

public sealed class DeepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mavyex-deep-{Environment.ProcessId}-{Guid.NewGuid():N}");

    public DeepTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private string Put(string rel, string content = "x")
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    /// <summary>Drains the job until it stops; returns everything it found.</summary>
    private static List<Entry> Collect(DeepJob job)
    {
        var all = new List<Entry>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            all.AddRange(job.Drain());
            if (!job.Running) break;
            Assert.True(DateTime.UtcNow < deadline, "deep search did not finish");
            Thread.Sleep(5);
        }
        return all;
    }

    private List<(string Name, string Snippet)> RunWith(string q, string exclude, SearchOpts opts, bool hidden = true)
    {
        using var job = Deep.Spawn(_root, q, opts, Deep.ParseExclude(exclude), hidden);
        return Collect(job).Select(e => (e.Name, e.Snippet)).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    private List<string> Run(string q, string exclude = "", bool hidden = true) =>
        RunWith(q, exclude, new SearchOpts(), hidden).Select(x => x.Name).ToList();

    // ---- ported from deep.rs

    [Fact]
    public void FindsNestedAndHonoursExclude()
    {
        Put(@"a\b\note.txt");
        Put(@"node_modules\x\note.txt");
        Put("note.md");
        Assert.Equal([@"a\b\note.txt", @"node_modules\x\note.txt", "note.md"], Run("note"));
        Assert.Equal([@"a\b\note.txt", "note.md"], Run("note", "node_modules"));
        Assert.Equal([@"a\b\note.txt"], Run("note", "node_*, *.md"));
    }

    [Fact]
    public void ContentSearchReportsFirstLineAndSkipsBinary()
    {
        Put("one.txt", "alpha\nthe needle here\nneedle again\n");
        Put(@"sub\two.rs", "fn main() {}\n// NEEDLE\n");
        File.WriteAllBytes(Path.Combine(_root, "sub", "bin.dat"), "needle\0\0\0"u8.ToArray());
        Put("none.txt", "nothing");

        var o = new SearchOpts { Content = true };
        var r = RunWith("needle", "", o);
        Assert.Equal(2, r.Count);
        Assert.Equal(("one.txt", "2: the needle here   (+1 more)"), r[0]);
        Assert.Equal(@"sub\two.rs", r[1].Name);
        Assert.StartsWith("2: // NEEDLE", r[1].Snippet);
        // filters narrow the files that are scanned
        r = RunWith(":ext=rs needle", "", o);
        Assert.Equal([@"sub\two.rs"], r.Select(x => x.Name));
    }

    // ---- additional

    [Fact]
    public void QueryIsMatchedAgainstTheRelativePathAndFoldersAreResults()
    {
        Put(@"src\ui\readme.md");
        Put(@"docs\readme.md");
        Assert.Equal([@"src\ui\readme.md"], Run(@"ui\read"));
        Assert.Equal(["src", @"src\ui", @"src\ui\readme.md"], Run("src"));
        using var job = Deep.Spawn(_root, "readme", new SearchOpts(), [], true);
        var all = Collect(job);
        Assert.All(all, e => Assert.True(e.Deep));
        Assert.All(all, e => Assert.True(File.Exists(e.Path)));
    }

    [Fact]
    public void HiddenItemsFollowShowHidden()
    {
        var h = Put("secret.txt");
        File.SetAttributes(h, FileAttributes.Hidden);
        var hd = Path.Combine(_root, "hid");
        Directory.CreateDirectory(hd);
        Put(@"hid\inner.txt");
        new DirectoryInfo(hd).Attributes |= FileAttributes.Hidden;
        Put("plain.txt");
        Assert.Equal([@"hid\inner.txt", "plain.txt", "secret.txt"], Run(".txt", hidden: true));
        Assert.Equal(["plain.txt"], Run(".txt", hidden: false)); // a hidden folder is not descended into
    }

    [Fact]
    public void FiltersApplyToNameSearchToo()
    {
        Put(@"a\big.txt", new string('x', 2048));
        Put(@"a\small.txt", "x");
        Assert.Equal([@"a\big.txt"], Run(":size>1kb"));
        Assert.Equal(["a"], Run(":type=dir"));
    }

    [Fact]
    public void TruncatesAtTheCap()
    {
        for (int i = 0; i < 20; i++) Put($@"d{i % 3}\f{i}.txt");
        using var job = Deep.Spawn(_root, ".txt", new SearchOpts(), [], true, null, 5);
        var r = Collect(job);
        Assert.Equal(5, r.Count);
        Assert.True(job.Truncated);

        using var job2 = Deep.Spawn(_root, ".txt", new SearchOpts(), [], true);
        Assert.Equal(20, Collect(job2).Count);
        Assert.False(job2.Truncated);
    }

    [Fact]
    public void ContentSearchSkipsLargeAndEmptyFilesAndHandlesCrLf()
    {
        Put("crlf.txt", "a\r\nfind me\r\nfind me too\r\nfind\r\n");
        Put("empty.txt", "");
        using (var f = File.Create(Path.Combine(_root, "huge.txt")))
        {
            f.Write("find\n"u8);
            f.SetLength(8 * 1024 * 1024 + 1);
        }
        var r = RunWith("find", "", new SearchOpts { Content = true });
        Assert.Equal([("crlf.txt", "2: find me   (+2 more)")], r);
        // long matched lines are trimmed to 140 chars in the snippet
        Put("long.txt", "   " + new string('z', 300) + " find");
        r = RunWith("zzz", "", new SearchOpts { Content = true });
        Assert.Equal("1: " + new string('z', 140), r.Single(x => x.Name == "long.txt").Snippet);
    }

    [Fact]
    public void ContentSearchUsesTheSelectedModeAndCase()
    {
        Put("a.txt", "Hello World\nhello world\n");
        var r = RunWith("^hello", "", new SearchOpts { Content = true, Mode = SearchMode.Regex, MatchCase = true });
        Assert.Equal([("a.txt", "2: hello world")], r);
        // only filters, no text: content mode just lists matching files
        r = RunWith(":ext=txt", "", new SearchOpts { Content = true });
        Assert.Equal([("a.txt", "")], r);
    }

    [Fact]
    public void ContentSearchTruncates()
    {
        // more files than the scan queue holds (512): the walker must wait for the scanners and sees the hits
        for (int i = 0; i < 1500; i++) Put($"f{i:D4}.txt", "hit");
        using var job = Deep.Spawn(_root, "hit", new SearchOpts { Content = true }, [], true, null, 3);
        var r = Collect(job);
        // the scanners run ahead of the walker's check, like in Rust: at least the cap, never everything
        Assert.True(job.Truncated);
        Assert.InRange(r.Count, 3, 1499);
    }

    [Fact]
    public void CallbackFiresAndDisposeCancels()
    {
        for (int i = 0; i < 50; i++) Put($@"x{i % 5}\n{i}.txt");
        int calls = 0;
        using (var job = Deep.Spawn(_root, ".txt", new SearchOpts(), [], true, () => Interlocked.Increment(ref calls)))
        {
            Assert.Equal(50, Collect(job).Count);
        }
        Assert.True(calls >= 1);

        var j2 = Deep.Spawn(_root, ".txt", new SearchOpts { Content = true }, [], true);
        j2.Dispose(); // must not throw or hang; Drain still works
        j2.Drain();
    }

    [Fact]
    public void SymlinkedFoldersAreNotFollowed()
    {
        Put(@"real\t.txt");
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_root, "real"));
        }
        catch (IOException) { return; } // needs developer mode / admin
        catch (UnauthorizedAccessException) { return; }
        // the link itself is a result, but nothing below it ("link\t.txt")
        Assert.Equal(["link", "real", @"real\t.txt"], Run("l"));
    }

    [Fact]
    public void MissingRootFinishesEmpty()
    {
        using var job = Deep.Spawn(Path.Combine(_root, "nope"), "x", new SearchOpts(), [], true);
        Assert.Empty(Collect(job));
        Assert.False(job.Truncated);
    }
}
