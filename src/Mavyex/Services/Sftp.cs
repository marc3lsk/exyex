using Mavyex.Core;

namespace Mavyex.Services;

// STUB – to be replaced by the port of sftp.rs (subagent "sftp", SSH.NET + SshNet.Agent/Pageant). Keep these signatures.
// The trivial path functions below already work (the UI needs them); the agent rewrites the whole file.

/// <summary>A parsed sftp:\user@host[:port]\rest... location.</summary>
public sealed record Loc(string User, string Host, int Port, List<string> Rest)
{
    public string Key => $"{User}@{Host}:{Port}";
}

/// <summary>SFTP over SSH authenticated by Pageant. Virtual paths "sftp:\user@host[:port]\~\dir" (~ = remote home).
/// Blocking API: call from worker threads or short UI actions.</summary>
public static class Sftp
{
    public const string Prefix = "sftp:";

    private static List<string>? Parts(string p)
    {
        var s = p.Replace('/', '\\');
        if (!s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return s[Prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static bool IsRemote(string p) => Parts(p) != null;
    /// <summary>"sftp:" itself – the list of known connections.</summary>
    public static bool IsRoot(string p) => Parts(p) is { Count: 0 };
    public static Loc? Parse(string p) => null;
    /// <summary>sftp://user@host:2222/home/me (also ssh://, and aliases) -> virtual path, else null.</summary>
    public static string? FromUrl(string s) => null;
    public static void SetAliases(Dictionary<string, string> m) { }
    public static string? AliasOf(Loc l) => null;
    public static string? ConnKey(string p) => null;
    public static string? CrumbLabel(string p) => null;
    public static string? Title(string p) => null;
    public static string? Pretty(string p) => null;
    public static string? RootOf(string p) => null;
    public static void DisconnectAll() { }
    public static List<Entry> List(string p) => throw new NotImplementedException();
    public static bool Exists(string p) => false;
    public static bool IsDir(string p) => false;
    public static void Mkdir(string p) => throw new NotImplementedException();
    public static void CreateFile(string p) => throw new NotImplementedException();
    public static void Rename(string from, string to) => throw new NotImplementedException();
    /// <summary>chown over SSH exec; returns a summary message, throws on failure.</summary>
    public static string Chown(IReadOnlyList<string> paths, string owner, string group, bool recursive, bool sudo) =>
        throw new NotImplementedException();
    public static void RemoveAll(string p) => throw new NotImplementedException();
    public static long SizeOf(string p) => 0;
    public static string UniqueRemote(string dir, string name) => VPath.Join(dir, name);
    public static void Download(string remote, string local, CancellationToken cancel, Action<long> progress) =>
        throw new NotImplementedException();
    public static void Upload(string local, string remote, CancellationToken cancel, Action<long> progress) =>
        throw new NotImplementedException();
    public static byte[] ReadHead(string p, int max) => throw new NotImplementedException();
    /// <summary>Downloads to a temp file (to open / edit it) and returns its path.</summary>
    public static string FetchTemp(string p) => throw new NotImplementedException();
}
