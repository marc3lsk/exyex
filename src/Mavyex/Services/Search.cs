using Mavyex.Core;

namespace Mavyex.Services;

// STUB – to be replaced by the port of search.rs (subagent "search"). Keep these signatures.

public static class SearchModes
{
    public static readonly SearchMode[] All = [SearchMode.Smart, SearchMode.Fuzzy, SearchMode.Wildcard, SearchMode.Regex];
    public static string Label(this SearchMode m) => m.ToString();
    public static string Icon(this SearchMode m) => m switch { SearchMode.Smart => "✦", SearchMode.Fuzzy => "≈", SearchMode.Wildcard => "*", _ => ".*" };
    public static string Hint(this SearchMode m) => "";
    public static string Tooltip(this SearchMode m) => m.ToString();
}

/// <summary>Compiled query (search.rs Matcher): name matching by mode + metadata filters (:size :age :date :type :ext).</summary>
public sealed class Matcher
{
    public static readonly Matcher All = new();

    /// <summary>Matches everything (empty query, no filters).</summary>
    public bool IsAll => true;
    /// <summary>The name part of the query is empty (only :filters, or nothing).</summary>
    public bool NameAll => true;
    public bool FiltersOk(Entry e) => true;
    /// <summary>Set when the pattern is invalid; the list then stays unfiltered.</summary>
    public string? Error => null;
    /// <summary>Fuzzy results are ordered by relevance instead of the sort column.</summary>
    public bool Ranked => false;
    /// <summary>null = no match; higher = better (only meaningful when Ranked).</summary>
    public int? Score(string name) => 0;
    /// <summary>Character ranges [start, end) of name to highlight.</summary>
    public List<(int Start, int End)> Ranges(string name) => [];
}

public static class Search
{
    /// <summary>Case-aware glob with * and ? (whole text).</summary>
    public static bool GlobMatch(string pat, string text, bool matchCase) => false;
    public static Matcher Compile(string query, SearchOpts opts) => Matcher.All;
}
