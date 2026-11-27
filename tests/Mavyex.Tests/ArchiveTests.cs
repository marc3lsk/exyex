using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using Mavyex.Services;
using Xunit;

namespace Mavyex.Tests;

public sealed class ArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mavyex-archive-test-{Guid.NewGuid():N}");

    public ArchiveTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException) { }
    }

    private static void MakeZip(string path, params (string Name, string? Text)[] entries)
    {
        using var za = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            var e = za.CreateEntry(name);
            if (text == null) continue;
            using var s = e.Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    private static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);

    private static void Write(string p, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private (string Dir, string F) MakeTree()
    {
        Write(Path.Combine(root, "dir", "sub", "x.txt"), "xx");
        Write(Path.Combine(root, "f.txt"), "hello");
        return (Path.Combine(root, "dir"), Path.Combine(root, "f.txt"));
    }

    [Fact]
    public void BrowseAndExtractZip()
    {
        var zp = Path.Combine(root, "t.zip");
        MakeZip(zp, ("a.txt", "hello"), ("d/b.txt", "world!"));
        var (z, inner) = Archive.Split(Path.Combine(zp, "d"))!.Value;
        Assert.Equal((zp, "d"), (z, inner));
        Assert.Null(Archive.SplitInner(zp));
        Assert.Equal((zp, ""), Archive.Split(zp));
        var top = Archive.List(zp, "").Select(e => (e.Name, e.IsDir)).ToList();
        Assert.Equal([("a.txt", false), ("d", true)], top);
        Assert.Equal(6, Archive.List(zp, "d")[0].Size);
        Assert.Equal(Path.Combine(zp, "d", "b.txt"), Archive.List(zp, "d")[0].Path);
        Assert.Equal("txt", Archive.List(zp, "d")[0].Ext);
        Assert.Equal(6, Archive.Size(zp, "d"));
        var outDir = Path.Combine(root, "out");
        Archive.Extract(zp, "d", outDir, CancellationToken.None, _ => { });
        Assert.Equal("world!", File.ReadAllText(Path.Combine(outDir, "b.txt")));
        Assert.Equal("hel", Utf8(Archive.ReadHead(zp, "a.txt", 3)));
        Assert.Throws<IOException>(() => Archive.Extract(zp, "nope", Path.Combine(root, "n"), CancellationToken.None, _ => { }));
    }

    [Fact]
    public void SplitSemantics()
    {
        var zp = Path.Combine(root, "t.zip");
        MakeZip(zp, ("a.txt", "x"));
        Assert.Equal((zp, "a/b"), Archive.Split(zp + @"\a\.\b"));
        Assert.True(Archive.InArchive(Path.Combine(zp, "a.txt")));
        // a folder named like an archive, or a missing archive, is not virtual
        Directory.CreateDirectory(Path.Combine(root, "folder.zip"));
        Assert.Null(Archive.Split(Path.Combine(root, "folder.zip", "x")));
        Assert.Null(Archive.Split(Path.Combine(root, "missing.zip", "x")));
        Assert.True(Archive.IsArchiveName("A.TGZ") && Archive.IsPlainGz("x.gz") && !Archive.IsPlainGz("x.tar.gz"));
        Assert.False(Archive.IsArchiveName("x.7z"));
        Assert.True(Archive.Contains(zp, Path.Combine(zp, "d").ToUpperInvariant()));
        Assert.True(Archive.Contains(zp, zp + "/d"));
        Assert.False(Archive.Contains(zp, zp + ".bak"));
    }

    [Fact]
    public void ImplicitFoldersAreSynthesized()
    {
        var zp = Path.Combine(root, "i.zip");
        // no folder entries at all; an explicit "e/" folder; "./" and ".." names are cleaned / dropped
        MakeZip(zp, ("x/y/z.txt", "zz"), ("e/", null), ("./top.txt", "t"), ("../evil.txt", "e"), ("w\\v.txt", "v"));
        var top = Archive.List(zp, "").Select(e => (e.Name, e.IsDir)).ToList();
        Assert.Equal([("e", true), ("top.txt", false), ("w", true), ("x", true)], top);
        var y = Archive.List(zp, "x");
        Assert.Single(y);
        Assert.True(y[0].IsDir && y[0].Modified == null && y[0].Path == Path.Combine(zp, "x", "y"));
        Assert.Equal("zz", Utf8(Archive.ReadHead(zp, "x/y/z.txt", 10)));
        Assert.Empty(Archive.List(zp, "e"));
        Assert.Equal(2, Archive.Size(zp, "x"));
        var outDir = Path.Combine(root, "all");
        Archive.Extract(zp, "", outDir, CancellationToken.None, _ => { });
        Assert.True(File.Exists(Path.Combine(outDir, "x", "y", "z.txt")));
        Assert.True(Directory.Exists(Path.Combine(outDir, "e")));
        Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
    }

    [Fact]
    public void CreateZipFromFilesAndFolders()
    {
        var (dir, f) = MakeTree();
        var zp = Path.Combine(root, "out.zip");
        string[] srcs = [dir, f];
        long got = 0;
        Archive.Create(srcs, zp, CancellationToken.None, (n, _) => got += n);
        Assert.True(got >= 7);
        Assert.Equal(["dir", "f.txt"], Archive.List(zp, "").Select(e => e.Name));
        Assert.Equal(2, Archive.List(zp, "dir/sub")[0].Size);
        Archive.Create(srcs[1..], zp, CancellationToken.None, (_, _) => { }); // replaces
        Assert.Single(Archive.List(zp, ""));
        Assert.False(File.Exists(Path.Combine(root, "out.mavyex-tmp")));
        Assert.Equal(2 + 5, Archive.TreeTotal(srcs));
    }

    [Fact]
    public void TarGzAndGzRoundTrip()
    {
        var (dir, f) = MakeTree();
        string[] srcs = [dir, f];
        foreach (var name in new[] { "out.tar.gz", "out.tgz", "out.tar" })
        {
            var tp = Path.Combine(root, name);
            Archive.Create(srcs, tp, CancellationToken.None, (_, _) => { });
            Assert.NotNull(Archive.Split(Path.Combine(tp, "dir")));
            var top = Archive.List(tp, "").Select(e => (e.Name, e.IsDir)).ToList();
            Assert.Equal([("dir", true), ("f.txt", false)], top);
            Assert.Equal(2, Archive.List(tp, "dir/sub")[0].Size);
            Assert.Equal(2, Archive.Size(tp, "dir"));
            Assert.Equal("hell", Utf8(Archive.ReadHead(tp, "f.txt", 4)));
            var outDir = Path.Combine(root, $"x-{name}");
            Archive.Extract(tp, "dir", outDir, CancellationToken.None, _ => { });
            Assert.Equal("xx", File.ReadAllText(Path.Combine(outDir, "sub", "x.txt")));
        }
        // the tar.gz is a real gzip-compressed tar other tools can read
        using (var gz = new GZipStream(File.OpenRead(Path.Combine(root, "out.tar.gz")), CompressionMode.Decompress))
        using (var tr = new TarReader(gz))
        {
            var names = new List<string>();
            while (tr.GetNextEntry() is { } e) names.Add(e.Name);
            Assert.Contains("dir/sub/x.txt", names);
        }
        // edit session on a tar.gz
        var tgz = Path.Combine(root, "out.tar.gz");
        File.WriteAllText(Archive.RealForWrite(Path.Combine(tgz, "new.txt")), "added");
        Archive.Save(tgz);
        Assert.Equal("added", Utf8(Archive.ReadHead(tgz, "new.txt", 100)));
        Assert.Equal(3, Archive.List(tgz, "").Count);
        Assert.False(File.Exists(Path.Combine(root, "out.tar.mavyex-tmp")));
        // plain gz: one file, named after the archive
        var gp = Path.Combine(root, "f.txt.gz");
        Archive.Create(srcs[1..], gp, CancellationToken.None, (_, _) => { });
        var es = Archive.List(gp, "");
        Assert.Equal(("f.txt", 5L, 1), (es[0].Name, es[0].Size, es.Count));
        Assert.Equal("hello", Utf8(Archive.ReadHead(gp, "f.txt", 100)));
        Archive.Extract(gp, "f.txt", Path.Combine(root, "plain.txt"), CancellationToken.None, _ => { });
        Assert.Equal("hello", File.ReadAllText(Path.Combine(root, "plain.txt")));
        var bad = Assert.Throws<IOException>(() => Archive.Create(srcs, Path.Combine(root, "bad.gz"), CancellationToken.None, (_, _) => { }));
        Assert.Contains("exactly one file", bad.Message);
        Assert.False(File.Exists(Path.Combine(root, "bad.gz")) || File.Exists(Path.Combine(root, "bad.mavyex-tmp")));
    }

    [Fact]
    public void PlainGzIsStandardAndKeepsName()
    {
        var (_, f) = MakeTree();
        var gp = Path.Combine(root, "renamed.gz");
        Archive.Create([f], gp, CancellationToken.None, (_, _) => { });
        // readable by GZipStream; header carries FNAME = original file name
        using (var gz = new GZipStream(File.OpenRead(gp), CompressionMode.Decompress))
        using (var sr = new StreamReader(gz))
            Assert.Equal("hello", sr.ReadToEnd());
        var raw = File.ReadAllBytes(gp);
        Assert.Equal(0x08, raw[3] & 0x08);
        Assert.Equal("f.txt", Encoding.UTF8.GetString(raw, 10, 5));
        // shown under the archive's name without .gz
        Assert.Equal("renamed", Archive.List(gp, "")[0].Name);
        // a gz without a stem is shown as "data"; external gzip files (no FNAME) work too
        var ext = Path.Combine(root, "ext.log.gz");
        using (var w = new GZipStream(File.Create(ext), CompressionLevel.Optimal))
            w.Write(Encoding.UTF8.GetBytes("line1\nline2\n"));
        Assert.Equal(("ext.log", 12L), (Archive.List(ext, "")[0].Name, Archive.List(ext, "")[0].Size));
        Assert.Equal("line1", Utf8(Archive.ReadHead(ext, "ext.log", 5)));
        // edit session on a plain gz writes a gz again
        File.WriteAllText(Archive.RealForWrite(Path.Combine(ext, "ext.log")), "changed");
        Archive.Save(ext);
        Assert.Equal("changed", Utf8(Archive.ReadHead(ext, "ext.log", 100)));
    }

    [Fact]
    public void EditSessionWritesChangesBack()
    {
        var zp = Path.Combine(root, "e.zip");
        MakeZip(zp, ("a.txt", "hello"), ("d/b.txt", "world"));
        Assert.True(Archive.Active(zp) == null && !Archive.IsDirty(zp));
        // write: new file in a subfolder, rename, delete
        File.WriteAllText(Archive.RealForWrite(Path.Combine(zp, "d", "new.txt")), "added");
        File.Move(Archive.RealForWrite(Path.Combine(zp, "a.txt")), Archive.RealForWrite(Path.Combine(zp, "renamed.txt")));
        File.Delete(Archive.RealForWrite(Path.Combine(zp, "d", "b.txt")));
        Assert.True(Archive.IsDirty(zp));
        Assert.Contains(zp, Archive.Sessions());
        // while editing, listing and reading come from the working copy
        var names = Archive.List(zp, "").Select(e => e.Name).ToList();
        Assert.Contains("renamed.txt", names);
        Assert.DoesNotContain("a.txt", names);
        Assert.Equal("added", Utf8(Archive.ReadHead(zp, "d/new.txt", 100)));
        Assert.Equal(Path.Combine(zp, "d", "new.txt"), Archive.List(zp, "d").Single().Path);
        Assert.True(Archive.Contains(zp, Path.Combine(zp, "d")) && !Archive.Contains(zp, Path.Combine(root, "e.zip.bak")));
        Archive.Save(zp);
        Assert.Null(Archive.Active(zp));
        Assert.Equal(["new.txt"], Archive.List(zp, "d").Select(e => e.Name).Order());
        Assert.Equal("hello", Utf8(Archive.ReadHead(zp, "renamed.txt", 100)));
        Assert.False(File.Exists(Path.Combine(root, "e.mavyex-tmp")));
        // an untouched session is not dirty, and discarding drops the working copy
        var staging = Archive.Ensure(zp);
        Assert.False(Archive.IsDirty(zp));
        Archive.Discard(zp);
        Assert.Null(Archive.Active(zp));
        Assert.False(Directory.Exists(staging));
        Assert.Throws<IOException>(() => Archive.Save(zp));
    }

    [Fact]
    public void RealForEditTracksChangesAndDatesSurviveSave()
    {
        var zp = Path.Combine(root, "dates.tar.gz");
        var (dir, f) = MakeTree();
        var old = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(f, old);
        Archive.Create([dir, f], zp, CancellationToken.None, (_, _) => { });
        Assert.Equal(old, Archive.List(zp, "").Single(e => e.Name == "f.txt").Modified);
        var real = Archive.RealForEdit(Path.Combine(zp, "dir", "sub", "x.txt"));
        Assert.False(Archive.IsDirty(zp));
        Assert.Equal(old, File.GetLastWriteTimeUtc(Path.Combine(Archive.Active(zp)!, "f.txt"))); // extraction keeps dates
        File.WriteAllText(real, "edited!");
        Assert.True(Archive.IsDirty(zp));
        Archive.Save(zp);
        Assert.Equal("edited!", Utf8(Archive.ReadHead(zp, "dir/sub/x.txt", 100)));
        Assert.Equal(old, Archive.List(zp, "").Single(e => e.Name == "f.txt").Modified);
        Assert.Throws<IOException>(() => Archive.RealForEdit(zp)); // the archive itself is not "inside"
    }

    [Fact]
    public void EmptyArchiveCanBeEdited()
    {
        var zp = Path.Combine(root, "empty.zip");
        MakeZip(zp);
        Assert.Empty(Archive.List(zp, ""));
        File.WriteAllText(Archive.RealForWrite(Path.Combine(zp, "a.txt")), "a");
        Archive.Save(zp);
        Assert.Equal("a.txt", Archive.List(zp, "").Single().Name);
    }

    [Fact]
    public void CancelRemovesPartialOutput()
    {
        var big = Path.Combine(root, "big.bin");
        File.WriteAllBytes(big, new byte[3 * 1024 * 1024]);
        using (var pre = new CancellationTokenSource())
        {
            pre.Cancel();
            foreach (var name in new[] { "c.zip", "c.tar", "c.tgz", "c.gz" })
            {
                var zp = Path.Combine(root, name);
                Assert.ThrowsAny<OperationCanceledException>(() => Archive.Create([big], zp, pre.Token, (_, _) => { }));
                Assert.False(File.Exists(zp));
                Assert.Empty(Directory.GetFiles(root, "*.mavyex-tmp"));
            }
        }
        // cancel in the middle of a create
        using (var cts = new CancellationTokenSource())
        {
            var zp = Path.Combine(root, "mid.tar.gz");
            Assert.ThrowsAny<OperationCanceledException>(() => Archive.Create([big], zp, cts.Token, (n, _) =>
            {
                if (n > 0) cts.Cancel();
            }));
            Assert.False(File.Exists(zp));
            Assert.Empty(Directory.GetFiles(root, "*.mavyex-tmp"));
        }
        // cancel in the middle of an extract: the partial file is deleted
        var ok = Path.Combine(root, "ok.zip");
        Archive.Create([big], ok, CancellationToken.None, (_, _) => { });
        using (var cts = new CancellationTokenSource())
        {
            var target = Path.Combine(root, "partial.bin");
            Assert.ThrowsAny<OperationCanceledException>(() => Archive.Extract(ok, "big.bin", target, cts.Token, _ => cts.Cancel()));
            Assert.False(File.Exists(target));
        }
    }

    [Fact]
    public void ExtractTempAndJarManifestFirst()
    {
        var jar = Path.Combine(root, "a.jar");
        MakeZip(jar, ("a/A.class", "x"), ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0"));
        var t = Archive.ExtractTemp(jar, "a/A.class");
        Assert.Equal("x", File.ReadAllText(t));
        Assert.Equal("A.class", Path.GetFileName(t));
        File.WriteAllText(Archive.RealForWrite(Path.Combine(jar, "b.txt")), "b");
        Assert.Equal(Path.Combine(Archive.Active(jar)!, "b.txt"), Archive.ExtractTemp(jar, "b.txt"));
        Archive.Save(jar);
        using var za = ZipFile.OpenRead(jar);
        Assert.Equal("META-INF/", za.Entries[0].FullName);
        Assert.Equal("META-INF/MANIFEST.MF", za.Entries[1].FullName);
    }
}
