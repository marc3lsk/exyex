using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Mavyex.Core;

namespace Mavyex.Services;

// Port of deep.rs: recursive ("search in subfolders") walker. Runs on a background thread and streams
// matching entries back; the name of each result is its path relative to the searched folder, e.g.
// "src\ui\README.md". With opts.Content the query is matched against the *lines* of every file instead
// of its name (a small worker pool scans files in parallel).

/// <summary>A running subfolder (or content) search. Results are drained by the UI with <see cref="Drain"/>.
/// Dispose cancels the walk.</summary>
public sealed class DeepJob : IDisposable
{
    /// <summary>Hard cap so a too-broad query cannot flood the list.</summary>
    public const int MaxResults = 50_000;

    private readonly object _lock = new();
    private List<Entry> _pending = [];
    private bool _done, _doneTruncated;
    /// <summary>A callback was sent and the UI has not drained since: further results don't call it again.</summary>
    private bool _notified;
    private readonly Action? _onResults;
    private readonly CancellationTokenSource _cts = new();

    internal DeepJob(Action? onResults)
    {
        _onResults = onResults;
        Running = true;
    }

    public bool Running { get; internal set; }
    public bool Truncated { get; internal set; }

    internal CancellationToken Token => _cts.Token;
    internal bool Cancelled => _cts.IsCancellationRequested;

    /// <summary>Entries found since the last call (thread-safe); also updates Running / Truncated.</summary>
    public List<Entry> Drain()
    {
        lock (_lock)
        {
            var r = _pending;
            _pending = [];
            _notified = false;
            // _done is set only after the last results were queued, so nothing is lost
            if (_done)
            {
                Running = false;
                Truncated = _doneTruncated;
            }
            return r;
        }
    }

    // The CTS is not disposed: worker threads may still read the token after the UI dropped the job.
    public void Dispose() => _cts.Cancel();

    /// <summary>Queues results (worker side) and pokes the UI once per drain.</summary>
    internal void Push(List<Entry> batch)
    {
        bool notify;
        lock (_lock)
        {
            _pending.AddRange(batch);
            notify = !_notified;
            _notified = true;
        }
        if (notify) Notify();
    }

    internal void Finish(bool truncated)
    {
        lock (_lock)
        {
            _done = true;
            _doneTruncated = truncated;
            _notified = true;
        }
        Notify(); // always: the UI must see Running = false
    }

    private void Notify()
    {
        if (Cancelled || _onResults is null) return;
        try
        {
            _onResults();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"deep: onResults failed: {ex}");
        }
    }
}

public static class Deep
{
    /// <summary>Larger files are not scanned for content.</summary>
    private const long MaxScanBytes = 8 * 1024 * 1024;
    private const int ScanThreads = 4;
    private const int BatchSize = 500;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(80);

    /// <summary>Splits the manual exclude list: names / globs separated by ',' or ';'.</summary>
    public static List<string> ParseExclude(string s) =>
        s.Split([',', ';']).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    /// <summary>Starts the background walk. Result names are paths relative to root; content search fills Entry.Snippet.
    /// onResults is called (from a worker thread) whenever new results are available, so the UI can schedule a refresh.</summary>
    public static DeepJob Spawn(string root, string query, SearchOpts opts, List<string> exclude, bool showHidden, Action? onResults = null) =>
        Spawn(root, query, opts, exclude, showHidden, onResults, DeepJob.MaxResults);

    /// <summary>Same with a custom result cap (tests).</summary>
    internal static DeepJob Spawn(string root, string query, SearchOpts opts, List<string> exclude, bool showHidden, Action? onResults, int maxResults)
    {
        var job = new DeepJob(onResults);
        var ex = exclude.ToList(); // the caller may reuse its list
        var t = new Thread(() => Walk(job, root, query, opts, ex, showHidden, maxResults))
        {
            IsBackground = true,
            Name = "mavyex deep search",
        };
        t.Start();
        return job;
    }

    /// <summary>Symlinks / junctions are listed but not descended into (cycles, duplicates).</summary>
    private static bool IsReparse(string p)
    {
        if (Sftp.IsRemote(p)) return false;
        try
        {
            return File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false; // archive-inner paths etc.
        }
    }

    /// <summary>Shared state of one content search.</summary>
    private sealed class ScanCtx(DeepJob job, Matcher matcher)
    {
        public readonly DeepJob Job = job;
        public readonly Matcher Matcher = matcher;
        public int Hits;
        public volatile bool Halt;
        public bool Stop => Halt || Job.Cancelled;
    }

    /// <summary>Scans one file; queues it (with a snippet) when some line matches.</summary>
    private static void ScanFile(Entry e, ScanCtx ctx)
    {
        if (e.Size == 0 || e.Size > MaxScanBytes) return;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(e.Path); // local files only, like Rust's fs::read
        }
        catch
        {
            return;
        }
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 4096)) >= 0) return; // binary
        var text = Encoding.UTF8.GetString(bytes); // invalid sequences become U+FFFD (from_utf8_lossy)
        int count = 0, firstNo = 0;
        string? first = null;
        // str::lines(): split at '\n', drop a '\r' before it, no empty line after a final '\n'
        int n = 0;
        for (int start = 0; start < text.Length; n++)
        {
            if (n % 4096 == 0 && ctx.Stop) return;
            int nl = text.IndexOf('\n', start);
            int end = nl < 0 ? text.Length : nl;
            int len = end - start;
            if (nl >= 0 && len > 0 && text[end - 1] == '\r') len--;
            var line = text.Substring(start, Math.Min(len, 2000));
            start = nl < 0 ? text.Length : nl + 1;
            if (ctx.Matcher.Score(line) is not null)
            {
                count++;
                if (first is null)
                {
                    firstNo = n + 1;
                    var tr = line.Trim();
                    first = tr.Length > 140 ? tr[..140] : tr;
                }
            }
        }
        if (first is null) return;
        e.Snippet = $"{firstNo}: {first}" + (count > 1 ? $"   (+{count - 1} more)" : "");
        Interlocked.Increment(ref ctx.Hits);
        ctx.Job.Push([e]);
    }

    private static void Walk(DeepJob job, string root, string query, SearchOpts opts, List<string> exclude, bool showHidden, int maxResults)
    {
        bool truncated = false;
        try
        {
            truncated = WalkCore(job, root, query, opts, exclude, showHidden, maxResults, out bool cancelled);
            if (cancelled) return;
        }
        catch (Exception ex)
        {
            // never leave the UI with a job that runs forever
            Debug.WriteLine($"deep: walker failed: {ex}");
            if (job.Cancelled) return;
        }
        job.Finish(truncated);
    }

    /// <summary>Returns "truncated"; cancelled = the job was disposed (no Done is reported then).</summary>
    private static bool WalkCore(DeepJob job, string root, string query, SearchOpts opts, List<string> exclude, bool showHidden, int maxResults, out bool cancelled)
    {
        cancelled = false;
        var matcher = Search.Compile(query, opts);
        // content mode needs a text pattern; with only :filters it just lists files
        bool content = opts.Content && !matcher.NameAll;
        var ctx = new ScanCtx(job, matcher);

        using var files = new BlockingCollection<Entry>(512);
        var workers = new List<Thread>();
        if (content)
        {
            for (int i = 0; i < ScanThreads; i++)
            {
                var w = new Thread(() =>
                {
                    try
                    {
                        foreach (var e in files.GetConsumingEnumerable(job.Token))
                        {
                            if (ctx.Stop) break;
                            ScanFile(e, ctx);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"deep: scanner failed: {ex}");
                    }
                })
                {
                    IsBackground = true,
                    Name = "mavyex content scan",
                };
                w.Start();
                workers.Add(w);
            }
        }

        bool truncated = false;
        try
        {
            var queue = new Queue<(string Dir, string Prefix)>();
            queue.Enqueue((root, ""));
            var batch = new List<Entry>();
            int total = 0;
            var last = Stopwatch.StartNew();
            while (queue.Count > 0 && !truncated)
            {
                var (dir, prefix) = queue.Dequeue();
                List<Entry> items;
                try
                {
                    items = Fs.ReadDir(dir);
                }
                catch
                {
                    continue; // unreadable folder: skip, like Rust's `let Ok(..) else continue`
                }
                foreach (var e in items)
                {
                    if (job.Cancelled)
                    {
                        cancelled = true;
                        return false;
                    }
                    if (!showHidden && e.Hidden) continue;
                    if (exclude.Any(x => Search.GlobMatch(x, e.Name, false))) continue;
                    var rel = prefix + e.Name;
                    if (e.IsDir && !IsReparse(e.Path)) queue.Enqueue((e.Path, rel + "\\"));
                    if (!matcher.FiltersOk(e)) continue;
                    if (content)
                    {
                        if (!e.IsDir)
                        {
                            e.Name = rel;
                            e.Deep = true;
                            try
                            {
                                files.Add(e, job.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                cancelled = true;
                                return false;
                            }
                        }
                        if (Volatile.Read(ref ctx.Hits) >= maxResults)
                        {
                            truncated = true;
                            ctx.Halt = true;
                            break;
                        }
                        continue;
                    }
                    if (matcher.Score(rel) is not null)
                    {
                        e.Name = rel;
                        e.Deep = true;
                        batch.Add(e);
                        total++;
                        if (total >= maxResults)
                        {
                            truncated = true;
                            break;
                        }
                    }
                    if (batch.Count > 0 && (batch.Count >= BatchSize || last.Elapsed >= BatchInterval))
                    {
                        job.Push(batch);
                        batch = [];
                        last.Restart();
                    }
                }
            }
            files.CompleteAdding();
            foreach (var w in workers) w.Join();
            if (job.Cancelled)
            {
                cancelled = true;
                return false;
            }
            if (batch.Count > 0) job.Push(batch);
            return truncated;
        }
        finally
        {
            // early returns: release the scanners (they also watch the token) before `files` is disposed
            if (!files.IsAddingCompleted) files.CompleteAdding();
            foreach (var w in workers) w.Join();
        }
    }
}
