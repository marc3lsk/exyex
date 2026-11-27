using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mavyex.Core;

namespace Mavyex.Services;

// Port of search.rs: in-folder search – Smart / Fuzzy / Wildcard / Regex matching with scoring
// (for ranking) and match positions (for highlighting), plus metadata filters (:size :age :date :type :ext).
//
// Indices are UTF-16 code units (C# string indices), which is what WPF text ranges use; Rust counted
// code points. Both agree for everything outside the astral planes.

public static class SearchModes
{
    public static readonly SearchMode[] All = [SearchMode.Smart, SearchMode.Fuzzy, SearchMode.Wildcard, SearchMode.Regex];

    public static string Label(this SearchMode m) => m switch
    {
        SearchMode.Smart => "Smart",
        SearchMode.Fuzzy => "Fuzzy",
        SearchMode.Wildcard => "Wildcard",
        _ => "Regex",
    };

    /// <summary>Short glyph for the compact search bar (the tooltip carries the full name).</summary>
    public static string Icon(this SearchMode m) => m switch
    {
        SearchMode.Smart => "✦",
        SearchMode.Fuzzy => "≈",
        SearchMode.Wildcard => "*",
        _ => ".*",
    };

    public static string Hint(this SearchMode m) => m switch
    {
        SearchMode.Smart => "type to filter  ·  *.rs  ·  !exclude  ·  ~fuzzy  ·  re:regex  ·  :size>10mb  :age<7d  :type=img",
        SearchMode.Fuzzy => "fuzzy: letters in order, e.g. rdme finds README.md",
        SearchMode.Wildcard => "wildcard: * and ?, e.g. *.rs or report_??.pdf",
        _ => @"regular expression, e.g. ^img_\d+\.(jpg|png)$",
    };

    public static string Tooltip(this SearchMode m) => m switch
    {
        SearchMode.Smart => "Smart (Alt+1)\nWords are combined (AND). Plain text = contains,\n*/? = wildcard, !word = exclude, ~text = fuzzy, re:pattern = regex",
        SearchMode.Fuzzy => "Fuzzy (Alt+2)\nThe letters must appear in this order, not necessarily next to each other.\nBest matches come first.",
        SearchMode.Wildcard => "Wildcard (Alt+3)\n* = any text, ? = one character. The whole name must match;\nwithout * or ? the text is matched anywhere in the name.",
        _ => "Regex (Alt+4)\n.NET regular expression, matched anywhere in the name.",
    };
}

/// <summary>Compiled query (search.rs Matcher): name matching by mode + metadata filters (:size :age :date :type :ext).
/// Built by <see cref="Search.Compile"/>; immutable once built, safe to share between threads.</summary>
public sealed class Matcher
{
    internal enum KindTag { All, Smart, Fuzzy, Wild, Re, Bad }

    /// <summary>A Smart-mode word: plain text (already case-folded) or a glob; Neg = "!word".</summary>
    internal readonly record struct Tok(string Text, bool IsGlob, bool Neg);

    public static readonly Matcher All = new();

    internal KindTag Kind { get; init; } = KindTag.All;
    internal bool Case { get; init; }
    internal Tok[] Toks { get; init; } = [];
    /// <summary>Fuzzy: folded query without whitespace; Wild: the glob; Bad: the error message.</summary>
    internal string Text { get; init; } = "";
    internal Regex? Re { get; init; }
    internal SearchFilter[] Filters { get; init; } = [];

    /// <summary>Matches everything (empty query, no filters).</summary>
    public bool IsAll => Kind == KindTag.All && Filters.Length == 0;
    /// <summary>The name part of the query is empty (only :filters, or nothing).</summary>
    public bool NameAll => Kind == KindTag.All;

    /// <summary>Metadata filters (:size>..., :age&lt;..., :type=...).</summary>
    public bool FiltersOk(Entry e)
    {
        foreach (var f in Filters)
            if (!f.Test(e)) return false;
        return true;
    }

    /// <summary>Set when the pattern is invalid; the list then stays unfiltered.</summary>
    public string? Error => Kind == KindTag.Bad ? Text : null;
    /// <summary>Fuzzy results are ordered by relevance instead of the sort column.</summary>
    public bool Ranked => Kind == KindTag.Fuzzy;

    /// <summary>null = no match; higher = better (only meaningful when Ranked).</summary>
    public int? Score(string name)
    {
        switch (Kind)
        {
            case KindTag.Fuzzy:
                return Search.FuzzyMatch(Text, name, Case) is { } m ? m.Score : null;
            case KindTag.Wild:
                return Search.GlobMatch(Text, name, Case) ? 0 : null;
            case KindTag.Re:
                try
                {
                    return Re!.IsMatch(name) ? 0 : null;
                }
                catch (RegexMatchTimeoutException)
                {
                    return null;
                }
            case KindTag.Smart:
                var hay = Search.Folded(name, Case);
                foreach (var t in Toks)
                {
                    bool hit = t.IsGlob ? Search.GlobMatch(t.Text, name, Case) : hay.Contains(t.Text, StringComparison.Ordinal);
                    if (hit == t.Neg) return null;
                }
                return 0;
            default: // All, Bad
                return 0;
        }
    }

    /// <summary>Character ranges [start, end) of name to highlight (sorted, non-overlapping).</summary>
    public List<(int Start, int End)> Ranges(string name)
    {
        switch (Kind)
        {
            case KindTag.Fuzzy:
                return Search.FuzzyMatch(Text, name, Case) is { } m ? Search.PositionsToRanges(m.Positions) : [];
            case KindTag.Re:
                var outList = new List<(int, int)>();
                try
                {
                    for (var mt = Re!.Match(name); mt.Success && outList.Count < 16; mt = mt.NextMatch())
                        if (mt.Length > 0) outList.Add((mt.Index, mt.Index + mt.Length));
                }
                catch (RegexMatchTimeoutException) { }
                return outList;
            case KindTag.Smart:
                var hay = Search.Folded(name, Case);
                var v = new List<(int, int)>();
                foreach (var t in Toks)
                {
                    if (t.IsGlob || t.Neg || t.Text.Length == 0) continue;
                    int i = hay.IndexOf(t.Text, StringComparison.Ordinal);
                    if (i >= 0) v.Add((i, i + t.Text.Length));
                }
                return Search.Merge(v);
            default:
                return [];
        }
    }
}

internal enum Cmp { Gt, Ge, Lt, Le, Eq }

internal enum FilterKind { Size, Age, Date, Kind }

/// <summary>One metadata filter token of the query.</summary>
internal sealed class SearchFilter
{
    public FilterKind Kind { get; init; }
    public Cmp Cmp { get; init; }
    /// <summary>Size: bytes; Age: seconds since the modification (:age&lt;7d = modified within the last 7 days).</summary>
    public ulong N { get; init; }
    /// <summary>Date: start of the local day, in UTC.</summary>
    public DateTime Date { get; init; }
    /// <summary>Group names (img, video, ...), dir/folder, file or plain extensions.</summary>
    public List<string> Kinds { get; init; } = [];

    private static bool Test<T>(Cmp c, T a, T b) where T : IComparable<T>
    {
        int r = a.CompareTo(b);
        return c switch
        {
            Cmp.Gt => r > 0,
            Cmp.Ge => r >= 0,
            Cmp.Lt => r < 0,
            Cmp.Le => r <= 0,
            _ => r == 0,
        };
    }

    public bool Test(Entry e)
    {
        switch (Kind)
        {
            case FilterKind.Size:
                return !e.IsDir && Test(Cmp, (ulong)Math.Max(0, e.Size), N);
            case FilterKind.Age:
                if (e.Modified is not { } m) return false;
                long ticks = DateTime.UtcNow.Ticks - m.Ticks; // a future mtime counts as age 0
                ulong secs = ticks <= 0 ? 0 : (ulong)(ticks / TimeSpan.TicksPerSecond);
                return Test(Cmp, secs, N);
            case FilterKind.Date:
                if (e.Modified is not { } d) return false;
                long t = d.Ticks;
                if (Cmp == Cmp.Eq) return t >= Date.Ticks && t < Date.Ticks + TimeSpan.TicksPerDay;
                return Test(Cmp, t, Date.Ticks);
            default:
                foreach (var k in Kinds)
                {
                    bool ok = k switch
                    {
                        "dir" or "folder" => e.IsDir,
                        "file" => !e.IsDir,
                        _ => !e.IsDir && ((Search.Groups.TryGetValue(k, out var exts) && exts.Contains(e.Ext)) || e.Ext == k),
                    };
                    if (ok) return true;
                }
                return false;
        }
    }
}

public static class Search
{
    /// <summary>.NET regex backtracks (Rust's is linear-time): a timeout keeps a pathological pattern from freezing.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    internal static readonly Dictionary<string, HashSet<string>> Groups = new()
    {
        ["img"] = ["png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "svg", "tif", "tiff", "heic"],
        ["video"] = ["mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v"],
        ["audio"] = ["mp3", "wav", "flac", "ogg", "m4a", "aac", "wma"],
        ["doc"] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "txt", "md", "rtf", "csv"],
        ["archive"] = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "jar"],
        ["code"] = ["rs", "py", "js", "ts", "tsx", "jsx", "java", "c", "cpp", "h", "hpp", "cs", "go", "rb", "php", "sh", "ps1", "json", "toml", "yaml", "yml", "xml", "html", "css", "sql"],
    };

    internal static char Fold(char c, bool matchCase) => matchCase ? c : char.ToLowerInvariant(c);

    /// <summary>Per-character case fold: keeps the length, so indices map 1:1 to the original name.</summary>
    internal static string Folded(string s, bool matchCase)
    {
        if (matchCase) return s;
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++) span[i] = char.ToLowerInvariant(src[i]);
        });
    }

    private static string[] SplitWhitespace(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Case-aware glob with * and ? (whole text).</summary>
    public static bool GlobMatch(string pat, string text, bool matchCase)
    {
        var p = Folded(pat, matchCase);
        var t = Folded(text, matchCase);
        int pi = 0, ti = 0, star = -1, mark = 0;
        while (ti < t.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || p[pi] == t[ti]))
            {
                pi++;
                ti++;
            }
            else if (pi < p.Length && p[pi] == '*')
            {
                star = pi;
                mark = ti;
                pi++;
            }
            else if (star >= 0)
            {
                pi = star + 1;
                mark++;
                ti = mark;
            }
            else
            {
                return false;
            }
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }

    /// <summary>q must already be folded. Returns the score and the matched positions.</summary>
    internal static (int Score, int[] Positions)? FuzzyMatch(string q, string name, bool matchCase)
    {
        if (q.Length == 0) return (0, []);
        var t = Folded(name, matchCase);
        // forward pass: where does the leftmost match end?
        int qi = 0, end = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == q[qi])
            {
                qi++;
                if (qi == q.Length)
                {
                    end = i;
                    break;
                }
            }
        }
        if (qi < q.Length) return null;
        // backward pass: tighten the window so the match is as compact as possible
        var pos = new int[q.Length];
        int qj = q.Length;
        for (int i = end; qj > 0 && i >= 0; i--)
        {
            if (t[i] == q[qj - 1])
            {
                qj--;
                pos[qj] = i;
            }
        }
        int score = 0, prev = -1;
        foreach (int p in pos)
        {
            int s = 16;
            if (prev >= 0)
            {
                if (prev + 1 == p) s += 24; // consecutive
                else s -= Math.Min(p - prev - 1, 8); // gap penalty
            }
            if (p == 0 || !char.IsLetterOrDigit(name[p - 1])) s += 14; // start of a word
            else if (char.IsUpper(name[p]) && char.IsLower(name[p - 1])) s += 10; // camelCase boundary
            score += s;
            prev = p;
        }
        // a compact match beats one spread over the whole name
        int span = pos[^1] - pos[0] + 1;
        score -= (span - q.Length) * 2;
        if (pos[0] == 0) score += 10;
        score -= name.Length / 6; // shorter names win ties
        return (score, pos);
    }

    internal static List<(int Start, int End)> PositionsToRanges(int[] pos)
    {
        var outList = new List<(int Start, int End)>();
        foreach (int p in pos)
        {
            if (outList.Count > 0 && outList[^1].End == p) outList[^1] = (outList[^1].Start, p + 1);
            else outList.Add((p, p + 1));
        }
        return outList;
    }

    internal static List<(int Start, int End)> Merge(List<(int Start, int End)> v)
    {
        v.Sort();
        var outList = new List<(int Start, int End)>();
        foreach (var r in v)
        {
            if (outList.Count > 0 && r.Start <= outList[^1].End) outList[^1] = (outList[^1].Start, Math.Max(outList[^1].End, r.End));
            else outList.Add(r);
        }
        return outList;
    }

    private static Matcher BuildRegex(string pat, bool matchCase)
    {
        var opts = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Matcher { Kind = Matcher.KindTag.Re, Case = matchCase, Re = new Regex(pat, opts, RegexTimeout) };
        }
        catch (ArgumentException e)
        {
            return new Matcher { Kind = Matcher.KindTag.Bad, Case = matchCase, Text = RegexError(e) };
        }
    }

    /// <summary>Keeps it short like Rust ("error: ..."): .NET says "Invalid pattern '…' at offset N. Reason."</summary>
    private static string RegexError(ArgumentException e)
    {
        var msg = e.Message;
        int at = msg.IndexOf(" at offset ", StringComparison.Ordinal);
        int dot = at >= 0 ? msg.IndexOf(". ", at, StringComparison.Ordinal) : -1;
        var reason = (dot >= 0 ? msg[(dot + 2)..] : msg).Trim().TrimEnd('.');
        return reason.Length == 0 ? "invalid regex" : "error: " + reason;
    }

    // ------------------------------------------------------------------ filters

    private static readonly (string Op, Cmp Cmp)[] Ops = [(">=", Cmp.Ge), ("<=", Cmp.Le), (">", Cmp.Gt), ("<", Cmp.Lt), ("=", Cmp.Eq), (":", Cmp.Eq)];

    /// <summary>Rust's `f64 as u64`: saturating, NaN = 0.</summary>
    private static ulong SatU64(double d) => double.IsNaN(d) || d <= 0 ? 0 : d >= 18446744073709551615.0 ? ulong.MaxValue : (ulong)d;

    private static bool ParseNum(string s, out double n) =>
        double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out n);

    /// <summary>Index of the first char that is not a digit or '.', -1 when there is none.</summary>
    private static int NumEnd(string v)
    {
        for (int i = 0; i < v.Length; i++)
            if (!(char.IsAsciiDigit(v[i]) || v[i] == '.')) return i;
        return -1;
    }

    internal static ulong? ParseSize(string v)
    {
        v = v.Trim().ToLowerInvariant();
        int i = NumEnd(v);
        if (i < 0) i = v.Length;
        if (!ParseNum(v[..i], out double n)) return null;
        double mul = v[i..].Trim() switch
        {
            "" or "b" => 1.0,
            "k" or "kb" or "kib" => 1024.0,
            "m" or "mb" or "mib" => 1024.0 * 1024.0,
            "g" or "gb" or "gib" => 1024.0 * 1024.0 * 1024.0,
            "t" or "tb" or "tib" => Math.Pow(1024.0, 4),
            _ => -1,
        };
        return mul < 0 ? null : SatU64(n * mul);
    }

    internal static ulong? ParseAge(string v)
    {
        v = v.Trim().ToLowerInvariant();
        int i = NumEnd(v);
        if (i < 0) return null; // a unit is required
        if (!ParseNum(v[..i], out double n)) return null;
        double unit = v[i..] switch
        {
            "min" => 60.0,
            "h" => 3600.0,
            "d" => 86400.0,
            "w" => 7.0 * 86400.0,
            "mo" => 30.0 * 86400.0,
            "y" => 365.0 * 86400.0,
            _ => -1,
        };
        return unit < 0 ? null : SatU64(n * unit);
    }

    /// <summary>YYYY-MM-DD at local midnight, returned in UTC (Entry.Modified is UTC).</summary>
    internal static DateTime? ParseDate(string v)
    {
        if (!DateTime.TryParseExact(v.Trim(), "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        var local = DateTime.SpecifyKind(d.Date, DateTimeKind.Local);
        // chrono's .single(): a midnight skipped or repeated by a DST change is rejected
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local)) return null;
        return local.ToUniversalTime();
    }

    /// <summary>null = not a filter token (plain text); error set = looks like one but is malformed.</summary>
    private static SearchFilter? ParseFilter(string tok, out string? error)
    {
        error = null;
        if (!tok.StartsWith(':')) return null;
        var body = tok[1..];
        int keyEnd = 0;
        while (keyEnd < body.Length && char.IsAsciiLetter(body[keyEnd])) keyEnd++;
        var key = body[..keyEnd].ToLowerInvariant();
        var rest = body[keyEnd..];
        if (key is not ("size" or "age" or "modified" or "mod" or "date" or "type" or "ext")) return null;
        var bad = $"bad filter {tok}";
        Cmp cmp = Cmp.Eq;
        string val = "";
        bool hasOp = false;
        foreach (var (op, c) in Ops)
        {
            if (rest.StartsWith(op, StringComparison.Ordinal))
            {
                (cmp, val, hasOp) = (c, rest[op.Length..], true);
                break;
            }
        }
        if (!hasOp || val.Length == 0)
        {
            error = bad;
            return null;
        }
        SearchFilter? f = key switch
        {
            "size" => ParseSize(val) is { } n ? new SearchFilter { Kind = FilterKind.Size, Cmp = cmp, N = n } : null,
            "age" => ParseAge(val) is { } a ? new SearchFilter { Kind = FilterKind.Age, Cmp = cmp, N = a } : null,
            "modified" or "mod" or "date" => ParseAge(val) is { } age
                ? new SearchFilter { Kind = FilterKind.Age, Cmp = cmp, N = age }
                : ParseDate(val) is { } d ? new SearchFilter { Kind = FilterKind.Date, Cmp = cmp, Date = d } : null,
            _ => new SearchFilter
            {
                Kind = FilterKind.Kind,
                Kinds = val.ToLowerInvariant().Split(',').Select(s => s.Trim().TrimStart('.')).Where(s => s.Length > 0).ToList(),
            },
        };
        if (f is null) error = bad;
        return f;
    }

    /// <summary>Compiles a query: :size>10mb, :age&lt;7d, :type=img tokens are filters on the item's metadata,
    /// everything else is matched against the name by the chosen mode.</summary>
    public static Matcher Compile(string query, SearchOpts opts)
    {
        var filters = new List<SearchFilter>();
        var rest = new List<string>();
        foreach (var tok in SplitWhitespace(query))
        {
            var f = ParseFilter(tok, out var err);
            if (err is not null) return new Matcher { Kind = Matcher.KindTag.Bad, Case = opts.MatchCase, Text = err };
            if (f is not null) filters.Add(f);
            else rest.Add(tok);
        }
        return CompileText(string.Join(' ', rest), opts, filters.ToArray());
    }

    private static Matcher CompileText(string query, SearchOpts opts, SearchFilter[] filters)
    {
        var q = query.Trim();
        bool c = opts.MatchCase;
        Matcher AllM() => new() { Kind = Matcher.KindTag.All, Case = c, Filters = filters };
        Matcher Fuzzy(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
                if (!char.IsWhiteSpace(ch)) sb.Append(Fold(ch, c));
            return new Matcher { Kind = Matcher.KindTag.Fuzzy, Case = c, Text = sb.ToString(), Filters = filters };
        }
        Matcher Regex(string pat)
        {
            var m = BuildRegex(pat, c);
            return new Matcher { Kind = m.Kind, Case = c, Re = m.Re, Text = m.Text, Filters = filters };
        }
        if (q.Length == 0) return AllM();
        switch (opts.Mode)
        {
            case SearchMode.Fuzzy:
                return Fuzzy(q);
            case SearchMode.Regex:
                return Regex(q);
            case SearchMode.Wildcard:
                return new Matcher { Kind = Matcher.KindTag.Wild, Case = c, Text = q.IndexOfAny(['*', '?']) >= 0 ? q : $"*{q}*", Filters = filters };
            default:
                if (q.StartsWith("re:", StringComparison.Ordinal))
                {
                    var r = q[3..].Trim();
                    return r.Length == 0 ? AllM() : Regex(r);
                }
                if (q.StartsWith('~'))
                {
                    var r = q[1..].Trim();
                    return r.Length == 0 ? AllM() : Fuzzy(r);
                }
                var toks = SplitWhitespace(q).Select(w =>
                {
                    // a lone "!" is plain text, not an empty exclusion
                    bool neg = w.Length > 1 && w[0] == '!';
                    if (neg) w = w[1..];
                    return w.IndexOfAny(['*', '?']) >= 0 ? new Matcher.Tok(w, true, neg) : new Matcher.Tok(Folded(w, c), false, neg);
                }).ToArray();
                return new Matcher { Kind = Matcher.KindTag.Smart, Case = c, Toks = toks, Filters = filters };
        }
    }
}
