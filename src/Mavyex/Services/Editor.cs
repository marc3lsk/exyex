using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Mavyex.Core;

namespace Mavyex.Services;

/// <summary>F4 external editor (editor.rs): a command template such as <c>code {file}</c> or
/// <c>"C:\Program Files\Notepad++\notepad++.exe" -n{line} {file}</c>, chosen per file extension.</summary>
public static class Editor
{
    /// <summary>Used when nothing is configured.</summary>
    public const string Default = "notepad {file}";

    /// <summary>The template for a file: the first matching rule, else <paramref name="defaultCmd"/>, else <see cref="Default"/>.
    /// <paramref name="rules"/> holds one rule per line, <c>ext1,ext2 = command</c> (<c>*</c> matches everything,
    /// <c>#</c> starts a comment).</summary>
    public static string CommandFor(string defaultCmd, string rules, string file)
    {
        var ext = VPath.Extension(file);
        foreach (var raw in rules.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var cmd = line[(eq + 1)..].Trim();
            bool hit = line[..eq].Split([',', ' ', ';'])
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .Any(e => e == "*" || TrimPattern(e).ToLowerInvariant() == ext);
            if (hit && cmd.Length > 0) return cmd;
        }
        var d = defaultCmd.Trim();
        return d.Length == 0 ? Default : d;
    }

    /// <summary>"*.md" / ".md" / "md" -> "md" (Rust: trim_start_matches("*.") then '.').</summary>
    private static string TrimPattern(string e)
    {
        while (e.StartsWith("*.", StringComparison.Ordinal)) e = e[2..];
        return e.TrimStart('.');
    }

    /// <summary>Splits a command line into arguments; double quotes group words ("" is an empty argument).</summary>
    public static List<string> SplitArgs(string s)
    {
        var outList = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in s)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any || cur.Length > 0)
                {
                    outList.Add(cur.ToString());
                    cur.Clear();
                    any = false;
                }
            }
            else cur.Append(c);
        }
        if (any || cur.Length > 0) outList.Add(cur.ToString());
        return outList;
    }

    /// <summary>Program + arguments with {file}, {dir} and {line} filled in. The file is appended when the template
    /// does not mention it.</summary>
    public static List<string> Build(string template, string file)
    {
        var dir = VPath.Parent(file) ?? "";
        var parts = SplitArgs(template);
        bool usesFile = parts.Any(p => p.Contains("{file}"));
        for (int i = 0; i < parts.Count; i++)
            parts[i] = parts[i].Replace("{file}", file).Replace("{dir}", dir).Replace("{line}", "1");
        if (!usesFile) parts.Add(file);
        return parts;
    }

    /// <summary>Finds <paramref name="name"/> (+ "", .exe, .cmd, .bat, .com) on PATH, or as given when it has a
    /// directory; falls back to the bare name. Only candidates with an extension count (like Rust), so "code"
    /// resolves to code.cmd rather than a stray extension-less file.</summary>
    internal static string ResolveProgram(string name, string? pathVar = null)
    {
        string[] exts = ["", ".exe", ".cmd", ".bat", ".com"];
        bool hasDir = name.Contains('\\') || name.Contains('/');
        IEnumerable<string> dirs = hasDir
            ? [""]
            : (pathVar ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(d => d.Trim().Trim('"'));
        foreach (var d in dirs)
        {
            if (!hasDir && d.Length == 0) continue; // empty PATH entries
            foreach (var e in exts)
            {
                var cand = d.Length == 0 ? name + e : Path.Combine(d, name + e);
                if (File.Exists(cand) && VPath.Extension(cand).Length > 0) return cand;
            }
        }
        return name;
    }

    /// <summary>Starts the editor and returns immediately; throws InvalidOperationException with a readable message.</summary>
    public static void Launch(string template, string file)
    {
        var parts = Build(template, file);
        if (parts.Count == 0) throw new InvalidOperationException("empty editor command");
        var prog = ResolveProgram(parts[0]);
        var args = parts.Skip(1).ToList();
        bool batch = VPath.Extension(prog) is "cmd" or "bat";
        ProcessStartInfo psi;
        if (batch)
        {
            // Batch files need cmd.exe; /s makes cmd strip only the outer quotes so the inner quoting survives.
            // CreateNoWindow: no console flash for code.cmd and friends (Rust: CREATE_NO_WINDOW).
            var line = string.Join(' ', new[] { prog }.Concat(args).Select(QuoteForCmd));
            psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /s /c \"{line}\"")
            {
                CreateNoWindow = true,
            };
        }
        else
        {
            psi = new ProcessStartInfo(prog);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.UseShellExecute = false;
        var dir = VPath.Parent(file);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            throw new InvalidOperationException($"could not start \"{prog}\": {e.Message}", e);
        }
    }

    /// <summary>Quotes one argument for a cmd.exe command line (spaces and cmd metacharacters).</summary>
    internal static string QuoteForCmd(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', '%', '!', ',', ';', '=']) < 0) return a;
        return "\"" + a.Replace("\"", "\"\"") + "\"";
    }
}
