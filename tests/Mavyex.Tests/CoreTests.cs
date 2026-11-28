using System.IO;
using System.Text.Json;
using Mavyex.Core;
using Xunit;

namespace Mavyex.Tests;

public class VPathTests
{
    [Theory]
    [InlineData(@"C:\a\b", @"C:\a")]
    [InlineData(@"C:\a", @"C:\")]
    [InlineData(@"C:\", null)]
    [InlineData(@"C:\a\", @"C:\")]
    [InlineData(@"sftp:\u@h\~", @"sftp:\u@h")]
    [InlineData(@"sftp:\u@h", "sftp:")]
    [InlineData("sftp:", "")]
    [InlineData(@"\\srv\share\x", @"\\srv\share\")]
    [InlineData(@"\\srv\share\", null)]
    public void Parent(string p, string? expected) => Assert.Equal(expected, VPath.Parent(p));

    [Theory]
    [InlineData(@"C:\a\b.txt", "b.txt")]
    [InlineData(@"C:\", null)]
    [InlineData("sftp:", "sftp:")]
    public void FileName(string p, string? expected) => Assert.Equal(expected, VPath.FileName(p));

    [Fact]
    public void StartsWithIsComponentWise()
    {
        Assert.True(VPath.StartsWith(@"C:\a\b", @"c:\A"));
        Assert.False(VPath.StartsWith(@"C:\ab", @"C:\a"));
        Assert.Equal(@"b\c", VPath.StripPrefix(@"C:\a\b\c", @"C:\a"));
        Assert.Equal("", VPath.StripPrefix(@"C:\a", @"C:\a\"));
    }

    [Fact]
    public void JoinAndNormalize()
    {
        Assert.Equal(@"C:\a\b", VPath.Join(@"C:\a", "b"));
        Assert.Equal(@"C:\b", VPath.Join(@"C:\", "b"));
        Assert.Equal(@"C:\x\z", VPath.LexicalNormalize(@"C:\x\y\..\.\z"));
        Assert.Equal(@"C:\", VPath.LexicalNormalize(@"C:\..\.."));
    }

    [Fact]
    public void ParseUserPath()
    {
        Assert.Equal(@"C:\Windows", VPath.ParseUserPath("\"C:/Windows\"", @"D:\"));
        Assert.Equal(@"D:\x\sub", VPath.ParseUserPath("sub", @"D:\x"));
        Assert.Equal(@"E:\", VPath.ParseUserPath("E:", @"D:\"));
        Assert.Equal(@"C:\Program Files", VPath.ParseUserPath("file:///C:/Program%20Files", @"D:\"));
    }

    [Fact]
    public void Extension()
    {
        Assert.Equal("gz", VPath.Extension(@"C:\a.tar.gz"));
        Assert.Equal("", VPath.Extension(@"C:\.bashrc"));
        Assert.Equal("a.tar", VPath.Stem(@"C:\a.tar.gz"));
    }
}

public class Sha256SumTests
{
    [Fact]
    public void ParsesSha256sumOutput()
    {
        var h1 = new string('a', 64);
        var h2 = new string('B', 64);
        var h3 = new string('0', 64);
        var outp = $"{h1}  /home/u/a file.txt\n{h2} */srv/bin.dat\r\n\\{h3}  /tmp/we\\\\ird\\nname\nsha256sum: /x: No such file or directory\n";
        var m = Mavyex.Services.Sftp.ParseSums(outp);
        Assert.Equal(h1, m["/home/u/a file.txt"]);
        Assert.Equal(new string('b', 64), m["/srv/bin.dat"]);
        Assert.Equal(h3, m["/tmp/we\\ird\nname"]);
        Assert.Equal(3, m.Count);
    }
}

public class CompareTests
{
    private static Dictionary<string, Mavyex.UI.Diff> Settled(Tab a, Tab b)
    {
        // hashes are computed in the background: poll until nothing is pending
        for (int i = 0; i < 100; i++)
        {
            var m = Mavyex.UI.Helpers.DiffMap(a, b);
            if (!Mavyex.Services.ContentHash.AnyPending) return Mavyex.UI.Helpers.DiffMap(a, b);
            Thread.Sleep(50);
        }
        throw new TimeoutException("hashing did not finish");
    }

    [Fact]
    public void SameSizeDifferentDateIsComparedByContentAgainstAnArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mavyex-cmp-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        try
        {
            var zip = Path.Combine(root, "remote.zip");
            using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var (name, text) in new[] { ("same.txt", "hello world"), ("changed.txt", "aaaa bbbb") })
                {
                    var en = z.CreateEntry(name);
                    en.LastWriteTime = new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);
                    using var w = new StreamWriter(en.Open());
                    w.Write(text);
                }
            }
            File.WriteAllText(Path.Combine(local, "same.txt"), "hello world");     // identical bytes, newer date
            File.WriteAllText(Path.Combine(local, "changed.txt"), "aaaa cccc");    // same size, different bytes
            var a = new Tab(local, false);
            var b = new Tab(zip, false);
            var m = Settled(a, b);
            Assert.False(m.ContainsKey("same.txt"));                 // the date alone no longer marks it
            Assert.Equal(Mavyex.UI.Diff.Newer, m["changed.txt"]);   // content differs: direction by date
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

public class TextTests
{
    [Fact]
    public void NaturalOrder()
    {
        var v = new List<string> { "file10", "File2", "file1", "a" };
        v.Sort(Text.Natural);
        Assert.Equal(["a", "file1", "File2", "file10"], v);
    }

    [Fact]
    public void Sizes()
    {
        Assert.Equal("512 B", Text.FmtSize(512));
        Assert.Equal("1.5 KB", Text.FmtSize(1536));
    }

    [Fact]
    public void PaletteFuzzy()
    {
        Assert.NotNull(Text.Fuzzy("cp", "Copy path"));
        Assert.Null(Text.Fuzzy("xyz", "Copy path"));
        Assert.True(Text.Fuzzy("copy", "Copy to other pane") > Text.Fuzzy("copy", "Close other tabs pane copy"));
    }
}

public class StoreTests
{
    [Fact]
    public void ReadsTheRustStateJson()
    {
        const string json = """
        {"aliases":{"root@1.2.3.4:22":"prod"},"editor":"code {file}","editor_rules":"","diff_dark":null,
         "view":"Grid","cols":{"ext":true,"size":true,"date":false},
         "window":{"x":10.0,"y":20.0,"w":1300.0,"h":800.0,"maximized":true},"jobs_win":null,"theme":"Dark",
         "search":{"mode":"Fuzzy","match_case":false,"deep":true,"content":false},"exclude":"node_modules",
         "favorites":["C:\\x"],"visits":{"C:\\x":{"count":3,"last":100},"c:\\X":{"count":1,"last":200}},
         "panes":[{"tabs":[{"path":"C:\\x","sort":"Modified","asc":false}],"active":0}],"active_pane":1,"show_hidden":true}
        """;
        var s = JsonSerializer.Deserialize<Store>(json, Store.Json)!;
        Assert.Equal("prod", s.Aliases["root@1.2.3.4:22"]);
        Assert.Equal(ViewMode.Grid, s.View);
        Assert.True(s.Cols.Ext);
        Assert.False(s.Cols.Date);
        Assert.True(s.Window!.Maximized);
        Assert.Equal(ThemeChoice.Dark, s.Theme);
        Assert.Equal(SearchMode.Fuzzy, s.Search.Mode);
        Assert.True(s.Search.Deep);
        Assert.Equal(SortKey.Modified, s.Panes[0].Tabs[0].Sort);
        Assert.False(s.Panes[0].Tabs[0].Asc);
        Assert.Equal(1, s.ActivePane);
        var back = JsonSerializer.Serialize(s, Store.Json);
        Assert.Contains("\"editor_rules\"", back);
        Assert.Contains("\"match_case\"", back);
        Assert.Contains("\"view\": \"Grid\"", back);
    }

    [Fact]
    public void FavoritesAndVisits()
    {
        var s = new Store();
        Assert.True(s.ToggleFavorite(@"C:\a"));
        Assert.True(s.IsFavorite(@"c:\A"));
        Assert.False(s.ToggleFavorite(@"C:\A"));
        s.RecordVisit(@"C:\a\");
        s.RecordVisit(@"c:\A");
        Assert.Single(s.Visits);
        Assert.Equal(2u, s.Visits.Values.First().Count);
        s.RecordVisit("D:");
        Assert.Contains(@"D:\", s.Visits.Keys);
    }
}
