using Mavyex.Core;

namespace Mavyex.Services;

// STUB – to be replaced by the port of archive.rs (subagent "archive"). Keep these signatures.
// Errors: throw IOException (or InvalidOperationException) with a user-readable message where Rust returned Err(String).
// Cancellation: OperationCanceledException via CancellationToken.

/// <summary>Archives as folders: zip/jar, tar, tar.gz/tgz, plain gz. Virtual paths "C:\x.zip\inner\file".
/// Writes go through *edit sessions* (whole archive extracted to a staging folder; Save rewrites the archive).</summary>
public static class Archive
{
    public static bool IsArchiveName(string p) => false;
    public static bool IsPlainGz(string p) => false;
    /// <summary>(archive file, inner path with '/' separators, "" = root) when an ancestor (or p itself) is an existing archive file.</summary>
    public static (string Zip, string Inner)? Split(string p) => null;
    /// <summary>Like Split, but only for something *inside* (inner not empty).</summary>
    public static (string Zip, string Inner)? SplitInner(string p) => null;
    public static bool InArchive(string p) => false;
    /// <summary>Staging folder of an active edit session, or null.</summary>
    public static string? Active(string zip) => null;
    /// <summary>Starts (or returns) the edit session: extracts the archive into a staging folder.</summary>
    public static string Ensure(string zip) => throw new NotImplementedException();
    /// <summary>Real (staging) path to write to for a virtual path inside an archive (starts the session).</summary>
    public static string RealForWrite(string p) => throw new NotImplementedException();
    /// <summary>Real (staging) path of an existing inner file for editing (F4).</summary>
    public static string RealForEdit(string p) => throw new NotImplementedException();
    public static bool IsDirty(string zip) => false;
    public static List<string> Sessions() => [];
    public static void Discard(string zip) { }
    /// <summary>True when p is the archive itself or inside it.</summary>
    public static bool Contains(string zip, string p) => false;
    /// <summary>Rewrites the archive atomically from its staging folder and ends the session.</summary>
    public static void Save(string zip) => throw new NotImplementedException();
    public static long TreeTotal(IEnumerable<string> srcs) => 0;
    /// <summary>Packs srcs into a new archive; the format follows the file name (.zip/.jar, .tar, .tar.gz/.tgz, .gz).</summary>
    public static void Create(IReadOnlyList<string> srcs, string zipPath, CancellationToken cancel, Action<long, string> progress) =>
        throw new NotImplementedException();
    public static List<Entry> List(string zip, string inner) => throw new NotImplementedException();
    public static long Size(string zip, string inner) => 0;
    /// <summary>Extracts an inner file or folder (recursively) to target.</summary>
    public static void Extract(string zip, string inner, string target, CancellationToken cancel, Action<long> progress) =>
        throw new NotImplementedException();
    public static byte[] ReadHead(string zip, string inner, int max) => throw new NotImplementedException();
    /// <summary>Extracts one inner file to a temp folder (to open it) and returns that path.</summary>
    public static string ExtractTemp(string zip, string inner) => throw new NotImplementedException();
}
