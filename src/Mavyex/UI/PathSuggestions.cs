using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

/// <summary>Path-bar suggestions (app.rs suggestions): favorites + frecent folders, and subfolder completion.</summary>
public static class PathSuggestions
{
    private const int Max = 14;

    public static List<Sugg> Compute(string text, string orig, string baseDir, Store store)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Push(List<Sugg> o, string p, string tag)
        {
            if (o.Count < Max && seen.Add(p)) o.Add(new Sugg(p, tag));
        }
        var q = text == orig ? "" : text.Trim();
        var fq = q.Replace('/', '\\'); // recents of remote folders use backslashes internally

        // just opened (text untouched): favorites, then most frecent folders
        if (q.Length == 0)
        {
            var o = new List<Sugg>();
            foreach (var f in store.Favorites)
                if (!VPath.Same(f, baseDir) && Fs.IsDirPath(f)) Push(o, f, "★ favorite");
            foreach (var (p, _) in store.Frecent().Take(40))
                if (!VPath.Same(p, baseDir) && Fs.IsDirPath(p)) Push(o, p, "recent");
            return o;
        }

        List<string> Subfolders(string dirPart, string leaf)
        {
            string dir;
            if (dirPart.Length == 0) dir = baseDir;
            else if (VPath.ParseUserPath(dirPart, baseDir) is { } d) dir = d;
            else return [];
            if (Sftp.IsRemote(dir)) return []; // never hit the network on every keystroke
            List<Entry> rd;
            try { rd = Fs.ReadDir(dir); } catch { return []; }
            var l = leaf.ToLowerInvariant();
            return rd.Where(e => e.IsDir && (l.Length == 0 || e.Name.ToLowerInvariant().Contains(l)))
                     .Select(e => (Starts: e.Name.ToLowerInvariant().StartsWith(l), e.Path, e.Name))
                     .OrderByDescending(x => x.Starts).ThenBy(x => x.Name, Text.Natural)
                     .Take(8).Select(x => x.Path).ToList();
        }
        int cut = q.LastIndexOfAny(['\\', '/']);
        var (dirPart, leaf) = cut >= 0 ? (q[..(cut + 1)], q[(cut + 1)..]) : ("", q);
        bool pathlike = q.IndexOfAny(['\\', '/', ':', '~', '%']) >= 0;

        // favorites + recent folders matching anywhere in the path
        var ranked = new List<(int Score, string Path, string Tag)>();
        foreach (var f in store.Favorites)
        {
            var best = Max2(Text.Fuzzy(fq, f), Text.Fuzzy(fq, VPath.DisplayName(f)) + 20);
            if (best is { } s) ranked.Add((s + 500, f, "★ favorite"));
        }
        foreach (var (p, score) in store.Frecent())
        {
            var full = Sftp.Pretty(p) is { } pr ? $"{p} {pr}" : p;
            var best = Max2(Text.Fuzzy(fq, full), Text.Fuzzy(fq, VPath.DisplayName(p)) + 20);
            if (best is { } s) ranked.Add((s + Math.Min((int)score, 100) / 2, p, "recent"));
        }
        var hist = ranked.OrderByDescending(r => r.Score).Where(r => Fs.IsDirPath(r.Path)).Take(8).Select(r => (r.Path, r.Tag)).ToList();

        var outList = new List<Sugg>();
        if (pathlike)
        {
            foreach (var p in Subfolders(dirPart, leaf).Take(hist.Count == 0 ? 8 : 6)) Push(outList, p, "");
            foreach (var (p, t) in hist) Push(outList, p, t);
        }
        else
        {
            foreach (var (p, t) in hist) Push(outList, p, t);
            foreach (var p in Subfolders("", q)) Push(outList, p, "");
        }
        return outList;
    }

    private static int? Max2(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
