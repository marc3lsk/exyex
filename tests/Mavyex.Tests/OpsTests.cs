using System.IO;
using Mavyex.Services;
using Xunit;
using Path = System.IO.Path;

namespace Mavyex.Tests;

public sealed class OpsTests : IDisposable
{
    private readonly string _dir;

    public OpsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mavyex-ops-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Sub(string name)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static Job Wait(Job job)
    {
        Assert.True(job.Wait(60_000), "job timed out");
        return job;
    }

    private static void Ok(Job job)
    {
        Wait(job);
        Assert.Null(job.Error);
        Assert.Equal(JobState.Done, job.State);
        Assert.NotNull(job.Finished);
    }

    [Fact]
    public void BigFileIsCopiedInChunksWithProgressAndMtime()
    {
        var src = Path.Combine(_dir, "big.bin");
        var data = new byte[20 * 1024 * 1024];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
        File.WriteAllBytes(src, data);
        var old = DateTime.UtcNow.AddDays(-30);
        File.SetLastWriteTimeUtc(src, old);
        var dest = Sub("out");

        var job = Ops.Spawn(OpKind.Copy, [src], dest);
        Ok(job);
        Assert.Equal(data.Length, Interlocked.Read(ref job.Done)); // progress reaches the full size
        Assert.Equal(data.Length, Interlocked.Read(ref job.Total));
        var copied = Path.Combine(dest, "big.bin");
        Assert.Equal(data, File.ReadAllBytes(copied));
        Assert.True((File.GetLastWriteTimeUtc(copied) - old).Duration() < TimeSpan.FromSeconds(3), "modification time should be preserved");
    }

    [Fact]
    public void CancelledChunkedCopyLeavesNoPartialFile()
    {
        var src = Path.Combine(_dir, "big.bin");
        File.WriteAllBytes(src, new byte[20 * 1024 * 1024]);
        var dst = Path.Combine(_dir, "copy.bin");
        var job = new Job();
        job.Cancel();
        var pr = new Progress(job, 1);
        Assert.Throws<OperationCanceledException>(() => Ops.CopyFile(src, dst, 20 * 1024 * 1024, pr, job));
        Assert.False(File.Exists(dst), "partial file must be removed");
    }

    [Fact]
    public void CancellingARunningJobStopsIt()
    {
        var src = Path.Combine(_dir, "big.bin");
        File.WriteAllBytes(src, new byte[64 * 1024 * 1024]);
        var dest = Sub("out");
        var job = Ops.Spawn(OpKind.Copy, [src], dest);
        job.Cancel();
        Wait(job);
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Null(job.Error);
        Assert.False(File.Exists(Path.Combine(dest, "big.bin")));
    }

    [Fact]
    public void ConflictPoliciesOverwriteSkipAndNewer()
    {
        var (src, dest) = (Sub("src"), Sub("dest"));
        foreach (var n in new[] { "over.txt", "skip.txt", "old.txt", "newer.txt" })
        {
            File.WriteAllText(Path.Combine(src, n), "new");
            File.WriteAllText(Path.Combine(dest, n), "existing");
        }
        // old.txt: the existing target is newer than the source; newer.txt: the source is newer
        File.SetLastWriteTimeUtc(Path.Combine(src, "old.txt"), DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(Path.Combine(dest, "newer.txt"), DateTime.UtcNow.AddDays(-1));
        var srcs = new[] { "over.txt", "skip.txt", "old.txt", "newer.txt" }.Select(n => Path.Combine(src, n)).ToList();
        var policies = new Dictionary<string, Policy>
        {
            [srcs[0]] = Policy.Overwrite, [srcs[1]] = Policy.Skip, [srcs[2]] = Policy.Newer, [srcs[3]] = Policy.Newer,
        };
        Ok(Ops.SpawnWith(OpKind.Copy, srcs, dest, policies));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "over.txt")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(dest, "skip.txt")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(dest, "old.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "newer.txt")));
        Assert.Equal(4, Directory.GetFiles(dest).Length); // no extra copies

        // a skipped move leaves the source in place
        var skip = Path.Combine(src, "skip.txt");
        Ok(Ops.SpawnWith(OpKind.Move, [skip], dest, new() { [skip] = Policy.Skip }));
        Assert.True(File.Exists(skip));
        // an overwriting move replaces the target and removes the source
        var over = Path.Combine(src, "over.txt");
        File.WriteAllText(over, "newest");
        Ok(Ops.SpawnWith(OpKind.Move, [over], dest, new() { [over] = Policy.Overwrite }));
        Assert.False(File.Exists(over));
        Assert.Equal("newest", File.ReadAllText(Path.Combine(dest, "over.txt")));
    }

    [Fact]
    public void NameConflictsGetANumberAndFoldersCopyRecursively()
    {
        var src = Path.Combine(_dir, "a");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "f.txt"), "hi");
        var dest = Sub("dest");
        Directory.CreateDirectory(Path.Combine(dest, "a")); // conflict
        var job = Ops.Spawn(OpKind.Copy, [src], dest);
        Ok(job);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(dest, "a (2)", "sub", "f.txt")));
        Assert.Equal("Copying a", job.Title);
        Assert.Equal("Copying 2 items", Ops.Spawn(OpKind.Copy, [src, src], dest).Title);
    }

    [Fact]
    public void CopyIntoTheSameFolderKeepsBoth()
    {
        var f = Path.Combine(_dir, "x.txt");
        File.WriteAllText(f, "x");
        Ok(Ops.SpawnWith(OpKind.Copy, [f], _dir, new() { [f] = Policy.Overwrite })); // target == source: never overwrite itself
        Assert.Equal("x", File.ReadAllText(Path.Combine(_dir, "x (2).txt")));
    }

    [Fact]
    public void UniqueDestNumbersNames()
    {
        Assert.Equal(Path.Combine(_dir, "n.txt"), Ops.UniqueDest(_dir, "n.txt"));
        File.WriteAllText(Path.Combine(_dir, "n.txt"), "");
        File.WriteAllText(Path.Combine(_dir, "n (2).txt"), "");
        Assert.Equal(Path.Combine(_dir, "n (3).txt"), Ops.UniqueDest(_dir, "n.txt"));
        File.WriteAllText(Path.Combine(_dir, "a.tar.gz"), "");
        Assert.Equal(Path.Combine(_dir, "a.tar (2).gz"), Ops.UniqueDest(_dir, "a.tar.gz"));
        File.WriteAllText(Path.Combine(_dir, ".bashrc"), "");
        Assert.Equal(Path.Combine(_dir, ".bashrc (2)"), Ops.UniqueDest(_dir, ".bashrc"));
        Directory.CreateDirectory(Path.Combine(_dir, "dir"));
        Assert.Equal(Path.Combine(_dir, "dir (2)"), Ops.UniqueDest(_dir, "dir"));
    }

    [Fact]
    public void MergeIntoExistingFolderWithNewerOnly()
    {
        var src = Path.Combine(Sub("src"), "d");
        var dest = Sub("dest");
        var dd = Path.Combine(dest, "d");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dd);
        File.WriteAllText(Path.Combine(src, "fresh.txt"), "src");
        File.WriteAllText(Path.Combine(dd, "fresh.txt"), "dest");
        File.SetLastWriteTimeUtc(Path.Combine(dd, "fresh.txt"), DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(src, "stale.txt"), "src");
        File.SetLastWriteTimeUtc(Path.Combine(src, "stale.txt"), DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(dd, "stale.txt"), "dest");
        File.WriteAllText(Path.Combine(src, "only.txt"), "src");
        File.WriteAllText(Path.Combine(dd, "keep.txt"), "dest");

        // a merging move: the folder can't be renamed onto the existing one, so it is copied file by file
        Ok(Ops.SpawnWith(OpKind.Move, [src], dest, new() { [src] = Policy.Newer }));
        Assert.Equal("src", File.ReadAllText(Path.Combine(dd, "fresh.txt")));
        Assert.Equal("dest", File.ReadAllText(Path.Combine(dd, "stale.txt")));
        Assert.Equal("src", File.ReadAllText(Path.Combine(dd, "only.txt")));
        Assert.Equal("dest", File.ReadAllText(Path.Combine(dd, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "d (2)")));
        Assert.True(File.Exists(Path.Combine(src, "stale.txt")), "skipped older files stay in the source folder");
    }

    [Fact]
    public void OverwriteMergesFoldersAndMoveRemovesTheSource()
    {
        var src = Path.Combine(Sub("src"), "d");
        var dd = Path.Combine(Sub("dest"), "d");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dd);
        File.WriteAllText(Path.Combine(src, "f.txt"), "src");
        File.SetLastWriteTimeUtc(Path.Combine(src, "f.txt"), DateTime.UtcNow.AddDays(-1)); // older, still overwritten
        File.WriteAllText(Path.Combine(dd, "f.txt"), "dest");
        File.WriteAllText(Path.Combine(dd, "keep.txt"), "dest");
        Ok(Ops.SpawnWith(OpKind.Move, [src], Path.GetDirectoryName(dd)!, new() { [src] = Policy.Overwrite }));
        Assert.Equal("src", File.ReadAllText(Path.Combine(dd, "f.txt")));
        Assert.True(File.Exists(Path.Combine(dd, "keep.txt")));
        Assert.False(Directory.Exists(src));
    }

    [Fact]
    public void MoveRenamesFilesAndFolders()
    {
        var src = Sub("src");
        var dest = Sub("dest");
        var f = Path.Combine(src, "f.txt");
        File.WriteAllText(f, "f");
        var d = Path.Combine(src, "d");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "g.txt"), "g");
        var job = Ops.Spawn(OpKind.Move, [f, d], dest);
        Ok(job);
        Assert.False(File.Exists(f));
        Assert.False(Directory.Exists(d));
        Assert.Equal("f", File.ReadAllText(Path.Combine(dest, "f.txt")));
        Assert.Equal("g", File.ReadAllText(Path.Combine(dest, "d", "g.txt")));
        Assert.Equal(Interlocked.Read(ref job.Total), Interlocked.Read(ref job.Done));
        // moving into the folder it is already in does nothing
        var again = Path.Combine(dest, "f.txt");
        Ok(Ops.Spawn(OpKind.Move, [again], dest));
        Assert.True(File.Exists(again));
        Assert.False(File.Exists(Path.Combine(dest, "f (2).txt")));
    }

    [Fact]
    public void FolderCannotBePlacedInsideItself()
    {
        var a = Sub("a");
        var inner = Sub(Path.Combine("a", "inner"));
        var job = Wait(Ops.Spawn(OpKind.Copy, [a], inner));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal($"{a}: cannot place a folder inside itself", job.Error);
    }

    [Fact]
    public void ErrorsAreCollectedAndSummarized()
    {
        var dest = Sub("dest");
        var missing = Enumerable.Range(1, 5).Select(i => Path.Combine(_dir, $"missing{i}.txt")).ToList();
        var job = Wait(Ops.Spawn(OpKind.Delete, missing, null));
        Assert.Equal(JobState.Failed, job.State);
        Assert.StartsWith($"{missing[0]}: ", job.Error);
        Assert.EndsWith(" (+2 more)", job.Error);
        Assert.Equal(2, job.Error!.Split("; ").Length - 1);
        Assert.Equal("no destination", Wait(Ops.Spawn(OpKind.Copy, [dest], null)).Error);
    }

    [Fact]
    public void DeleteJobReportsProgressAndRemoves()
    {
        var f = Path.Combine(_dir, "x.txt");
        File.WriteAllText(f, "x");
        var d = Sub("tree");
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        File.WriteAllText(Path.Combine(d, "sub", "y.txt"), "y");
        var job = Ops.Spawn(OpKind.Delete, [f, d], null);
        Ok(job);
        Assert.False(File.Exists(f));
        Assert.False(Directory.Exists(d));
        Assert.Equal(2, Interlocked.Read(ref job.Done));
        Assert.Equal(2, Interlocked.Read(ref job.Total));
    }

    [Fact]
    public void TrashSendsTempFilesToTheRecycleBin()
    {
        // only files this test created in its own temp folder
        var f = Path.Combine(_dir, $"mavyex-trash-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(f, "x");
        var d = Sub($"mavyex-trash-test-{Guid.NewGuid():N}");
        File.WriteAllText(Path.Combine(d, "y.txt"), "y");
        var job = Ops.Spawn(OpKind.Trash, [f, d], null);
        Ok(job);
        Assert.Equal("Recycling 2 items", job.Title);
        Assert.False(File.Exists(f));
        Assert.False(Directory.Exists(d));
    }

    [Fact]
    public void TreeSizeCountsEveryItem()
    {
        var d = Sub("t");
        File.WriteAllBytes(Path.Combine(d, "a"), new byte[10]);
        File.WriteAllBytes(Path.Combine(d, "empty"), []);
        Directory.CreateDirectory(Path.Combine(d, "e"));
        Assert.Equal(12, Ops.TreeSize(d)); // 10 + 1 (empty file) + 1 (empty folder)
        Assert.Equal(1, Ops.TreeSize(Path.Combine(_dir, "nope")));
    }
}
