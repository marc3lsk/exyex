using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Mavyex.Core;

namespace Mavyex.Services;

/// <summary>
/// Archive support (port of archive.rs): zip/jar, tar, tar.gz/tgz and plain gz behave like folders. A path such as
/// <c>C:\x\a.zip\docs\readme.txt</c> is "virtual": the part up to <c>a.zip</c> exists on disk, the rest lives inside the
/// archive. A plain <c>.gz</c> holds one compressed file; it is shown as a folder containing that file (named after the
/// archive without <c>.gz</c>).
/// <para>Browsing reads the archive directly (tar/gz have no central directory, so their listing is built by one pass over
/// the stream and cached by mtime/size). The first *write* (copy in, delete, rename, F4 edit ...) extracts the whole archive
/// into a staging folder (an "edit session"); from then on listing / reading use that folder. When the user leaves the
/// archive, the app asks whether to write the staging folder back (<see cref="Save"/>).</para>
/// <para>Errors: <see cref="IOException"/> with a user-readable message (Rust's <c>Err(String)</c>); cancellation:
/// <see cref="OperationCanceledException"/>. Safe to call from the UI thread and worker threads.</para>
/// </summary>
public static class Archive
{
    private enum Kind { Zip, Tar, TarGz, Gz }

    private static Kind? KindOf(string p)
    {
        var n = VPath.FileName(p)?.ToLowerInvariant();
        if (n == null) return null;
        if (n.EndsWith(".zip") || n.EndsWith(".jar")) return Kind.Zip;
        if (n.EndsWith(".tar.gz") || n.EndsWith(".tgz")) return Kind.TarGz;
        if (n.EndsWith(".tar")) return Kind.Tar;
        if (n.EndsWith(".gz")) return Kind.Gz;
        return null;
    }

    private const string GzOne = "A .gz file holds exactly one file – use .tar.gz (or .tgz) for folders and several files";

    /// <summary>Extension says it is an archive we can browse (and write).</summary>
    public static bool IsArchiveName(string p) => KindOf(p) != null;

    /// <summary>The name says single-file gzip (<c>.gz</c>, but not <c>.tar.gz</c>).</summary>
    public static bool IsPlainGz(string p) => KindOf(p) == Kind.Gz;

    /// <summary>(archive file, inner path with '/' separators, "" = root) when an ancestor (or p itself) is an existing archive file.</summary>
    public static (string Zip, string Inner)? Split(string p)
    {
        var comps = VPath.Components(p);
        bool rooted = VPath.RootLength(p) > 0;
        var acc = "";
        for (int i = 0; i < comps.Count; i++)
        {
            // Rust's Path::components drops "." except at the very start of a relative path
            if (comps[i] == "." && (i > 0 || rooted)) continue;
            acc = acc.Length == 0 ? comps[i] : VPath.Join(acc, comps[i]);
            if (IsArchiveName(acc) && File.Exists(acc))
                return (acc, string.Join('/', comps.Skip(i + 1).Where(c => c != ".")));
        }
        return null;
    }

    /// <summary>Like Split, but only for something *inside* (inner not empty).</summary>
    public static (string Zip, string Inner)? SplitInner(string p) => Split(p) is { } s && s.Inner.Length > 0 ? s : null;

    public static bool InArchive(string p) => Split(p) != null;

    // ------------------------------------------------------------------ helpers

    private static string VirtualPath(string zip, string inner) =>
        inner.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(zip, VPath.Join);

    private static string StagePath(string dir, string inner) => VirtualPath(dir, inner);

    /// <summary>Normalizes a stored entry name: '/' separators, no "./", no leading or trailing '/'. Returns the name and
    /// whether it was marked as a folder (trailing '/'); null for empty names and names with ".." (never shown or written
    /// outside the target).</summary>
    private static (string Name, bool IsDir)? Clean(string name)
    {
        var n = name.Replace('\\', '/');
        bool isDir = n.EndsWith('/');
        var parts = n.Split('/').Where(x => x.Length > 0 && x != ".").ToList();
        if (parts.Count == 0 || parts.Contains("..")) return null;
        return (string.Join('/', parts), isDir);
    }

    /// <summary>Name of the single file inside a .gz: the archive name without ".gz".</summary>
    private static string GzName(string arc)
    {
        var n = VPath.FileName(arc) ?? "";
        var stem = n.Length >= 3 ? n[..^3] : "";
        return stem.Length == 0 ? "data" : stem;
    }

    /// <summary>Uncompressed size from the gzip trailer (ISIZE, modulo 4 GiB).</summary>
    private static long? GzSize(string p)
    {
        try
        {
            using var f = OpenRead(p);
            if (f.Length < 18) return null;
            f.Seek(-4, SeekOrigin.End);
            Span<byte> b = stackalloc byte[4];
            f.ReadExactly(b);
            return BitConverter.ToUInt32(b);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Rust opens files with read/write/delete sharing on Windows: don't lock archives others are using.
    private static FileStream OpenRead(string p) =>
        new(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

    private static OperationCanceledException Cancelled(CancellationToken c) => new("Cancelled", c);

    private static void CheckCancel(CancellationToken c)
    {
        if (c.IsCancellationRequested) throw Cancelled(c);
    }

    /// <summary>Wraps errors from opening/reading a source path as "{path}: {message}" (Rust's format).</summary>
    private static T At<T>(string path, Func<T> f)
    {
        try
        {
            return f();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{path}: {e.Message}", e);
        }
    }

    private static DateTime? ZipTime(ZipArchiveEntry z)
    {
        try
        {
            return z.LastWriteTime.UtcDateTime; // DOS time, interpreted as local time (like the Rust version)
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ZipArchive OpenZip(string zip)
    {
        var fs = OpenRead(zip);
        try
        {
            return new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException e)
        {
            fs.Dispose();
            throw new IOException(e.Message, e);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private readonly record struct Meta(string Name, bool IsDir, long Size, DateTime? Modified);

    /// <summary>Visits the entries of an archive in stored order; <c>open</c> gives the content stream (valid only during
    /// the callback). The callback returns false to stop.</summary>
    private static void Each(string arc, Func<Meta, Func<Stream>, bool> f)
    {
        var k = KindOf(arc) ?? throw new IOException("not an archive");
        try
        {
            switch (k)
            {
                case Kind.Zip:
                {
                    using var ar = OpenZip(arc);
                    foreach (var z in ar.Entries)
                    {
                        if (Clean(z.FullName) is not { } c) continue;
                        // opened lazily: entries the callback skips are never decompressed
                        Stream? s = null;
                        try
                        {
                            var m = new Meta(c.Name, c.IsDir, z.Length, ZipTime(z));
                            if (!f(m, () => s ??= z.Open())) break;
                        }
                        finally
                        {
                            s?.Dispose();
                        }
                    }
                    break;
                }
                case Kind.Tar:
                {
                    using var fs = OpenRead(arc);
                    TarEach(fs, f);
                    break;
                }
                case Kind.TarGz:
                {
                    using var fs = OpenRead(arc);
                    using var gz = new GZipStream(fs, CompressionMode.Decompress); // also reads concatenated members
                    TarEach(gz, f);
                    break;
                }
                case Kind.Gz:
                {
                    var name = GzName(arc);
                    DateTime? modified = File.Exists(arc) ? File.GetLastWriteTimeUtc(arc) : null;
                    using var fs = OpenRead(arc);
                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    f(new Meta(name, false, GzSize(arc) ?? 0, modified), () => gz);
                    break;
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or FormatException)
        {
            throw new IOException(e.Message, e);
        }
    }

    private static void TarEach(Stream r, Func<Meta, Func<Stream>, bool> f)
    {
        using var tr = new TarReader(r, leaveOpen: true);
        while (tr.GetNextEntry(copyData: false) is { } e)
        {
            bool isFile = e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;
            bool isDir = e.EntryType == TarEntryType.Directory;
            if (!(isFile || isDir)) continue; // links, devices, sparse files, extended headers ...
            if (Clean(e.Name) is not { } c) continue;
            DateTime? modified = null;
            try
            {
                modified = e.ModificationTime.UtcDateTime;
            }
            catch (ArgumentOutOfRangeException) { }
            var m = new Meta(c.Name, c.IsDir || isDir, e.Length, modified);
            if (!f(m, () => e.DataStream ?? Stream.Null)) break;
        }
    }

    // ------------------------------------------------------------------ listing index

    private sealed record Item(string Name, bool IsDir, long Size, DateTime? Modified);

    /// <summary>Listings of archive files, keyed by lower-case path and invalidated by (mtime, size).</summary>
    private static readonly Dictionary<string, ((DateTime? Mtime, long Len) Stamp, List<Item> Items)> IndexCache = [];

    private static List<Item> Index(string arc)
    {
        var fi = new FileInfo(arc);
        if (!fi.Exists) throw new FileNotFoundException($"Could not find file '{arc}'.", arc);
        var stamp = ((DateTime?)fi.LastWriteTimeUtc, fi.Length);
        lock (IndexCache)
        {
            if (IndexCache.TryGetValue(Key(arc), out var hit) && hit.Stamp == stamp) return hit.Items;
        }
        var v = BuildIndex(arc);
        lock (IndexCache)
        {
            if (IndexCache.Count > 32) IndexCache.Clear();
            IndexCache[Key(arc)] = (stamp, v);
        }
        return v;
    }

    private static List<Item> BuildIndex(string arc)
    {
        var outList = new List<Item>();
        if (KindOf(arc) == Kind.Zip)
        {
            // the central directory is enough (also lists encrypted entries)
            using var ar = OpenZip(arc);
            foreach (var z in ar.Entries)
                if (Clean(z.FullName) is { } c) outList.Add(new Item(c.Name, c.IsDir, z.Length, ZipTime(z)));
            return outList;
        }
        Each(arc, (m, _) =>
        {
            outList.Add(new Item(m.Name, m.IsDir, m.Size, m.Modified));
            return true;
        });
        return outList;
    }

    // ------------------------------------------------------------------ edit sessions

    private sealed class Session
    {
        public required string Zip;
        /// <summary>Staging folder holding the extracted archive.</summary>
        public required string Dir;
        public bool Dirty;
        /// <summary>Files opened in an editor: (staging file, mtime, size) when the editor was started.</summary>
        public readonly List<(string File, DateTime? Mtime, long Len)> Edits = [];
    }

    private static readonly Dictionary<string, Session> SessionMap = [];
    // Serializes Ensure: two threads starting a session at once would extract into the same staging folder.
    private static readonly Lock EnsureLock = new();

    private static string Key(string zip) => zip.ToLowerInvariant();

    /// <summary>FNV-1a 64: deterministic across runs (string.GetHashCode is randomized per process).</summary>
    private static ulong Hash(string s, ulong h = 0xcbf29ce484222325)
    {
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 0x100000001b3;
        }
        return h;
    }

    private static string StagingDir(string zip)
    {
        var h = Hash(Environment.ProcessId.ToString(), Hash(Key(zip)));
        var name = VPath.FileName(zip) ?? "";
        return Path.Combine(Path.GetTempPath(), "mavyex-archive-edit", $"{h:x}-{name}");
    }

    /// <summary>Staging folder of an active edit session, or null.</summary>
    public static string? Active(string zip)
    {
        lock (SessionMap) return SessionMap.TryGetValue(Key(zip), out var s) ? s.Dir : null;
    }

    /// <summary>Starts (or returns) the edit session: extracts the archive into a staging folder.</summary>
    public static string Ensure(string zip)
    {
        lock (EnsureLock)
        {
            if (Active(zip) is { } d) return d;
            var dir = StagingDir(zip);
            TryDeleteDir(dir);
            Directory.CreateDirectory(dir);
            try
            {
                Extract(zip, "", dir, CancellationToken.None, _ => { });
            }
            catch
            {
                TryDeleteDir(dir);
                throw;
            }
            lock (SessionMap) SessionMap[Key(zip)] = new Session { Zip = zip, Dir = dir };
            return dir;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Real (staging) path to write to for a virtual path inside an archive (starts the session) and marks the
    /// archive as changed.</summary>
    public static string RealForWrite(string p)
    {
        var (zip, inner) = Split(p) ?? throw new IOException("not inside an archive");
        var dir = Ensure(zip);
        lock (SessionMap)
        {
            if (SessionMap.TryGetValue(Key(zip), out var s)) s.Dirty = true;
        }
        return StagePath(dir, inner);
    }

    /// <summary>Real (staging) path of an existing inner file for editing (F4); remembers its state so that a later change
    /// marks the archive as modified.</summary>
    public static string RealForEdit(string p)
    {
        var (zip, inner) = SplitInner(p) ?? throw new IOException("not inside an archive");
        var dir = Ensure(zip);
        var real = StagePath(dir, inner);
        var fi = new FileInfo(real);
        if (!fi.Exists && !Directory.Exists(real)) throw new FileNotFoundException($"Could not find file '{real}'.", real);
        long len = fi.Exists ? fi.Length : 0;
        lock (SessionMap)
        {
            if (SessionMap.TryGetValue(Key(zip), out var s))
            {
                s.Edits.RemoveAll(e => e.File == real);
                s.Edits.Add((real, File.GetLastWriteTimeUtc(real), len));
            }
        }
        return real;
    }

    /// <summary>The archive has unsaved changes (an operation ran, or an edited file changed on disk).</summary>
    public static bool IsDirty(string zip)
    {
        List<(string File, DateTime? Mtime, long Len)> edits;
        lock (SessionMap)
        {
            if (!SessionMap.TryGetValue(Key(zip), out var s)) return false;
            if (s.Dirty) return true;
            edits = [.. s.Edits];
        }
        foreach (var (f, mt, len) in edits)
        {
            var fi = new FileInfo(f);
            if (!fi.Exists) return true; // deleted (or replaced)
            if (fi.LastWriteTimeUtc != mt || fi.Length != len) return true;
        }
        return false;
    }

    /// <summary>Archives with a running edit session (as passed to <see cref="Ensure"/>).</summary>
    public static List<string> Sessions()
    {
        lock (SessionMap) return [.. SessionMap.Values.Select(s => s.Zip)];
    }

    /// <summary>Forgets the session and deletes the staging folder (changes are lost).</summary>
    public static void Discard(string zip)
    {
        Session? removed;
        lock (SessionMap) SessionMap.Remove(Key(zip), out removed);
        if (removed != null) TryDeleteDir(removed.Dir);
    }

    /// <summary>True when p is the archive itself or inside it (case-insensitive, no disk access).</summary>
    public static bool Contains(string zip, string p)
    {
        var z = Key(zip);
        var l = p.ToLowerInvariant();
        return l == z || (l.Length > z.Length && l.StartsWith(z, StringComparison.Ordinal) && (l[z.Length] == '\\' || l[z.Length] == '/'));
    }

    /// <summary>(name with '/' and a trailing '/' for folders, source path, is folder).</summary>
    private readonly record struct WItem(string Name, string Path, bool IsDir);

    private static void Walk(string dir, string rel, List<WItem> outList)
    {
        var items = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 })
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var e in items)
        {
            var name = rel + e.Name;
            // like Rust's DirEntry::file_type: a directory symlink / junction is not followed
            if (e is DirectoryInfo && !e.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                outList.Add(new WItem(name + "/", e.FullName, true));
                Walk(e.FullName, name + "/", outList);
            }
            else
            {
                outList.Add(new WItem(name, e.FullName, false));
            }
        }
    }

    /// <summary>Rewrites the archive atomically from its staging folder and ends the session.</summary>
    public static void Save(string zip)
    {
        var dir = Active(zip) ?? throw new IOException("no changes to save");
        var items = new List<WItem>();
        Walk(dir, "", items);
        // jar convention: META-INF/ (with the manifest) first; OrderBy is stable
        items = [.. items.OrderBy(i => i.Name.StartsWith("META-INF/", StringComparison.Ordinal) ? 0 : 1)];
        WriteArchive(zip, items, CancellationToken.None, (_, _) => { });
        Discard(zip);
    }

    /// <summary>Total bytes of srcs (files and folders) – used as the progress total when zipping.</summary>
    public static long TreeTotal(IEnumerable<string> srcs) => srcs.Sum(s => Math.Max(TreeBytes(s), 1));

    /// <summary>Packs srcs (files / folders, each stored under its own name) into a new archive; the format follows the
    /// file name (.zip/.jar, .tar, .tar.gz/.tgz, .gz = one file only). Written to a temp file first; removed again on error
    /// or cancel. <paramref name="progress"/> gets (bytes, current source path).</summary>
    public static void Create(IReadOnlyList<string> srcs, string zipPath, CancellationToken cancel, Action<long, string> progress)
    {
        var items = new List<WItem>();
        foreach (var s in srcs)
        {
            if (VPath.FileName(s) is not { } name) continue;
            if (At(s, () => Directory.Exists(s) || File.Exists(s) ? Directory.Exists(s) : throw new FileNotFoundException($"Could not find file '{s}'.", s)))
            {
                items.Add(new WItem(name + "/", s, true));
                At(s, () =>
                {
                    Walk(s, name + "/", items);
                    return 0;
                });
            }
            else
            {
                items.Add(new WItem(name, s, false));
            }
        }
        WriteArchive(zipPath, items, cancel, progress);
    }

    /// <summary>Read-only stream over a file that reports progress and stops on cancel. Seekable (delegates to the file) so
    /// that TarWriter can take the entry size from it.</summary>
    private sealed class Pump(FileStream f, string path, CancellationToken cancel, Action<long, string> progress) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            CheckCancel(cancel);
            int n = f.Read(buffer);
            progress(n, path);
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => f.CanSeek;
        public override bool CanWrite => false;
        public override long Length => f.Length;
        public override long Position { get => f.Position; set => f.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => f.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) f.Dispose();
            base.Dispose(disposing);
        }
    }

    private static Pump OpenPump(string path, CancellationToken cancel, Action<long, string> progress) =>
        new(At(path, () => OpenRead(path)), path, cancel, progress);

    /// <summary>Rust's <c>Path::with_extension</c>: replaces the last extension ("out.tar.gz" -> "out.tar.mavyex-tmp").</summary>
    private static string TmpPath(string outPath)
    {
        var parent = VPath.Parent(outPath) ?? "";
        var stem = VPath.Stem(outPath) + ".mavyex-tmp";
        return parent.Length == 0 ? stem : VPath.Join(parent, stem);
    }

    /// <summary>Writes items as a new archive at outPath (format by extension), via a temp file that replaces outPath at the
    /// end (the caller has already asked the user about replacing).</summary>
    private static void WriteArchive(string outPath, List<WItem> items, CancellationToken cancel, Action<long, string> progress)
    {
        var tmp = TmpPath(outPath);
        try
        {
            var k = KindOf(outPath) ?? Kind.Zip;
            if (k == Kind.Gz && !(items.Count == 1 && !items[0].IsDir)) throw new IOException(GzOne);
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024))
            {
                switch (k)
                {
                    case Kind.Zip:
                        WriteZip(f, items, cancel, progress);
                        break;
                    case Kind.Tar:
                        WriteTar(f, items, cancel, progress);
                        break;
                    case Kind.TarGz:
                        using (var gz = new GZipStream(f, CompressionLevel.Optimal, leaveOpen: true))
                            WriteTar(gz, items, cancel, progress);
                        break;
                    case Kind.Gz:
                        WriteGz(f, items[0], cancel, progress);
                        break;
                }
            }
            try
            {
                File.Move(tmp, outPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"could not replace the archive (is it open elsewhere?): {e.Message}", e);
            }
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void WriteZip(Stream f, List<WItem> items, CancellationToken cancel, Action<long, string> progress)
    {
        using var w = new ZipArchive(f, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (name, path, isDir) in items)
        {
            CheckCancel(cancel);
            var mtime = At(path, () => File.Exists(path) || Directory.Exists(path)
                ? File.GetLastWriteTime(path)
                : throw new FileNotFoundException($"Could not find file '{path}'.", path));
            var e = w.CreateEntry(name, CompressionLevel.Optimal);
            // DOS dates cover 1980..2107; outside that the zip crate falls back to its default (1980-01-01)
            e.LastWriteTime = mtime.Year is >= 1980 and <= 2107 ? new DateTimeOffset(mtime) : new DateTimeOffset(new DateTime(1980, 1, 1));
            if (isDir)
            {
                progress(1, path);
                continue;
            }
            using var src = OpenPump(path, cancel, progress);
            long len = src.Length;
            using (var dst = e.Open()) src.CopyTo(dst);
            if (len == 0) progress(1, path);
        }
    }

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void WriteTar(Stream s, List<WItem> items, CancellationToken cancel, Action<long, string> progress)
    {
        using var w = new TarWriter(s, TarEntryFormat.Gnu, leaveOpen: true);
        foreach (var (name, path, isDir) in items)
        {
            CheckCancel(cancel);
            var attrs = At(path, () => File.GetAttributes(path));
            var mtime = At(path, () => File.GetLastWriteTimeUtc(path));
            if (mtime < UnixEpoch) mtime = UnixEpoch;
            if (isDir)
            {
                // trailing '/' for folders: what GNU tar writes (the Rust tar crate left it off; both read back the same)
                w.WriteEntry(new GnuTarEntry(TarEntryType.Directory, name.TrimEnd('/') + "/")
                {
                    ModificationTime = mtime,
                    Mode = (UnixFileMode)0b111_101_101, // 0755
                });
                progress(1, path);
                continue;
            }
            using var src = OpenPump(path, cancel, progress);
            long len = src.Length;
            w.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name.TrimEnd('/'))
            {
                ModificationTime = mtime,
                // like the tar crate on Windows: 0644, or 0444 for read-only files
                Mode = attrs.HasFlag(FileAttributes.ReadOnly) ? (UnixFileMode)0b100_100_100 : (UnixFileMode)0b110_100_100,
                DataStream = src,
            });
            if (len == 0) progress(1, path);
        }
    }

    /// <summary>Single-file gzip with the original file name (FNAME) and mtime in the header, like flate2's GzBuilder
    /// (GZipStream can write neither, so the member is framed by hand around a raw DeflateStream).</summary>
    private static void WriteGz(Stream f, WItem item, CancellationToken cancel, Action<long, string> progress)
    {
        var (name, path, _) = item;
        var mtimeUtc = At(path, () => File.GetLastWriteTimeUtc(path));
        var secs = mtimeUtc > UnixEpoch ? (uint)Math.Min((mtimeUtc - UnixEpoch).TotalSeconds, uint.MaxValue) : 0u;
        var header = new List<byte> { 0x1f, 0x8b, 8, 0x08 /* FNAME */ };
        header.AddRange(BitConverter.GetBytes(secs));
        header.Add(0); // XFL
        header.Add(255); // OS: unknown
        header.AddRange(Encoding.UTF8.GetBytes(name));
        header.Add(0);
        f.Write([.. header]);
        using var src = OpenPump(path, cancel, progress);
        long len = src.Length;
        uint crc = 0;
        long total = 0;
        using (var d = new DeflateStream(f, CompressionLevel.Optimal, leaveOpen: true))
        {
            var buf = new byte[256 * 1024];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                crc = Crc32(crc, buf.AsSpan(0, n));
                total += n;
                d.Write(buf, 0, n);
            }
        }
        f.Write(BitConverter.GetBytes(crc));
        f.Write(BitConverter.GetBytes((uint)total));
        if (len == 0) progress(1, path);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        return ~crc;
    }

    // ------------------------------------------------------------------ reading

    private static List<Entry> ListDir(string zip, string dir, string inner)
    {
        var prefix = inner.Length == 0 ? "" : inner + "/";
        var outList = new List<Entry>();
        foreach (var de in new DirectoryInfo(StagePath(dir, inner)).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            try
            {
                var name = de.Name;
                bool isDir = de is DirectoryInfo;
                var path = VirtualPath(zip, prefix + name);
                outList.Add(new Entry
                {
                    Name = name,
                    Path = path,
                    IsDir = isDir,
                    Size = isDir ? 0 : ((FileInfo)de).Length,
                    Modified = de.LastWriteTimeUtc,
                    Ext = isDir ? "" : VPath.Extension(path),
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return outList;
    }

    private static long TreeBytes(string p)
    {
        try
        {
            if (Directory.Exists(p))
            {
                long sum = 0;
                foreach (var e in Directory.EnumerateFileSystemEntries(p)) sum += TreeBytes(e);
                return sum;
            }
            var fi = new FileInfo(p);
            return fi.Exists ? fi.Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void CopyStaged(string src, string dst, CancellationToken cancel, Action<long> progress)
    {
        CheckCancel(cancel);
        if (Directory.Exists(src))
        {
            Directory.CreateDirectory(dst);
            foreach (var e in Directory.EnumerateFileSystemEntries(src))
                CopyStaged(e, Path.Combine(dst, Path.GetFileName(e)), cancel, progress);
        }
        else
        {
            File.Copy(src, dst, overwrite: true); // keeps the file date, like std::fs::copy
            progress(new FileInfo(dst).Length);
        }
    }

    /// <summary>Lists one level of an archive folder; implicit folders (only present as a prefix of deeper names) are
    /// synthesized. Sorted by name (ordinal).</summary>
    public static List<Entry> List(string zip, string inner)
    {
        if (Active(zip) is { } dir) return ListDir(zip, dir, inner);
        var idx = Index(zip);
        var prefix = inner.Length == 0 ? "" : inner + "/";
        var map = new SortedDictionary<string, (bool IsDir, long Size, DateTime? Modified)>(StringComparer.Ordinal);
        foreach (var it in idx)
        {
            if (!it.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = it.Name[prefix.Length..];
            if (rest.Length == 0) continue;
            int slash = rest.IndexOf('/');
            if (slash >= 0) map.TryAdd(rest[..slash], (true, 0, null));
            else map[rest] = (it.IsDir, it.Size, it.Modified); // an explicit entry wins over a synthesized folder
        }
        return
        [
            .. map.Select(kv =>
            {
                var path = VirtualPath(zip, prefix + kv.Key);
                return new Entry
                {
                    Name = kv.Key,
                    Path = path,
                    IsDir = kv.Value.IsDir,
                    Size = kv.Value.Size,
                    Modified = kv.Value.Modified,
                    Ext = kv.Value.IsDir ? "" : VPath.Extension(path),
                };
            }),
        ];
    }

    /// <summary>Total uncompressed size of a file or folder inside an archive (0 on error).</summary>
    public static long Size(string zip, string inner)
    {
        if (Active(zip) is { } dir) return TreeBytes(StagePath(dir, inner));
        List<Item> idx;
        try
        {
            idx = Index(zip);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        var dirPrefix = inner + "/";
        return idx.Where(it => !it.IsDir && (inner.Length == 0 || it.Name == inner || it.Name.StartsWith(dirPrefix, StringComparison.Ordinal)))
            .Sum(it => it.Size);
    }

    /// <summary>Extracts an inner file or folder (recursively; "" = everything) so that it ends up at target.
    /// <paramref name="progress"/> gets the number of bytes written.</summary>
    public static void Extract(string zip, string inner, string target, CancellationToken cancel, Action<long> progress)
    {
        if (Active(zip) is { } dir)
        {
            CopyStaged(StagePath(dir, inner), target, cancel, progress);
            return;
        }
        var dirPrefix = inner.Length == 0 ? "" : inner + "/";
        bool found = false;
        var buf = new byte[256 * 1024];
        Each(zip, (m, open) =>
        {
            CheckCancel(cancel);
            string rel;
            if (m.Name == inner && inner.Length > 0) rel = "";
            else if (m.Name.StartsWith(dirPrefix, StringComparison.Ordinal)) rel = m.Name[dirPrefix.Length..];
            else return true;
            found = true;
            var outPath = rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(target, Path.Combine);
            if (m.IsDir)
            {
                Directory.CreateDirectory(outPath);
                return true;
            }
            if (Path.GetDirectoryName(outPath) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
            var r = open();
            using (var w = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024))
            {
                while (true)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        w.Dispose();
                        try
                        {
                            File.Delete(outPath);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                        throw Cancelled(cancel);
                    }
                    int n = r.Read(buf, 0, buf.Length);
                    if (n == 0) break;
                    w.Write(buf, 0, n);
                    progress(n);
                }
            }
            if (m.Modified is { } t)
            {
                try
                {
                    File.SetLastWriteTimeUtc(outPath, t); // keep the dates of the archived files
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
            }
            return rel.Length > 0; // the one requested file is done: no need to read the rest of a tar stream
        });
        // "" (the whole archive) is never "not found": an empty archive extracts to an empty folder
        if (!found && inner.Length > 0) throw new IOException($"{inner}: not found in archive");
    }

    private static byte[] ReadMax(Stream s, int max)
    {
        var buf = new byte[Math.Max(max, 0)];
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n == 0) break;
            got += n;
        }
        return got == buf.Length ? buf : buf[..got];
    }

    /// <summary>Reads at most max bytes of a file inside an archive.</summary>
    public static byte[] ReadHead(string zip, string inner, int max)
    {
        if (Active(zip) is { } dir)
        {
            using var f = OpenRead(StagePath(dir, inner));
            return ReadMax(f, max);
        }
        if (KindOf(zip) == Kind.Zip)
        {
            using var ar = OpenZip(zip);
            if (ar.GetEntry(inner) is { } z)
            {
                try
                {
                    using var s = z.Open();
                    return ReadMax(s, max);
                }
                catch (InvalidDataException e)
                {
                    throw new IOException(e.Message, e);
                }
            }
        }
        byte[]? result = null;
        Each(zip, (m, open) =>
        {
            if (m.IsDir || m.Name != inner) return true;
            result = ReadMax(open(), max);
            return false;
        });
        return result ?? throw new FileNotFoundException($"{inner}: not found in archive");
    }

    /// <summary>Extracts one inner file to a temp folder (to open it) and returns that path. While the archive is being
    /// edited, the working copy itself is returned.</summary>
    public static string ExtractTemp(string zip, string inner)
    {
        if (Active(zip) is { } staged) return StagePath(staged, inner);
        var mtime = File.Exists(zip) ? File.GetLastWriteTimeUtc(zip).Ticks.ToString() : "";
        var h = Hash(mtime, Hash(zip));
        var dir = Path.Combine(Path.GetTempPath(), "mavyex-archive", $"{h:x}");
        var target = Path.Combine(dir, inner[(inner.LastIndexOf('/') + 1)..]);
        Directory.CreateDirectory(dir);
        if (!File.Exists(target) && !Directory.Exists(target)) Extract(zip, inner, target, CancellationToken.None, _ => { });
        return target;
    }
}
