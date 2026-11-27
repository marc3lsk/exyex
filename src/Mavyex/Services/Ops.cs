using System.Diagnostics;
using System.Runtime.InteropServices;
using Mavyex.Core;

namespace Mavyex.Services;

// Port of ops.rs: file operations running on a worker thread with progress + cancellation.

public enum OpKind { Copy, Move, Trash, Delete, /** compress the sources into the archive given as dest */ Zip }

/// <summary>What to do when the target of a copy / move already exists.</summary>
public enum Policy
{
    /// <summary>keep both: "name (2).ext"</summary>
    Rename,
    Overwrite,
    Skip,
    /// <summary>overwrite only when the source is newer (folders are merged file by file)</summary>
    Newer,
}

public enum JobState { Running, Done, Cancelled, Failed }

/// <summary>A background file operation (copy / move / trash / delete / zip) with progress and cancel.
/// Progress fields are updated from the worker thread; the UI polls them (e.g. with a DispatcherTimer).</summary>
public sealed class Job
{
    public long Id { get; init; }
    public OpKind Kind { get; init; }
    /// <summary>e.g. "Copying 5 items".</summary>
    public string Title { get; init; } = "";
    public string? Dest { get; init; }
    public DateTime Started { get; init; } = DateTime.Now;
    /// <summary>Set by the worker before <see cref="State"/> leaves Running.</summary>
    public DateTime? Finished { get; set; }
    public volatile bool Cancelling;
    public long Done;     // Interlocked
    public long Total;    // Interlocked
    public string Current = ""; // Volatile
    private volatile JobState _state = JobState.Running;
    /// <summary>Written last by the worker (volatile), so Error / Finished are visible once it is not Running.</summary>
    public JobState State { get => _state; set => _state = value; }
    /// <summary>The failure message when State is Failed.</summary>
    public string? Error { get; set; }
    /// <summary>Raised on the worker thread when the job finished (any state).</summary>
    public event Action<Job>? Completed;

    internal readonly CancellationTokenSource Cts = new();
    private readonly ManualResetEventSlim _finished = new(false);

    public void Cancel()
    {
        Cancelling = true;
        try { Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Blocks until the job has finished (Completed already raised). False on timeout.</summary>
    public bool Wait(int millisecondsTimeout = Timeout.Infinite) => _finished.Wait(millisecondsTimeout);

    /// <summary>True once cancel was requested (also when the UI only set <see cref="Cancelling"/>).</summary>
    internal bool IsCancelled
    {
        get
        {
            if (Cts.IsCancellationRequested) return true;
            if (!Cancelling) return false;
            Cancel(); // propagate a bare Cancelling = true to the token the Archive / Sftp APIs watch
            return true;
        }
    }

    internal void Publish(long done, long total, string current)
    {
        Interlocked.Exchange(ref Done, done);
        Interlocked.Exchange(ref Total, total);
        Volatile.Write(ref Current, current);
    }

    internal void Finish(JobState state, string? error)
    {
        Error = error;
        Finished = DateTime.Now;
        State = state; // volatile write publishes Error / Finished
        try { Completed?.Invoke(this); }
        finally { _finished.Set(); }
    }

}

/// <summary>Error that carries the exact user-facing message (Rust's Err(String)).</summary>
internal sealed class OpException(string message) : Exception(message);

internal sealed class Progress(Job job, long total)
{
    public long Done;
    public long Total = total;
    private readonly Stopwatch _last = Stopwatch.StartNew();
    /// <summary>merging into an existing folder: only replace files that are older than the source</summary>
    public bool NewerOnly;

    public void Send(string current)
    {
        _last.Restart();
        job.Publish(Done, Total, VPath.FileName(current) ?? "");
    }

    public void Advance(long n, string current)
    {
        Done += n;
        Send(current);
    }

    /// <summary>Progress inside a big file: reported at most every 100 ms.</summary>
    public void AdvanceChunk(long n, string current)
    {
        Done += n;
        if (_last.ElapsedMilliseconds >= 100) Send(current);
    }
}

public static class Ops
{
    private static long _nextId;

    public static string Verb(this OpKind k) => k switch
    {
        OpKind.Zip => "Zipping",
        OpKind.Copy => "Copying",
        OpKind.Move => "Moving",
        OpKind.Trash => "Recycling",
        _ => "Deleting",
    };

    public static Job Spawn(OpKind kind, IReadOnlyList<string> srcs, string? dest) => SpawnWith(kind, srcs, dest, new());

    /// <summary>policies maps a source path to what to do when its target exists (default: keep both).</summary>
    public static Job SpawnWith(OpKind kind, IReadOnlyList<string> srcs, string? dest, Dictionary<string, Policy> policies)
    {
        var what = srcs.Count == 1 ? VPath.FileName(srcs[0]) ?? "" : $"{srcs.Count} items";
        var job = new Job
        {
            Id = Interlocked.Increment(ref _nextId),
            Kind = kind,
            Title = $"{kind.Verb()} {what}",
            Dest = dest,
        };
        var list = srcs.ToList();
        var pol = new Dictionary<string, Policy>(policies, policies.Comparer);
        var t = new Thread(() =>
        {
            JobState state;
            string? error = null;
            try
            {
                Run(kind, list, dest, pol, job);
                state = job.IsCancelled ? JobState.Cancelled : JobState.Done;
            }
            catch (Exception e) when (IsCancel(e))
            {
                state = JobState.Cancelled;
            }
            catch (Exception e)
            {
                state = JobState.Failed;
                error = e.Message;
            }
            job.Finish(state, error);
        })
        {
            IsBackground = true,
            Name = $"mavyex op {job.Id}",
        };
        // The Recycle Bin goes through the shell's IFileOperation, which wants an STA thread.
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return job;
    }

    private static bool IsCancel(Exception e) => e is OperationCanceledException || e.Message == "Cancelled";

    private static string Name(string p) => VPath.FileName(p) ?? "";

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>Modification time following links (Rust fs::metadata().modified()); null when missing.</summary>
    private static DateTime? Mtime(string p)
    {
        try
        {
            if (File.Exists(p)) return File.GetLastWriteTimeUtc(p);
            if (Directory.Exists(p)) return Directory.GetLastWriteTimeUtc(p);
        }
        catch { }
        return null;
    }

    /// <summary>Like Rust's symlink_metadata().is_dir(): a real folder, not a link / junction to one.</summary>
    private static bool IsRealDir(FileAttributes a) =>
        a.HasFlag(FileAttributes.Directory) && !a.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Bytes of a file or folder tree (every item counts at least 1 so progress moves for empty ones).</summary>
    internal static long TreeSize(string p)
    {
        try
        {
            var a = File.GetAttributes(p);
            if (IsRealDir(a))
            {
                long sum = 0;
                try
                {
                    foreach (var e in Directory.EnumerateFileSystemEntries(p)) sum += TreeSize(e);
                }
                catch { }
                return Math.Max(sum, 1);
            }
            return Math.Max(new FileInfo(p).Length, 1);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>"name.ext" -> "name (2).ext" until it does not exist (local folders).</summary>
    public static string UniqueDest(string dir, string name)
    {
        var first = VPath.Join(dir, name);
        if (!Exists(first)) return first;
        // Rust file_stem / extension: ".bashrc" has no extension, "a.tar.gz" -> "a.tar" + ".gz"
        int i = name.LastIndexOf('.');
        var (stem, ext) = i <= 0 ? (name, "") : (name[..i], name[i..]);
        for (int n = 2; ; n++)
        {
            var p = VPath.Join(dir, $"{stem} ({n}){ext}");
            if (!Exists(p)) return p;
        }
    }

    /// <summary>Where src goes inside dest under policy; null = leave it alone.</summary>
    internal static string? TargetFor(string dest, string src, string name, Policy policy)
    {
        var t = VPath.Join(dest, name);
        if (!Exists(t)) return t;
        if (VPath.Same(t, src)) return UniqueDest(dest, name);
        switch (policy)
        {
            case Policy.Rename: return UniqueDest(dest, name);
            case Policy.Overwrite: return t;
            case Policy.Skip: return null;
            default:
                if (Directory.Exists(t)) return t; // merged; files are compared one by one
                var (a, b) = (Mtime(src), Mtime(t));
                bool newer = a is not { } x || b is not { } y || x > y;
                return newer ? t : null;
        }
    }

    private static Policy PolicyOf(Dictionary<string, Policy> policies, string src)
    {
        if (policies.TryGetValue(src, out var p)) return p;
        foreach (var (k, v) in policies)
            if (VPath.Same(k, src)) return v;
        return Policy.Rename;
    }

    private static void Run(OpKind kind, List<string> srcs, string? dest, Dictionary<string, Policy> policies, Job job)
    {
        var cancel = job.Cts.Token;
        if (kind == OpKind.Zip)
        {
            var zip = dest ?? throw new OpException("no destination");
            var total = Archive.TreeTotal(srcs);
            long done = 0;
            var last = Stopwatch.StartNew();
            bool first = true; // Rust started the clock 1 s in the past: the first chunk is reported at once
            job.Publish(0, total, "");
            Archive.Create(srcs, zip, cancel, (n, p) =>
            {
                done += n;
                if (first || last.ElapsedMilliseconds >= 100)
                {
                    first = false;
                    last.Restart();
                    job.Publish(done, total, Name(p));
                }
                _ = job.IsCancelled;
            });
            return;
        }
        // Writes inside an archive work on its staging copy (the archive is rewritten when the user leaves it).
        // `orig` keeps the caller's path for each source so the policy lookup still finds it after the mapping.
        var orig = srcs.ToList();
        if (dest != null && Archive.InArchive(dest))
        {
            job.Publish(0, 1, "Preparing archive…");
            dest = Archive.RealForWrite(dest);
        }
        if (kind is OpKind.Trash or OpKind.Delete or OpKind.Move && srcs.Any(s => Archive.SplitInner(s) != null))
        {
            job.Publish(0, 1, "Preparing archive…");
            for (int i = 0; i < srcs.Count; i++)
                if (Archive.SplitInner(srcs[i]) != null) srcs[i] = Archive.RealForWrite(srcs[i]);
            if (kind == OpKind.Trash) kind = OpKind.Delete; // there is no recycle bin inside an archive
        }
        var errors = new List<string>();
        switch (kind)
        {
            case OpKind.Trash:
                job.Publish(0, 1, $"{srcs.Count} item(s)");
                if (srcs.Any(Sftp.IsRemote))
                    throw new OpException("Remote files can't go to the Recycle Bin – use permanent delete");
                RecycleBin.DeleteAll(srcs);
                break;
            case OpKind.Delete:
            {
                var pr = new Progress(job, srcs.Count);
                foreach (var s in srcs)
                {
                    if (job.IsCancelled) throw new OperationCanceledException();
                    try
                    {
                        if (Sftp.IsRemote(s)) Sftp.RemoveAll(s);
                        else RemoveAny(s);
                    }
                    catch (Exception e)
                    {
                        errors.Add($"{s}: {e.Message}");
                    }
                    pr.Advance(1, s);
                }
                break;
            }
            case OpKind.Copy:
            case OpKind.Move:
            {
                if (dest == null) throw new OpException("no destination");
                long SrcSize(string s)
                {
                    try
                    {
                        if (Sftp.IsRemote(s)) return Sftp.SizeOf(s);
                        return Archive.SplitInner(s) is { } a ? Archive.Size(a.Zip, a.Inner) : TreeSize(s);
                    }
                    catch
                    {
                        return 1;
                    }
                }
                var pr = new Progress(job, srcs.Sum(SrcSize));
                for (int i = 0; i < srcs.Count; i++)
                {
                    var s = srcs[i];
                    if (job.IsCancelled) throw new OperationCanceledException();
                    if (VPath.FileName(s) is not { } name) continue;
                    var policy = PolicyOf(policies, orig[i]);
                    pr.NewerOnly = policy == Policy.Newer;
                    bool rs = Sftp.IsRemote(s), rd = Sftp.IsRemote(dest);
                    if (rs || rd)
                    {
                        if (rs && rd)
                        {
                            errors.Add($"{s}: copying between two servers is not supported (copy to a local folder first)");
                            continue;
                        }
                        string? target;
                        if (rs) target = TargetFor(dest, s, name, policy);
                        else
                        {
                            var direct = VPath.Join(dest, name);
                            target = policy switch
                            {
                                Policy.Overwrite or Policy.Newer => direct,
                                Policy.Skip => Sftp.Exists(direct) ? null : direct,
                                _ => Sftp.UniqueRemote(dest, name),
                            };
                        }
                        if (target == null)
                        {
                            pr.Advance(SrcSize(s), s); // skipped
                            continue;
                        }
                        try
                        {
                            if (rs) Sftp.Download(s, target, cancel, n => pr.Advance(n, s));
                            else Sftp.Upload(s, target, cancel, n => pr.Advance(n, s));
                            if (kind == OpKind.Move)
                            {
                                try
                                {
                                    if (rs) Sftp.RemoveAll(s);
                                    else RemoveAny(s);
                                }
                                catch (Exception e)
                                {
                                    throw new OpException($"copied, but could not remove source: {e.Message}");
                                }
                            }
                        }
                        catch (Exception e) when (IsCancel(e))
                        {
                            throw new OperationCanceledException();
                        }
                        catch (Exception e)
                        {
                            errors.Add($"{s}: {e.Message}");
                        }
                        continue;
                    }
                    if (Archive.SplitInner(s) is { } inside)
                    {
                        // copying out of an archive = extracting
                        if (kind == OpKind.Move)
                        {
                            errors.Add($"{s}: archives are read-only");
                            continue;
                        }
                        if (TargetFor(dest, s, name, policy) is not { } target)
                        {
                            pr.Advance(SrcSize(s), s); // skipped
                            continue;
                        }
                        try
                        {
                            Archive.Extract(inside.Zip, inside.Inner, target, cancel, n => pr.Advance(n, s));
                        }
                        catch (Exception e)
                        {
                            errors.Add($"{s}: {(e is OperationCanceledException ? "Cancelled" : e.Message)}");
                        }
                        continue;
                    }
                    if (kind == OpKind.Move && VPath.Parent(s) is { } parent && VPath.Same(parent, dest))
                        continue; // already there
                    if (VPath.StartsWith(dest, s))
                    {
                        errors.Add($"{s}: cannot place a folder inside itself");
                        continue;
                    }
                    var size = TreeSize(s);
                    if (TargetFor(dest, s, name, policy) is not { } tgt)
                    {
                        pr.Advance(size, s); // skipped, the source stays
                        continue;
                    }
                    try
                    {
                        if (kind == OpKind.Move)
                        {
                            if (Native.Rename(s, tgt))
                                pr.Advance(size, s);
                            else
                            {
                                // another volume, or a folder merged into an existing one
                                CopyTree(s, tgt, pr, job);
                                if (!(pr.NewerOnly && Directory.Exists(s))) // older files were skipped and must stay in the source folder
                                {
                                    try { RemoveAny(s); }
                                    catch (Exception e) { throw new OpException($"copied, but could not remove source: {e.Message}"); }
                                }
                            }
                        }
                        else CopyTree(s, tgt, pr, job);
                    }
                    catch (Exception e)
                    {
                        errors.Add($"{s}: {(e is OperationCanceledException ? "Cancelled" : e.Message)}");
                    }
                }
                break;
            }
        }
        if (job.IsCancelled) throw new OperationCanceledException();
        if (errors.Count > 0)
        {
            var msg = string.Join("; ", errors.Take(3));
            if (errors.Count > 3) msg += $" (+{errors.Count - 3} more)";
            throw new OpException(msg);
        }
    }

    /// <summary>Removes a file, a link (not its target) or a whole folder tree.</summary>
    internal static void RemoveAny(string p)
    {
        var a = File.GetAttributes(p); // throws when missing, like symlink_metadata
        if (a.HasFlag(FileAttributes.Directory))
            Directory.Delete(p, recursive: !a.HasFlag(FileAttributes.ReparsePoint));
        else File.Delete(p);
    }

    internal static void CopyTree(string src, string dst, Progress pr, Job job)
    {
        if (job.IsCancelled) throw new OperationCanceledException();
        var a = File.GetAttributes(src);
        if (IsRealDir(a))
        {
            Directory.CreateDirectory(dst);
            foreach (var e in Directory.EnumerateFileSystemEntries(src))
                CopyTree(e, VPath.Join(dst, Path.GetFileName(e)), pr, job);
            pr.Advance(1, src);
        }
        else
        {
            var len = new FileInfo(src).Length;
            if (pr.NewerOnly && Exists(dst))
            {
                var (s, d) = (Mtime(src), Mtime(dst));
                bool newer = s is not { } x || d is not { } y || x > y;
                if (!newer)
                {
                    pr.Advance(Math.Max(len, 1), src);
                    return;
                }
            }
            CopyFile(src, dst, len, pr, job);
        }
    }

    /// <summary>Small files use the OS copy; big ones are copied in 1 MiB chunks so the
    /// progress window moves and Cancel reacts quickly.</summary>
    internal static void CopyFile(string src, string dst, long len, Progress pr, Job job)
    {
        const long Big = 16L * 1024 * 1024;
        if (len < Big)
        {
            File.Copy(src, dst, overwrite: true); // CopyFile keeps the modification time
            pr.Advance(Math.Max(len, 1), src);
            return;
        }
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1);
        var buf = new byte[1 << 20];
        void Abort()
        {
            output.Dispose();
            try { File.Delete(dst); } catch { }
        }
        while (true)
        {
            if (job.IsCancelled)
            {
                Abort();
                throw new OperationCanceledException();
            }
            int n;
            try
            {
                n = input.Read(buf, 0, buf.Length);
                if (n == 0) break;
                output.Write(buf, 0, n);
            }
            catch
            {
                Abort();
                throw;
            }
            pr.AdvanceChunk(n, src);
        }
        try
        {
            File.SetLastWriteTimeUtc(output.SafeFileHandle, File.GetLastWriteTimeUtc(input.SafeFileHandle)); // keep the original modification time
        }
        catch { }
        output.Dispose();
        pr.Send(src);
    }

    private static class Native
    {
        private const uint MOVEFILE_REPLACE_EXISTING = 0x1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
        private static extern bool MoveFileEx(string from, string to, uint flags);

        /// <summary>Rust fs::rename: MoveFileExW with REPLACE_EXISTING and *without* COPY_ALLOWED, so it fails across
        /// volumes (and onto an existing folder) and the caller falls back to copy + delete with progress.</summary>
        public static bool Rename(string from, string to) =>
            MoveFileEx(Extended(from), Extended(to), MOVEFILE_REPLACE_EXISTING);
    }

    /// <summary>"\\?\" form for paths over MAX_PATH (the app is not long-path aware, raw Win32 calls need it).</summary>
    internal static string Extended(string p)
    {
        if (p.Length < 248 || p.StartsWith(@"\\?\")) return p;
        var full = Path.GetFullPath(p);
        return full.StartsWith(@"\\") ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
