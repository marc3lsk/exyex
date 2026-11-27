using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mavyex.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using SshNet.Agent;

namespace Mavyex.Services;

// Port of sftp.rs: SFTP browsing and transfers over SSH (SSH.NET), authenticated through the running Pageant agent
// (no passwords or key files are handled by mavyex).
//
// Remote locations are "virtual" paths: sftp:\user@host[:port]\~\docs\x.txt (~ = the remote home folder). Everything
// that works on a path (tabs, history, path bar, favorites, recents) works unchanged; only code that touches the file
// system goes through this class. The API is blocking and thread-safe; call it from worker threads or short UI actions.
//
// SSH.NET canonicalizes paths with SSH_FXP_REALPATH before most requests, which resolves symbolic links. For deleting
// or renaming a symlink that would hit the link's *target*, so those operations act on the ISftpFile taken from a
// directory listing (lstat attributes, exact path) whenever the item is a link.

/// <summary>A parsed sftp:\user@host[:port]\rest... location.</summary>
public sealed record Loc(string User, string Host, int Port, List<string> Rest)
{
    public string Key => $"{User}@{Host}:{Port}";
}

/// <summary>SFTP over SSH authenticated by Pageant. Virtual paths "sftp:\user@host[:port]\~\dir" (~ = remote home).
/// Blocking API: call from worker threads or short UI actions. Errors are thrown as <see cref="IOException"/> with a
/// user-readable message; a cancelled transfer throws <see cref="OperationCanceledException"/>.</summary>
public static class Sftp
{
    public const string Prefix = "sftp:";
    private const int Chunk = 256 * 1024;

    // ------------------------------------------------------------------ paths

    internal static List<string>? Parts(string p)
    {
        var s = p.Replace('/', '\\');
        if (!s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return s[Prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static bool IsRemote(string p) => Parts(p) != null;

    /// <summary>"sftp:" itself – the list of known connections.</summary>
    public static bool IsRoot(string p) => Parts(p) is { Count: 0 };

    public static Loc? Parse(string p)
    {
        var v = Parts(p);
        if (v == null || v.Count == 0) return null;
        var first = v[0];
        v.RemoveAt(0);
        int at = first.IndexOf('@');
        if (at < 0) return null;
        string user = first[..at], hostport = first[(at + 1)..];
        string host = hostport;
        int port = 22;
        int colon = hostport.LastIndexOf(':');
        if (colon >= 0)
        {
            var ps = hostport[(colon + 1)..];
            if (ps.Length > 0 && ps.All(char.IsAsciiDigit))
            {
                // like Rust's u16 parse: an out-of-range port makes the whole path invalid
                if (!ushort.TryParse(ps, out var n)) return null;
                host = hostport[..colon];
                port = n;
            }
        }
        return new Loc(user, host, port, v);
    }

    /// <summary>sftp://user@host:2222/home/me (also ssh://, and aliases) -> virtual path, else null.</summary>
    public static string? FromUrl(string s)
    {
        string rest;
        if (s.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase)) rest = s[7..];
        else if (s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) rest = s[6..];
        else return null;
        int slash = rest.IndexOf('/');
        string auth = slash < 0 ? rest : rest[..slash], path = slash < 0 ? "" : rest[(slash + 1)..];
        if (!auth.Contains('@'))
        {
            // sftp://prod/var/www with an alias called "prod"
            var resolved = AliasTarget(auth);
            if (resolved == null) return null;
            auth = resolved;
        }
        if (auth.StartsWith('@') || auth.EndsWith('@')) return null;
        var sb = new StringBuilder($"{Prefix}\\{auth}");
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) sb.Append("\\~");
        else
            foreach (var seg in segs)
                sb.Append('\\').Append(seg);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- aliases

    // Replaced as a whole (never mutated), so readers just take the current reference.
    private static volatile Dictionary<string, string> _aliases = new();

    /// <summary>Replaces the alias table (user@host:port -> alias); called when the settings change.</summary>
    public static void SetAliases(Dictionary<string, string> m) => _aliases = new Dictionary<string, string>(m);

    public static string? AliasOf(Loc l) => _aliases.TryGetValue(l.Key, out var a) ? a : null;

    /// <summary>user@host:port of a path's connection.</summary>
    public static string? ConnKey(string p) => Parse(p)?.Key;

    /// <summary>Looks an alias up (case-insensitive) and returns user@host[:port] (":22" dropped).</summary>
    internal static string? AliasTarget(string name)
    {
        var key = _aliases.FirstOrDefault(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)).Key;
        if (key == null) return null;
        return key.EndsWith(":22", StringComparison.Ordinal) ? key[..^3] : key;
    }

    /// <summary>Short name of a connection: its alias, else user@host.</summary>
    private static string LabelOf(Loc l) => AliasOf(l) ?? $"{l.User}@{l.Host}";

    private static string PortSuffix(Loc l) => l.Port == 22 ? "" : $":{l.Port}";

    /// <summary>Breadcrumb label of the server segment: "alias (user@host)" or just "user@host".</summary>
    public static string? CrumbLabel(string p)
    {
        var l = Parse(p);
        if (l == null) return null;
        var plain = $"{l.User}@{l.Host}{PortSuffix(l)}";
        return AliasOf(l) is { } a ? $"{a} ({plain})" : plain;
    }

    /// <summary>Tab title for a connection root / home folder.</summary>
    public static string? Title(string p)
    {
        var l = Parse(p);
        if (l == null) return null;
        return l.Rest.Count == 0 || (l.Rest.Count == 1 && l.Rest[0] == "~") ? LabelOf(l) : null;
    }

    /// <summary>Human-friendly form of a remote path: alias:\dir\sub (or user@host:\...).</summary>
    public static string? Pretty(string p)
    {
        var l = Parse(p);
        if (l == null) return null;
        var head = LabelOf(l);
        return l.Rest.Count == 0 ? head : $"{head}:\\{string.Join('\\', l.Rest)}";
    }

    private static string ConnPath(Loc l) => $"{Prefix}\\{l.User}@{l.Host}{PortSuffix(l)}";

    /// <summary>Root of the server a path belongs to (sftp:\user@host, i.e. remote "/").</summary>
    public static string? RootOf(string p) => Parse(p) is { } l ? ConnPath(l) : null;

    /// <summary>The remote (POSIX) path for a location, given the remote home folder.</summary>
    internal static string RemotePath(Loc l, string home)
    {
        var segs = l.Rest.ToList();
        var bas = "";
        if (segs.Count > 0 && segs[0] == "~")
        {
            segs.RemoveAt(0);
            bas = home.TrimEnd('/');
        }
        var tail = string.Join('/', segs);
        return (bas.Length == 0, tail.Length == 0) switch
        {
            (true, true) => "/",
            (true, false) => $"/{tail}",
            (false, true) => bas,
            (false, false) => $"{bas}/{tail}",
        };
    }

    internal static string JoinRemote(string dir, string name) => $"{dir.TrimEnd('/')}/{name}";

    // ------------------------------------------------------------ known hosts

    private static string HostsFile() => Path.Combine(Store.ConfigDir(), "known_hosts.json");

    private static readonly object HostsLock = new();

    // Same layout as Rust's serde_json::to_string_pretty (2-space indent); '+' of the base64 fingerprints is written
    // as-is instead of System.Text.Json's default + escape.
    private static readonly JsonSerializerOptions HostsJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Trust on first use: remembers the host key fingerprint ("SHA256:base64") in <paramref name="file"/>
    /// and returns null, or returns the refusal message when the saved key differs.</summary>
    internal static string? CheckHostKey(string file, string hostport, string fp)
    {
        lock (HostsLock)
        {
            Dictionary<string, string> map;
            try { map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new(); }
            catch { map = new(); }
            if (map.TryGetValue(hostport, out var old))
            {
                if (old == fp) return null;
                return $"HOST KEY CHANGED for {hostport}!\nsaved: {old}\nnow:   {fp}\nSomeone may be intercepting the connection. If the server was reinstalled, remove the entry from {file}";
            }
            map[hostport] = fp;
            try
            {
                if (Path.GetDirectoryName(file) is { Length: > 0 } d) Directory.CreateDirectory(d);
                File.WriteAllText(file, JsonSerializer.Serialize(map, HostsJson));
            }
            catch { }
            return null;
        }
    }

    // ------------------------------------------------------------- connections

    /// <summary>A pooled connection. Reference counted like Rust's Arc: dropping it from the pool (dead session,
    /// DisconnectAll) does not cut off operations still running on it; it is disposed when the last one ends.</summary>
    private sealed class Conn(Loc loc, SshAgentPrivateKey[] keys, SftpClient sftp, string home)
    {
        public readonly Loc Loc = loc;
        public readonly SftpClient Sftp = sftp;
        public readonly string Home = home;
        public int Users;
        public bool Retired;
        private SshClient? _exec;
        private readonly object _execLock = new();

        /// <summary>SSH.NET can't open an exec channel on the SFTP client's session, so commands (chown) use a
        /// second connection, opened on first use and kept with this one.</summary>
        public SshClient Exec()
        {
            lock (_execLock)
            {
                if (_exec is { IsConnected: true }) return _exec;
                _exec?.Dispose();
                _exec = null;
                var c = new SshClient(Info(Loc, keys)) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                Open(c, Loc, keys.Length);
                return _exec = c;
            }
        }

        public void Dispose()
        {
            try { Sftp.Dispose(); } catch { }
            lock (_execLock)
            {
                try { _exec?.Dispose(); } catch { }
                _exec = null;
            }
        }
    }

    private static readonly object PoolLock = new();
    private static readonly Dictionary<string, Conn> Pool = new();

    private static SshAgentPrivateKey[] AgentKeys()
    {
        SshAgentPrivateKey[] ids;
        try { ids = new Pageant().RequestIdentities().ToArray(); }
        catch (Exception e) { throw new IOException($"can't reach Pageant – is it running? ({e.Message})", e); }
        if (ids.Length == 0) throw new IOException("Pageant has no keys loaded");
        return ids;
    }

    private static ConnectionInfo Info(Loc l, SshAgentPrivateKey[] keys) =>
        new(l.Host, l.Port, l.User, new PrivateKeyAuthenticationMethod(l.User, keys.ToArray<IPrivateKeySource>()))
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

    /// <summary>Connects (host key check + Pageant auth) and maps failures to the Rust messages.</summary>
    private static void Open(BaseClient client, Loc l, int keyCount)
    {
        var hostport = $"{l.Host}:{l.Port}";
        string? reason = null; // why the host key was refused (read back after a failed connect)
        client.HostKeyReceived += (_, e) =>
        {
            var r = CheckHostKey(HostsFile(), hostport, "SHA256:" + e.FingerPrintSHA256);
            if (r != null)
            {
                Volatile.Write(ref reason, r);
                e.CanTrust = false;
            }
        };
        try
        {
            client.Connect();
        }
        catch (Exception e)
        {
            client.Dispose();
            if (Volatile.Read(ref reason) is { } r) throw new IOException(r, e);
            throw e switch
            {
                SshAuthenticationException => new IOException($"{l.User}@{l.Host} rejected all {keyCount} key(s) from Pageant", e),
                SshOperationTimeoutException or SocketException { SocketErrorCode: SocketError.TimedOut } =>
                    new IOException($"timeout connecting to {hostport}", e),
                _ when e.Message.Contains("subsystem", StringComparison.OrdinalIgnoreCase) =>
                    new IOException($"SFTP is not available on this server: {e.Message}", e),
                _ => new IOException($"can't connect to {hostport}: {e.Message}", e),
            };
        }
    }

    private static Conn Connect(Loc l)
    {
        // SSH.NET needs the auth methods before connecting, so Pageant is asked first (Rust asked after the handshake).
        var keys = AgentKeys();
        var sftp = new SftpClient(Info(l, keys))
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            OperationTimeout = TimeSpan.FromSeconds(60),
        };
        Open(sftp, l, keys.Length);
        string home;
        try { home = sftp.WorkingDirectory; } // realpath(".") done by SSH.NET on connect
        catch { home = "/"; }
        if (string.IsNullOrEmpty(home)) home = "/";
        return new Conn(l with { Rest = [] }, keys, sftp, home);
    }

    private static Conn Acquire(Loc l)
    {
        var key = l.Key;
        while (true)
        {
            lock (PoolLock)
            {
                if (Pool.TryGetValue(key, out var c))
                {
                    c.Users++;
                    return c;
                }
            }
            var n = Connect(l); // outside the lock: other servers stay usable meanwhile
            lock (PoolLock)
            {
                if (!Pool.ContainsKey(key))
                {
                    n.Users = 1;
                    Pool[key] = n;
                    return n;
                }
            }
            n.Dispose(); // another thread connected first: use that one
        }
    }

    private static void Release(Conn c)
    {
        bool dispose;
        lock (PoolLock)
        {
            c.Users--;
            dispose = c.Retired && c.Users == 0;
        }
        if (dispose) c.Dispose();
    }

    /// <summary>Drops a connection from the pool (it closes once no operation uses it).</summary>
    private static void Retire(Conn c)
    {
        bool dispose;
        lock (PoolLock)
        {
            if (c.Retired) return;
            if (Pool.TryGetValue(c.Loc.Key, out var cur) && ReferenceEquals(cur, c)) Pool.Remove(c.Loc.Key);
            c.Retired = true;
            dispose = c.Users == 0;
        }
        if (dispose) c.Dispose();
    }

    private static readonly string[] DeadWords = ["disconnect", "channel", "timeout", "send", "closed", "broken", "eof", "reset"];

    private static bool LooksDead(Conn c, Exception e)
    {
        if (e is SshConnectionException or SshOperationTimeoutException or ObjectDisposedException or SocketException) return true;
        try
        {
            if (!c.Sftp.IsConnected) return true;
        }
        catch { return true; }
        var m = e.Message.ToLowerInvariant();
        return DeadWords.Any(m.Contains);
    }

    private static IOException ToIo(Exception e) => e as IOException ?? new IOException(e.Message, e);

    /// <summary>Runs an operation on the (pooled) connection; reconnects once if the session died.</summary>
    private static T RunOn<T>(Loc l, bool retry, Func<Conn, string, T> mk)
    {
        for (int attempt = 0; ; attempt++)
        {
            var c = Acquire(l);
            try
            {
                return mk(c, RemotePath(l, c.Home));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                bool dead = LooksDead(c, e);
                if (dead) Retire(c);
                if (retry && attempt == 0 && dead) continue;
                throw ToIo(e);
            }
            finally
            {
                Release(c);
            }
        }
    }

    /// <summary>Drops all pooled connections.</summary>
    public static void DisconnectAll()
    {
        List<Conn> all;
        lock (PoolLock) all = Pool.Values.ToList();
        foreach (var c in all) Retire(c);
    }

    private static Loc LocOf(string p) => Parse(p) ?? throw new IOException($"not a valid SFTP location: {p}");

    // ---------------------------------------------------------------- listing

    // SSH.NET leaves the time at DateTime.MinValue when the server sent none (Rust: mtime None).
    private static DateTime? ToTime(DateTime t) =>
        t == DateTime.MinValue ? null : DateTime.SpecifyKind(t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t, DateTimeKind.Utc);

    /// <summary>Known connections (from favorites and recent folders) shown at "sftp:".</summary>
    private static List<Loc> KnownConnections(Store st)
    {
        var seen = new List<Loc>();
        foreach (var p in st.Favorites.Concat(st.Visits.Keys))
        {
            if (Parse(p) is not { } l) continue;
            l = l with { Rest = [] };
            if (!seen.Any(x => x.Key == l.Key)) seen.Add(l);
        }
        return seen;
    }

    private static Entry MakeEntry(string name, string path, bool isDir, long size, DateTime? modified)
    {
        // Rust: name.rsplit_once('.') – ".bashrc" has the extension "bashrc"
        int dot = name.LastIndexOf('.');
        var ext = isDir || dot < 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
        return new Entry
        {
            Name = name, Path = path, IsDir = isDir, Size = size, Modified = modified, Hidden = name.StartsWith('.'), Ext = ext,
        };
    }

    /// <summary>The "sftp:" listing: one folder per known connection.</summary>
    internal static List<Entry> RootEntries(Store st) =>
        KnownConnections(st)
            .Select(l =>
            {
                var name = AliasOf(l) is { } a ? $"{a}  —  {l.User}@{l.Host}" : $"{l.User}@{l.Host}";
                return MakeEntry(name, ConnPath(l), true, 0, null);
            })
            .ToList();

    public static List<Entry> List(string p)
    {
        if (IsRoot(p)) return RootEntries(Store.Load());
        var loc = LocOf(p);
        return RunOn(loc, true, (c, rp) =>
        {
            var items = new List<Entry>();
            foreach (var e in c.Sftp.ListDirectory(rp))
            {
                var name = e.Name;
                if (name is "." or "..") continue;
                bool isDir = e.IsDirectory;
                if (e.IsSymbolicLink)
                {
                    // follow the link to see whether it points at a folder
                    try { isDir = c.Sftp.GetAttributes(JoinRemote(rp, name)).IsDirectory; }
                    catch (Exception x) when (x is not SshConnectionException) { isDir = false; }
                }
                items.Add(MakeEntry(name, VPath.Join(p, name), isDir, e.Length, ToTime(e.LastWriteTimeUtc)));
            }
            return items;
        });
    }

    public static bool Exists(string p)
    {
        var l = Parse(p);
        if (l == null) return false;
        try { return RunOn(l, true, (c, rp) => c.Sftp.Exists(rp)); }
        catch { return false; }
    }

    public static bool IsDir(string p)
    {
        if (IsRoot(p)) return true;
        var l = Parse(p);
        if (l == null) return false;
        try { return RunOn(l, true, (c, rp) => c.Sftp.GetAttributes(rp).IsDirectory); }
        catch { return false; }
    }

    /// <summary>The item at exactly <paramref name="path"/> with lstat attributes (a link is not followed).</summary>
    private static ISftpFile LStat(SftpClient s, string path)
    {
        // Get() canonicalizes first; when nothing on the way was a link the result is the item itself
        var f = s.Get(path);
        if (f.FullName == path || path == "/") return f;
        int i = path.LastIndexOf('/');
        string dir = i <= 0 ? "/" : path[..i], name = path[(i + 1)..];
        foreach (var e in s.ListDirectory(dir))
            if (e.Name == name) return e;
        return f;
    }

    // ----------------------------------------------------------- modifications

    public static void Mkdir(string p)
    {
        var l = LocOf(p);
        RunOn(l, true, (c, rp) =>
        {
            c.Sftp.CreateDirectory(rp);
            return 0;
        });
    }

    public static void CreateFile(string p)
    {
        var l = LocOf(p);
        RunOn(l, true, (c, rp) =>
        {
            c.Sftp.Open(rp, FileMode.CreateNew, FileAccess.Write).Dispose(); // CREATE | WRITE | EXCLUDE
            return 0;
        });
    }

    public static void Rename(string from, string to)
    {
        Loc a = LocOf(from), b = LocOf(to);
        if (a.Key != b.Key) throw new IOException("can't rename across servers");
        RunOn(a, true, (c, rp) =>
        {
            var target = RemotePath(b, c.Home);
            var item = LStat(c.Sftp, rp);
            if (item.IsSymbolicLink) item.MoveTo(target); // rename the link, not what it points at
            else c.Sftp.RenameFile(rp, target);
            return 0;
        });
    }

    /// <summary>A user or group for chown: a name or a number, nothing the shell could interpret.</summary>
    internal static bool ValidId(string s) =>
        s.Length <= 64 && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or '$' or '@') && !s.StartsWith('-');

    internal static string ShQuote(string s) => $"'{s.Replace("'", "'\\''")}'";

    /// <summary>Validates a chown request (Rust messages) and returns the owner[:group] spec and the parsed locations.</summary>
    internal static (string Spec, List<Loc> Locs) ChownPrepare(IReadOnlyList<string> paths, string owner, string group)
    {
        owner = owner.Trim();
        group = group.Trim();
        if (owner.Length == 0 && group.Length == 0) throw new IOException("enter an owner and/or a group");
        if (!(owner.Length == 0 || ValidId(owner)) || !(group.Length == 0 || ValidId(group)))
            throw new IOException("owner / group may contain only letters, digits and _ - . $ @");
        if (paths.Count == 0) throw new IOException("nothing selected");
        var l = LocOf(paths[0]);
        var locs = new List<Loc>();
        foreach (var p in paths)
        {
            var pl = LocOf(p);
            if (pl.Key != l.Key) throw new IOException("select items on one server only");
            if (pl.Rest.Count == 0) throw new IOException("select a folder or file inside the server, not the connection itself");
            locs.Add(pl);
        }
        return (group.Length == 0 ? owner : $"{owner}:{group}", locs);
    }

    /// <summary>The shell command run for chown (every argument single-quoted).</summary>
    internal static string ChownCommand(string spec, IEnumerable<string> remotePaths, bool recursive, bool sudo)
    {
        var cmd = new StringBuilder();
        if (sudo) cmd.Append("sudo -n ");
        cmd.Append("chown -h ");
        if (recursive) cmd.Append("-R ");
        cmd.Append("-- ").Append(ShQuote(spec));
        foreach (var rp in remotePaths) cmd.Append(' ').Append(ShQuote(rp));
        return cmd.ToString();
    }

    /// <summary>chown over SSH exec; returns a summary message, throws on failure.</summary>
    /// <remarks>Changes the Linux owner / group of remote files (SFTP itself only knows numeric ids). owner and group
    /// may be empty (left unchanged), but not both. All paths must be on the same server.</remarks>
    public static string Chown(IReadOnlyList<string> paths, string owner, string group, bool recursive, bool sudo)
    {
        var (spec, locs) = ChownPrepare(paths, owner, group);
        RunOn(locs[0], false, (c, _) =>
        {
            var text = ChownCommand(spec, locs.Select(pl => RemotePath(pl, c.Home)), recursive, sudo);
            using var cmd = c.Exec().CreateCommand(text);
            cmd.Execute();
            var output = (cmd.Result + cmd.Error).Trim();
            return cmd.ExitStatus switch
            {
                0 => 0,
                { } n => throw new IOException($"chown failed (exit {n}): {output}"),
                null => throw new IOException($"chown ended without a result {output}"),
            };
        });
        return $"Owner changed to {spec}{(recursive ? " (recursively)" : "")}";
    }

    /// <summary>One item of a remote tree (parents come before their children). Link is set for symlinks that are
    /// removed as links.</summary>
    private readonly record struct Node(string Path, bool IsDir, long Size, DateTime? Mtime, ISftpFile? Link);

    /// <summary>Lists a remote tree. For <paramref name="forRemove"/> the root is not followed and every link is a
    /// leaf to delete; otherwise links to files are copied as files and links to folders are skipped (no cycles).</summary>
    private static List<Node> Walk(SftpClient s, string root, bool forRemove)
    {
        var nodes = new List<Node>();
        if (forRemove)
        {
            var f = LStat(s, root);
            if (!f.IsDirectory || f.IsSymbolicLink)
            {
                nodes.Add(new Node(root, false, f.Length, ToTime(f.LastWriteTimeUtc), f.IsSymbolicLink ? f : null));
                return nodes;
            }
            nodes.Add(new Node(root, true, 0, ToTime(f.LastWriteTimeUtc), null));
        }
        else
        {
            var a = s.GetAttributes(root);
            if (!a.IsDirectory)
            {
                nodes.Add(new Node(root, false, a.Size, ToTime(a.LastWriteTimeUtc), null));
                return nodes;
            }
            nodes.Add(new Node(root, true, 0, ToTime(a.LastWriteTimeUtc), null));
        }
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var e in s.ListDirectory(dir))
            {
                var name = e.Name;
                if (name is "." or "..") continue;
                var child = JoinRemote(dir, name);
                if (e.IsSymbolicLink)
                {
                    if (forRemove)
                    {
                        nodes.Add(new Node(child, false, e.Length, ToTime(e.LastWriteTimeUtc), e));
                        continue;
                    }
                    SftpFileAttributes? t = null;
                    try { t = s.GetAttributes(child); }
                    catch (Exception x) when (x is not SshConnectionException) { }
                    if (t is { IsDirectory: false }) nodes.Add(new Node(child, false, t.Size, ToTime(t.LastWriteTimeUtc), null));
                }
                else if (e.IsDirectory)
                {
                    nodes.Add(new Node(child, true, 0, ToTime(e.LastWriteTimeUtc), null));
                    stack.Push(child);
                }
                else nodes.Add(new Node(child, false, e.Length, ToTime(e.LastWriteTimeUtc), null));
            }
        }
        return nodes;
    }

    /// <summary>Deletes a file or a whole folder tree (permanently – there is no remote recycle bin).</summary>
    public static void RemoveAll(string p)
    {
        var l = LocOf(p);
        if (l.Rest.Count == 0 || (l.Rest.Count == 1 && l.Rest[0] == "~"))
            throw new IOException("refusing to delete the remote home / root folder");
        RunOn(l, false, (c, rp) =>
        {
            var nodes = Walk(c.Sftp, rp, forRemove: true);
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                var n = nodes[i];
                if (n.Link != null) n.Link.Delete(); // the link itself (path APIs would resolve it to the target)
                else if (n.IsDir) c.Sftp.DeleteDirectory(n.Path);
                else c.Sftp.DeleteFile(n.Path);
            }
            return 0;
        });
    }

    public static long SizeOf(string p)
    {
        var l = Parse(p);
        if (l == null) return 0;
        try { return RunOn(l, true, (c, rp) => Walk(c.Sftp, rp, forRemove: false).Sum(n => n.Size)); }
        catch { return 0; }
    }

    /// <summary>First free "name", "name (2)", … in a remote folder.</summary>
    public static string UniqueRemote(string dir, string name) => UniqueRemote(dir, name, Exists);

    internal static string UniqueRemote(string dir, string name, Func<string, bool> exists)
    {
        var first = VPath.Join(dir, name);
        if (!exists(first)) return first;
        int dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (int i = 2; i < 1000; i++)
        {
            var cand = VPath.Join(dir, $"{stem} ({i}){ext}");
            if (!exists(cand)) return cand;
        }
        return first;
    }

    // ---------------------------------------------------------------- transfers

    private static OperationCanceledException Cancelled(CancellationToken cancel) => new("Cancelled", cancel);

    /// <summary>Copies a remote file / folder to <paramref name="local"/> (the full target path).</summary>
    public static void Download(string remote, string local, CancellationToken cancel, Action<long> progress)
    {
        var l = LocOf(remote);
        RunOn(l, false, (c, rp) =>
        {
            var buf = new byte[Chunk];
            foreach (var n in Walk(c.Sftp, rp, forRemove: false))
            {
                var dest = local;
                foreach (var seg in n.Path[rp.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries)) dest = Path.Combine(dest, seg);
                if (n.IsDir)
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                using (var src = c.Sftp.OpenRead(n.Path))
                using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    while (true)
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            dst.Dispose();
                            try { File.Delete(dest); } catch { }
                            throw Cancelled(cancel);
                        }
                        int r = src.Read(buf, 0, buf.Length);
                        if (r == 0) break;
                        dst.Write(buf, 0, r);
                        progress(r);
                    }
                }
                if (n.Mtime is { } t)
                {
                    try { File.SetLastWriteTimeUtc(dest, t); } catch { }
                }
            }
            return 0;
        });
    }

    /// <summary>Local tree -> (local path, remote path, is_dir), parents first.</summary>
    private static void LocalTree(string local, string remote, List<(string Local, string Remote, bool IsDir)> tree)
    {
        if (Directory.Exists(local))
        {
            tree.Add((local, remote, true));
            foreach (var child in Directory.EnumerateFileSystemEntries(local))
                LocalTree(child, JoinRemote(remote, Path.GetFileName(child)), tree);
        }
        else if (File.Exists(local)) tree.Add((local, remote, false));
        else throw new FileNotFoundException($"{local}: not found", local);
    }

    /// <summary>Copies a local file / folder to <paramref name="remote"/> (the full target path).</summary>
    public static void Upload(string local, string remote, CancellationToken cancel, Action<long> progress)
    {
        var l = LocOf(remote);
        RunOn(l, false, (c, rp) =>
        {
            var tree = new List<(string Local, string Remote, bool IsDir)>();
            LocalTree(local, rp, tree);
            var buf = new byte[Chunk];
            foreach (var (lp, rpath, isDir) in tree)
            {
                if (isDir)
                {
                    bool exists;
                    try { exists = c.Sftp.Exists(rpath); }
                    catch (Exception x) when (x is not SshConnectionException) { exists = false; }
                    if (!exists) c.Sftp.CreateDirectory(rpath);
                    continue;
                }
                using (var src = new FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    SftpFileStream dst;
                    try { dst = c.Sftp.Open(rpath, FileMode.Create, FileAccess.Write); } // CREATE | WRITE | TRUNCATE
                    catch (Exception e) when (e is not SshConnectionException) { throw new IOException($"{rpath}: {e.Message}", e); }
                    using (dst)
                    {
                        while (true)
                        {
                            if (cancel.IsCancellationRequested)
                            {
                                try { dst.Dispose(); } catch { }
                                try { c.Sftp.DeleteFile(rpath); } catch { }
                                throw Cancelled(cancel);
                            }
                            int r = src.Read(buf, 0, buf.Length);
                            if (r == 0) break;
                            dst.Write(buf, 0, r);
                            progress(r);
                        }
                        dst.Flush();
                    }
                }
                try
                {
                    // Rust sets atime and mtime to the local mtime, whole seconds
                    var m = File.GetLastWriteTimeUtc(lp);
                    m = new DateTime(m.Ticks - m.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                    var attrs = c.Sftp.GetAttributes(rpath);
                    attrs.LastAccessTimeUtc = m;
                    attrs.LastWriteTimeUtc = m;
                    c.Sftp.SetAttributes(rpath, attrs);
                }
                catch (Exception x) when (x is not SshConnectionException) { }
            }
            return 0;
        });
    }

    /// <summary>Reads at most <paramref name="max"/> bytes from the start of a remote file.</summary>
    public static byte[] ReadHead(string p, int max)
    {
        var l = LocOf(p);
        return RunOn(l, true, (c, rp) =>
        {
            using var f = c.Sftp.OpenRead(rp);
            var buf = new MemoryStream();
            var chunk = new byte[Math.Min(Chunk, Math.Max(max, 1))];
            while (buf.Length < max)
            {
                int n = f.Read(chunk, 0, chunk.Length);
                if (n == 0) break;
                buf.Write(chunk, 0, (int)Math.Min(n, max - buf.Length));
            }
            return buf.ToArray();
        });
    }

    /// <summary>Downloads to a temp file (to open / edit it) and returns its path.</summary>
    public static string FetchTemp(string p)
    {
        // a stable per-path folder (Rust: DefaultHasher), so re-opening the same file reuses it
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(p)), 0, 8);
        var dir = Path.Combine(Path.GetTempPath(), "mavyex-sftp", hash);
        Directory.CreateDirectory(dir);
        var name = VPath.FileName(p) ?? throw new IOException("no file name");
        var target = Path.Combine(dir, name);
        Download(p, target, CancellationToken.None, _ => { });
        return target;
    }
}
