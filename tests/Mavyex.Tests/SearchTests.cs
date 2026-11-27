using Mavyex.Core;
using Mavyex.Services;
using Xunit;

namespace Mavyex.Tests;

public class SearchTests
{
    private static Matcher M(string q, SearchMode mode) => Search.Compile(q, new SearchOpts { Mode = mode });

    // ---- ported from search.rs

    [Fact]
    public void SmartWordsWildcardsAndExclusion()
    {
        var s = M("report !draft *.pdf", SearchMode.Smart);
        Assert.NotNull(s.Score("Report_final.PDF"));
        Assert.Null(s.Score("report_draft.pdf")); // excluded word
        Assert.Null(s.Score("report.docx")); // wildcard token must match
        Assert.Null(s.Score("notes.pdf")); // all words must match
    }

    [Fact]
    public void SmartPrefixesSwitchToFuzzyAndRegex()
    {
        Assert.NotNull(M("~rdme", SearchMode.Smart).Score("README.md"));
        Assert.NotNull(M(@"re:^img_\d+\.png$", SearchMode.Smart).Score("img_042.png"));
        Assert.Null(M(@"re:^img_\d+\.png$", SearchMode.Smart).Score("img_x.png"));
    }

    [Fact]
    public void FuzzyRanksCompactAndWordStartMatchesHigher()
    {
        var f = M("rdme", SearchMode.Fuzzy);
        var good = f.Score("README.md")!.Value;
        var scattered = f.Score("really_dumb_message_end.txt")!.Value;
        Assert.True(good > scattered, $"{good} should beat {scattered}");
        Assert.Null(f.Score("notes.txt"));
        Assert.True(f.Ranked);
    }

    [Fact]
    public void FuzzyReportsHighlightRanges() =>
        // R(0) ... D(3) M(4) E(5): one lone letter, then a compact run
        Assert.Equal([(0, 1), (3, 6)], M("rdme", SearchMode.Fuzzy).Ranges("README.md"));

    [Fact]
    public void WildcardIsAnchoredButPlainTextMeansContains()
    {
        Assert.NotNull(M("*.rs", SearchMode.Wildcard).Score("main.rs"));
        Assert.Null(M("*.rs", SearchMode.Wildcard).Score("main.rs.bak"));
        Assert.NotNull(M("report_??.pdf", SearchMode.Wildcard).Score("report_01.pdf"));
        Assert.NotNull(M("port", SearchMode.Wildcard).Score("report.pdf"));
    }

    [Fact]
    public void RegexErrorsDoNotHideFiles()
    {
        var r = M("([", SearchMode.Regex);
        Assert.NotNull(r.Error);
        Assert.NotNull(r.Score("anything")); // invalid pattern keeps the list unfiltered
        Assert.NotNull(M("^a.*z$", SearchMode.Regex).Score("abcz"));
    }

    [Fact]
    public void CaseSensitivityToggle()
    {
        Matcher Q(bool c) => Search.Compile("README", new SearchOpts { Mode = SearchMode.Smart, MatchCase = c });
        Assert.NotNull(Q(false).Score("readme.md"));
        Assert.Null(Q(true).Score("readme.md"));
        Assert.NotNull(Q(true).Score("README.md"));
    }

    [Fact]
    public void RegexRangesAreCharacterBased() =>
        Assert.Equal([(1, 3)], M("é+", SearchMode.Regex).Ranges("aéé.txt"));

    private static Entry E(string name, long size, long ageDays, bool dir) => new()
    {
        Name = name,
        Path = name,
        IsDir = dir,
        Size = size,
        Modified = DateTime.UtcNow - TimeSpan.FromSeconds(ageDays * 86400 + 60),
        Ext = dir ? "" : Entry.ExtOf(name, false),
    };

    [Fact]
    public void SizeAgeAndTypeFilters()
    {
        var o = new SearchOpts();
        var big = E("a.mp4", 20 << 20, 30, false);
        var small = E("b.png", 100, 1, false);
        var d = E("sub", 0, 1, true);
        var m = Search.Compile(":size>10mb", o);
        Assert.True(m.FiltersOk(big) && !m.FiltersOk(small) && !m.FiltersOk(d));
        m = Search.Compile(":age<7d", o);
        Assert.True(!m.FiltersOk(big) && m.FiltersOk(small));
        m = Search.Compile(":modified>7d", o);
        Assert.True(m.FiltersOk(big) && !m.FiltersOk(small));
        m = Search.Compile(":type=img,video", o);
        Assert.True(m.FiltersOk(big) && m.FiltersOk(small) && !m.FiltersOk(d));
        m = Search.Compile(":type:dir", o);
        Assert.True(m.FiltersOk(d) && !m.FiltersOk(big));
        m = Search.Compile(":date>2000-01-01 :size<1kb b", o);
        Assert.True(m.FiltersOk(small) && !m.FiltersOk(big));
        Assert.Equal(0, m.Score("b.png"));
        Assert.Null(m.Score("zzz"));
    }

    [Fact]
    public void BadFilterIsAnErrorAndUnknownColonIsText()
    {
        var o = new SearchOpts();
        Assert.NotNull(Search.Compile(":size>abc", o).Error);
        Assert.NotNull(Search.Compile(":size", o).Error);
        var m = Search.Compile(":foo", o);
        Assert.Null(m.Error);
        Assert.Equal(0, m.Score("x:foo.txt"));
    }

    // ---- additional

    [Theory]
    [InlineData("*.rs", "main.RS", false, true)]
    [InlineData("*.rs", "main.RS", true, false)]
    [InlineData("a?c", "abc", false, true)]
    [InlineData("a?c", "ac", false, false)]
    [InlineData("*", "", false, true)]
    [InlineData("", "", false, true)]
    [InlineData("", "x", false, false)]
    [InlineData("a*b*c", "aXXbYYc", false, true)]
    [InlineData("a*b*c", "aXXbYY", false, false)]
    [InlineData("node_*", "node_modules", false, true)]
    public void Glob(string pat, string text, bool matchCase, bool expected) =>
        Assert.Equal(expected, Search.GlobMatch(pat, text, matchCase));

    [Fact]
    public void EmptyQueryMatchesAll()
    {
        var m = M("   ", SearchMode.Smart);
        Assert.True(m.IsAll && m.NameAll);
        Assert.True(Matcher.All.IsAll);
        Assert.Equal(0, Matcher.All.Score("x"));
        Assert.True(M("re:", SearchMode.Smart).NameAll);
        Assert.True(M("~ ", SearchMode.Smart).NameAll);
        var f = Search.Compile(":size>1", new SearchOpts());
        Assert.True(f.NameAll);
        Assert.False(f.IsAll);
    }

    [Fact]
    public void FuzzyPrefersWordStartsAndCamelCase()
    {
        var f = M("fb", SearchMode.Fuzzy);
        // word starts (both letters) > camelCase boundary > letters in the middle of words
        int words = f.Score("foo_bar")!.Value, camel = f.Score("fooBar")!.Value, mid = f.Score("xfxxbx")!.Value;
        Assert.True(words > mid, $"{words} > {mid}");
        Assert.True(camel > mid, $"{camel} > {mid}");
        // whitespace in the query is ignored
        Assert.Equal(f.Score("foo_bar"), M("f b", SearchMode.Fuzzy).Score("foo_bar"));
        // the window is tightened: "ab" in "a_xab" highlights the later, compact pair
        Assert.Equal([(3, 5)], M("ab", SearchMode.Fuzzy).Ranges("a_xab"));
        Assert.Empty(M("zz", SearchMode.Fuzzy).Ranges("abc"));
    }

    [Fact]
    public void SmartRangesAreMergedAndSkipExclusionsAndGlobs()
    {
        var m = M("rep port !x *.pdf", SearchMode.Smart);
        Assert.Equal([(0, 6)], m.Ranges("Report.pdf"));
        // a lone "!" is a normal word, not an exclusion
        Assert.NotNull(M("!", SearchMode.Smart).Score("wow!.txt"));
        Assert.Null(M("!", SearchMode.Smart).Score("wow.txt"));
    }

    [Fact]
    public void RegexRangesAreLimitedTo16AndSkipEmptyMatches()
    {
        Assert.Equal(16, M("a", SearchMode.Regex).Ranges(new string('a', 40)).Count);
        Assert.Empty(M("x*", SearchMode.Regex).Ranges("abc"));
        Assert.NotNull(M("ABC", SearchMode.Regex).Score("xabcx")); // case-insensitive by default
        Assert.Null(Search.Compile("ABC", new SearchOpts { Mode = SearchMode.Regex, MatchCase = true }).Score("xabcx"));
    }

    [Fact]
    public void ErrorMessages()
    {
        Assert.Equal("bad filter :size>abc", Search.Compile(":size>abc", new SearchOpts()).Error);
        Assert.Equal("bad filter :age<7", Search.Compile("x :age<7", new SearchOpts()).Error);
        Assert.Equal("bad filter :ext=", Search.Compile(":ext=", new SearchOpts()).Error);
        Assert.Equal("bad filter :date=2020-13-45", Search.Compile(":date=2020-13-45", new SearchOpts()).Error);
        var r = M("([", SearchMode.Regex).Error!;
        Assert.StartsWith("error: ", r);
        Assert.DoesNotContain("\n", r);
        // an invalid regex keeps the filters
        var rf = M(":ext=txt ([", SearchMode.Regex);
        Assert.NotNull(rf.Error);
        Assert.False(rf.FiltersOk(E("a.png", 1, 1, false)));
    }

    [Fact]
    public void FilterParsingUnitsAndOperators()
    {
        Assert.Equal(1536UL, Search.ParseSize("1.5k"));
        Assert.Equal(10UL << 20, Search.ParseSize("10 MiB"));
        Assert.Equal(5UL, Search.ParseSize("5"));
        Assert.Null(Search.ParseSize("5x"));
        Assert.Null(Search.ParseSize("mb"));
        Assert.Equal(2UL * 7 * 86400, Search.ParseAge("2w"));
        Assert.Equal(90UL, Search.ParseAge("1.5min"));
        Assert.Equal(30UL * 86400, Search.ParseAge("1MO"));
        Assert.Null(Search.ParseAge("7"));
        Assert.Null(Search.ParseAge("7 d")); // no trim between number and unit, like Rust

        var o = new SearchOpts();
        var e = E("x.bin", 1024, 0, false);
        Assert.True(Search.Compile(":size>=1kb", o).FiltersOk(e));
        Assert.True(Search.Compile(":size<=1k", o).FiltersOk(e));
        Assert.True(Search.Compile(":size=1024", o).FiltersOk(e));
        Assert.False(Search.Compile(":size>1kb", o).FiltersOk(e));
        Assert.True(Search.Compile(":SIZE:1kb", o).FiltersOk(e)); // key is case-insensitive, ':' = '='
    }

    [Fact]
    public void KindAndExtFilters()
    {
        var o = new SearchOpts();
        var rs = E("main.rs", 1, 1, false);
        var d = E("src", 0, 1, true);
        Assert.True(Search.Compile(":ext=.RS", o).FiltersOk(rs));
        Assert.True(Search.Compile(":type=code", o).FiltersOk(rs));
        Assert.False(Search.Compile(":type=doc", o).FiltersOk(rs));
        Assert.True(Search.Compile(":type=file", o).FiltersOk(rs));
        Assert.True(Search.Compile(":type=folder", o).FiltersOk(d));
        Assert.False(Search.Compile(":ext=rs", o).FiltersOk(d)); // folders never match extensions
        Assert.True(Search.Compile(":ext=txt,,rs", o).FiltersOk(rs));
    }

    [Fact]
    public void DateFilterEqualsMeansThatLocalDay()
    {
        var o = new SearchOpts();
        var noon = DateTime.SpecifyKind(new DateTime(2024, 3, 15, 12, 0, 0), DateTimeKind.Local).ToUniversalTime();
        var e = new Entry { Name = "a", Path = "a", Modified = noon };
        Assert.True(Search.Compile(":date=2024-03-15", o).FiltersOk(e));
        Assert.True(Search.Compile(":date=2024-3-15", o).FiltersOk(e));
        Assert.False(Search.Compile(":date=2024-03-16", o).FiltersOk(e));
        Assert.True(Search.Compile(":date<2024-03-16", o).FiltersOk(e));
        Assert.True(Search.Compile(":mod>=2024-03-15", o).FiltersOk(e));
        Assert.False(Search.Compile(":date>2024-03-15", o).FiltersOk(new Entry { Name = "n" })); // unknown mtime
    }

    [Fact]
    public void ModeTexts()
    {
        Assert.Equal(["Smart", "Fuzzy", "Wildcard", "Regex"], SearchModes.All.Select(m => m.Label()));
        Assert.Equal(".*", SearchMode.Regex.Icon());
        Assert.Contains(".NET regular expression", SearchMode.Regex.Tooltip());
        Assert.StartsWith("type to filter", SearchMode.Smart.Hint());
    }
}
