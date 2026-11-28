using System.Collections.Concurrent;
using System.Security.Cryptography;
using Mavyex.Core;

namespace Mavyex.Services;

/// <summary>
/// SHA-256 of file contents for the compare mode (Ctrl+M), computed in the background and cached by (path, size, mtime).
/// Local files are hashed here, archive members through <see cref="Archive.ReadHead"/>, SFTP files on the server with
/// `sha256sum` (<see cref="Sftp.Sha256Many"/>) – batched per server, nothing is downloaded.
/// </summary>
public static class ContentHash
{
    /// <summary>Archive members up to this size are hashed (they are read into memory).</summary>
    private const long MaxArchiveBytes = 64L * 1024 * 1024;

    private sealed record Key(string Path, long Size, DateTime? Mod);

    public enum State { Pending, Ready, Failed }

    /// <summary>null value = hashing failed.</summary>
    private static readonly ConcurrentDictionary<Key, string?> done = new();
    private static readonly ConcurrentDictionary<Key, byte> pending = new();
    private static readonly SemaphoreSlim localSlots = new(2);
    private static readonly object queueLock = new();
    private static readonly List<Key> remoteQueue = [];
    private static bool remoteWorker;

    /// <summary>Raised (from a worker thread) when a hash finished – the UI recomputes the compare marks.</summary>
    public static event Action? Changed;

    public static bool AnyPending => !pending.IsEmpty;

    /// <summary>The hash of an entry if known; starts computing it otherwise.</summary>
    public static State Get(Entry e, out string? hash)
    {
        var k = new Key(e.Path, e.Size, e.Modified);
        if (done.TryGetValue(k, out hash)) return hash == null ? State.Failed : State.Ready;
        if (pending.TryAdd(k, 0)) Start(k);
        return State.Pending;
    }

    private static void Finish(Key k, string? hash)
    {
        done[k] = hash;
        pending.TryRemove(k, out _);
        if (done.Count > 20_000) done.Clear(); // bounded: folders change, old keys are useless
        try { Changed?.Invoke(); } catch { }
    }

    private static void Start(Key k)
    {
        if (Sftp.IsRemote(k.Path))
        {
            lock (queueLock)
            {
                remoteQueue.Add(k);
                if (remoteWorker) return;
                remoteWorker = true;
            }
            Task.Run(RemoteWorker);
            return;
        }
        Task.Run(async () =>
        {
            await localSlots.WaitAsync();
            try { Finish(k, HashLocalOrArchive(k)); }
            catch { Finish(k, null); }
            finally { localSlots.Release(); }
        });
    }

    private static string? HashLocalOrArchive(Key k)
    {
        if (Archive.SplitInner(k.Path) is { } a)
        {
            if (k.Size > MaxArchiveBytes) return null;
            var bytes = Archive.ReadHead(a.Zip, a.Inner, (int)k.Size + 1);
            return bytes.Length == k.Size ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null;
        }
        using var f = new FileStream(k.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>Collects requests for a moment, then runs one `sha256sum` per server for all of them.</summary>
    private static void RemoteWorker()
    {
        while (true)
        {
            Thread.Sleep(150);
            List<Key> batch;
            lock (queueLock)
            {
                if (remoteQueue.Count == 0)
                {
                    remoteWorker = false;
                    return;
                }
                batch = [.. remoteQueue];
                remoteQueue.Clear();
            }
            foreach (var group in batch.GroupBy(k => Sftp.ConnKey(k.Path) ?? ""))
            {
                Dictionary<string, string> sums;
                try { sums = Sftp.Sha256Many(group.Select(k => k.Path).Distinct().ToList()); }
                catch { sums = []; } // no sha256sum / connection failed: these files fall back to the date rule
                foreach (var k in group) Finish(k, sums.TryGetValue(k.Path, out var h) ? h : null);
            }
        }
    }
}
