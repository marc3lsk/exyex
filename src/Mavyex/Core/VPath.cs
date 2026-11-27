namespace Mavyex.Core;

/// <summary>
/// Path helpers with the semantics of Rust's <c>std::path::Path</c> on Windows, which the Rust version relied on.
/// Paths are plain strings with '\' separators and may be *virtual*:
/// <list type="bullet">
/// <item><c>C:\dir\file</c>, <c>\\server\share\dir</c> – local / UNC</item>
/// <item><c>C:\dir\a.zip\inner\file</c> – inside an archive (see Archive.Split)</item>
/// <item><c>sftp:\user@host[:port]\~\dir</c> – remote; <c>sftp:</c> alone = list of connections</item>
/// </list>
/// Rules (Rust semantics): Parent("C:\") = null, Parent("C:\a") = "C:\", Parent("sftp:\u@h") = "sftp:",
/// Parent("sftp:") = "" (empty, not null), Parent("") = null. FileName("C:\") = null.
/// StartsWith / StripPrefix work on whole components, case-insensitively.
/// </summary>
public static class VPath
{
    public const char Sep = '\\';

    /// <summary>Length of the root prefix: "C:\" = 3, "C:" = 2, "\\server\share\" = its length, "\" = 1, else 0.</summary>
    public static int RootLength(string p)
    {
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
            return p.Length >= 3 && (p[2] == '\\' || p[2] == '/') ? 3 : 2;
        if (p.StartsWith(@"\\") || p.StartsWith("//"))
        {
            // \\server\share\  (or \\?\C:\ verbatim – treated like UNC here, rare)
            int i = p.IndexOfAny(['\\', '/'], 2);
            if (i < 0) return p.Length;
            int j = p.IndexOfAny(['\\', '/'], i + 1);
            return j < 0 ? p.Length : j + 1;
        }
        if (p.StartsWith('\\') || p.StartsWith('/')) return 1;
        return 0;
    }

    /// <summary>True when the path is only a root ("C:\", "\\server\share").</summary>
    public static bool IsRoot(string p)
    {
        int r = RootLength(p);
        return r > 0 && TrimTrailing(p).Length <= r;
    }

    /// <summary>Removes trailing separators, but keeps a root's own ("C:\").</summary>
    public static string TrimTrailing(string p)
    {
        int r = RootLength(p);
        int end = p.Length;
        while (end > r && (p[end - 1] == '\\' || p[end - 1] == '/')) end--;
        return p[..end];
    }

    public static string? Parent(string p)
    {
        if (p.Length == 0) return null;
        var t = TrimTrailing(p);
        int r = RootLength(t);
        if (r > 0 && t.Length <= r) return null; // a root has no parent
        int i = t.LastIndexOfAny(['\\', '/']);
        if (i < 0) return "";                      // "sftp:" -> ""
        if (i < r) return t[..r];                  // "C:\a" -> "C:\"
        return t[..i];
    }

    /// <summary>Last component, or null for a root / empty path.</summary>
    public static string? FileName(string p)
    {
        var t = TrimTrailing(p);
        int r = RootLength(t);
        if (t.Length <= r) return null;
        int i = t.LastIndexOfAny(['\\', '/']);
        var name = i < 0 ? t : t[(i + 1)..];
        return name.Length == 0 || name == ".." ? null : name;
    }

    /// <summary>Lower-case extension without dot, "" if none (".bashrc" has none).</summary>
    public static string Extension(string p)
    {
        var n = FileName(p);
        if (n == null) return "";
        int i = n.LastIndexOf('.');
        return i <= 0 ? "" : n[(i + 1)..].ToLowerInvariant();
    }

    /// <summary>File name without the last extension ("a.tar.gz" -> "a.tar").</summary>
    public static string Stem(string p)
    {
        var n = FileName(p) ?? "";
        int i = n.LastIndexOf('.');
        return i <= 0 ? n : n[..i];
    }

    public static string Join(string a, string b)
    {
        if (b.Length == 0) return a;
        if (RootLength(b) > 0 && (b.Length >= 2 && b[1] == ':' || b.StartsWith(@"\\"))) return b; // absolute b wins
        if (a.Length == 0) return b;
        return a.EndsWith('\\') || a.EndsWith('/') ? a + b.TrimStart('\\', '/') : a + Sep + b.TrimStart('\\', '/');
    }

    public static string Join(string a, params string[] parts)
    {
        foreach (var p in parts) a = Join(a, p);
        return a;
    }

    /// <summary>Path components ("C:\a\b" -> ["C:\", "a", "b"]; "sftp:\u@h" -> ["sftp:", "u@h"]).</summary>
    public static List<string> Components(string p)
    {
        var list = new List<string>();
        int r = RootLength(p);
        if (r > 0) list.Add(p[..r]);
        foreach (var s in p[r..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)) list.Add(s);
        return list;
    }

    private static string Norm(string c) => c.Replace('/', '\\').TrimEnd('\\');

    /// <summary>Component-wise, case-insensitive prefix test ("C:\ab" does not start with "C:\a").</summary>
    public static bool StartsWith(string p, string prefix)
    {
        var a = Components(p);
        var b = Components(prefix);
        if (b.Count > a.Count) return false;
        for (int i = 0; i < b.Count; i++)
            if (!string.Equals(Norm(a[i]), Norm(b[i]), StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>The rest of <paramref name="p"/> after <paramref name="prefix"/> ("" when equal), or null.</summary>
    public static string? StripPrefix(string p, string prefix)
    {
        if (!StartsWith(p, prefix)) return null;
        var a = Components(p);
        int n = Components(prefix).Count;
        return string.Join(Sep, a.Skip(n));
    }

    public static bool Same(string a, string b) =>
        string.Equals(Norm(TrimTrailing(a)), Norm(TrimTrailing(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Lexically resolves "." and ".." without touching the disk.</summary>
    public static string LexicalNormalize(string p)
    {
        var comps = Components(p);
        int r = RootLength(p);
        var root = r > 0 ? comps[0] : null;
        var stack = new List<string>();
        foreach (var c in r > 0 ? comps.Skip(1) : comps)
        {
            if (c == ".") continue;
            if (c == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(c);
        }
        var body = string.Join(Sep, stack);
        if (root == null) return body;
        if (root.Length == 2 && root[1] == ':') return body.Length == 0 ? root : root + body; // "C:rel" (rare)
        return root.EndsWith('\\') || root.EndsWith('/') ? root + body : (body.Length == 0 ? root : root + Sep + body);
    }

    /// <summary>File name, or the path itself without the trailing separator for roots ("C:").</summary>
    public static string DisplayName(string p) => FileName(p) ?? p.TrimEnd('\\');

    private static string ExpandEnv(string s)
    {
        var sb = new System.Text.StringBuilder();
        var rest = s;
        while (true)
        {
            int i = rest.IndexOf('%');
            if (i < 0) break;
            sb.Append(rest, 0, i);
            var after = rest[(i + 1)..];
            int j = after.IndexOf('%');
            if (j >= 0)
            {
                var name = after[..j];
                var v = name.Length > 0 ? Environment.GetEnvironmentVariable(name) : null;
                if (v != null) sb.Append(v);
                else sb.Append('%').Append(name).Append('%');
                rest = after[(j + 1)..];
            }
            else
            {
                sb.Append('%');
                rest = after;
            }
        }
        sb.Append(rest);
        return sb.ToString();
    }

    /// <summary>Turns whatever the user typed or pasted into a path: strips quotes, expands %VARS%, ~, file:/// URLs,
    /// sftp:// URLs, converts / to \ and resolves relative paths against <paramref name="baseDir"/>.</summary>
    public static string? ParseUserPath(string input, string baseDir)
    {
        var s = input.Trim().Trim('"', '\'').Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) s = s[8..].Replace("%20", " ");
        if (Services.Sftp.FromUrl(s) is { } remote) return remote;
        if (Services.Sftp.IsRemote(s)) return LexicalNormalize(s.Replace('/', '\\'));
        s = ExpandEnv(s);
        if (s == "~" || s.StartsWith("~/") || s.StartsWith("~\\"))
            s = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + s[1..];
        s = s.Replace('/', '\\');
        if (s.Length == 2 && s[1] == ':') s += "\\";
        var p = RootLength(s) > 0 ? s : Join(baseDir, s);
        return LexicalNormalize(p);
    }
}
