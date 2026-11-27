using System.IO;
using System.Text.Json;
using Mavyex.Core;
using Mavyex.Services;
using Xunit;

namespace Mavyex.Tests;

// All tests touching the static alias table live in this one class, so xunit runs them sequentially.
public class SftpTests
{
    [Fact]
    public void UrlAndPathRoundTrip()
    {
        var p = Sftp.FromUrl("sftp://bob@example.com:2222/var/www/")!;
        Assert.Equal(@"sftp:\bob@example.com:2222\var\www", p);
        var l = Sftp.Parse(p)!;
        Assert.Equal(("bob", "example.com", 2222), (l.User, l.Host, l.Port));
        Assert.Equal("/var/www", Sftp.RemotePath(l, "/home/bob"));
        var home = Sftp.FromUrl("ssh://bob@example.com")!;
        l = Sftp.Parse(home)!;
        Assert.Equal(22, l.Port);
        Assert.Equal("/home/bob", Sftp.RemotePath(l, "/home/bob"));
        var sub = Sftp.Parse(@"sftp:\bob@h\~\docs\a b.txt")!;
        Assert.Equal("/home/bob/docs/a b.txt", Sftp.RemotePath(sub, "/home/bob/"));
        Assert.True(Sftp.IsRemote(@"SFTP:\x@y"));
        Assert.False(Sftp.IsRemote(@"C:\sftp"));
        Assert.True(Sftp.IsRoot("sftp:"));
        Assert.Null(Sftp.FromUrl("sftp://nouser/x"));
        Assert.Equal(["bob@h", "a", "b"], Sftp.Parts("sftp:/bob@h/a/b")!);
    }

    [Fact]
    public void AliasesResolveInUrlsAndTitles()
    {
        Sftp.SetAliases(new() { ["root@10.0.0.5:22"] = "prod", ["me@box:2222"] = "box" });
        try
        {
            var p = Sftp.FromUrl("sftp://PROD/var/log");
            Assert.Equal(@"sftp:\root@10.0.0.5\var\log", p);
            Assert.Equal("prod", Sftp.Title(@"sftp:\root@10.0.0.5"));
            Assert.Equal("prod", Sftp.Title(@"sftp:\root@10.0.0.5\~"));
            Assert.Null(Sftp.Title(@"sftp:\root@10.0.0.5\var"));
            Assert.Equal(@"prod:\var\log", Sftp.Pretty(p!));
            Assert.Null(Sftp.FromUrl("sftp://nobody/x"));
            // a non-default port stays in the target
            Assert.Equal(@"sftp:\me@box:2222\~", Sftp.FromUrl("ssh://box"));
            Assert.Equal("prod (root@10.0.0.5)", Sftp.CrumbLabel(@"sftp:\root@10.0.0.5\etc"));
            Assert.Equal("box (me@box:2222)", Sftp.CrumbLabel(@"sftp:\me@box:2222"));
            Assert.Equal("prod", Sftp.AliasOf(Sftp.Parse(@"sftp:\root@10.0.0.5:22\x")!));
        }
        finally
        {
            Sftp.SetAliases([]);
        }
        Assert.Equal(@"root@10.0.0.5:\var", Sftp.Pretty(@"sftp:\root@10.0.0.5\var"));
        Assert.Equal("root@10.0.0.5", Sftp.Title(@"sftp:\root@10.0.0.5"));
        Assert.Equal("u@h:2200", Sftp.CrumbLabel(@"sftp:\u@h:2200\a"));
    }

    [Theory]
    [InlineData(@"sftp:\u@h", "u", "h", 22, 0)]
    [InlineData(@"sftp:\u@h:2222\a\b", "u", "h", 2222, 2)]
    [InlineData(@"sftp:\u@h:\a", "u", "h:", 22, 1)]          // empty port = part of the host
    [InlineData(@"sftp:\u@h:x1", "u", "h:x1", 22, 0)]        // non-numeric "port" too
    [InlineData(@"sftp:\a@b@c", "a", "b@c", 22, 0)]          // first '@' splits
    [InlineData(@"sftp:\@h", "", "h", 22, 0)]
    public void ParseVariants(string p, string user, string host, int port, int rest)
    {
        var l = Sftp.Parse(p)!;
        Assert.Equal((user, host, port, rest), (l.User, l.Host, l.Port, l.Rest.Count));
    }

    [Theory]
    [InlineData("sftp:")]
    [InlineData(@"sftp:\nouser")]
    [InlineData(@"sftp:\u@h:70000")] // port out of u16 range
    [InlineData(@"C:\x")]
    public void ParseRejects(string p) => Assert.Null(Sftp.Parse(p));

    [Fact]
    public void FromUrlEdgeCases()
    {
        Assert.Null(Sftp.FromUrl("http://u@h/x"));
        Assert.Null(Sftp.FromUrl("sftp://@h/x"));
        Assert.Null(Sftp.FromUrl("sftp://u@/x"));
        Assert.Equal(@"sftp:\u@h\~", Sftp.FromUrl("SFTP://u@h/"));
        Assert.Equal(@"sftp:\u@h\a\b", Sftp.FromUrl("sftp://u@h//a//b/"));
    }

    [Fact]
    public void RootsKeysAndRemotePaths()
    {
        Assert.Equal(@"sftp:\u@h", Sftp.RootOf(@"sftp:\u@h:22\~\x"));
        Assert.Equal(@"sftp:\u@h:2022", Sftp.RootOf(@"sftp:\u@h:2022\x"));
        Assert.Null(Sftp.RootOf("sftp:"));
        Assert.Equal("u@h:22", Sftp.ConnKey(@"sftp:\u@h\x"));
        Assert.Equal("u@h:22", Sftp.Parse(@"sftp:\u@h")!.Key);
        Assert.Equal("/", Sftp.RemotePath(Sftp.Parse(@"sftp:\u@h")!, "/home/u"));
        Assert.Equal("/", Sftp.RemotePath(Sftp.Parse(@"sftp:\u@h\~")!, "/"));      // home "/" trims to "" -> "/"
        Assert.Equal("/etc/x", Sftp.RemotePath(Sftp.Parse(@"sftp:\u@h\etc\x")!, "/home/u"));
        Assert.Equal("/x/~", Sftp.RemotePath(Sftp.Parse(@"sftp:\u@h\x\~")!, "/home/u")); // ~ only special first
        Assert.Equal("/a/b", Sftp.JoinRemote("/a/", "b"));
        Assert.Equal("/b", Sftp.JoinRemote("/", "b"));
        Assert.True(Sftp.IsDir("sftp:"));
        Assert.False(Sftp.Exists("sftp:"));
        Assert.False(Sftp.Exists(@"C:\nope"));
        Assert.Equal(0, Sftp.SizeOf(@"C:\nope"));
    }

    [Fact]
    public void KnownHostsTrustOnFirstUse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mavyex-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "known_hosts.json");
        try
        {
            const string fp = "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo+6/g";
            Assert.Null(Sftp.CheckHostKey(file, "h:22", fp));
            var text = File.ReadAllText(file);
            Assert.Contains("\"h:22\": \"" + fp + "\"", text); // no \u002B escapes, like serde_json
            Assert.StartsWith("{\n  \"", text.Replace("\r\n", "\n"));
            Assert.Null(Sftp.CheckHostKey(file, "h:22", fp));
            Assert.Null(Sftp.CheckHostKey(file, "other:2222", "SHA256:abc"));
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
            Assert.Equal(2, map.Count);
            var err = Sftp.CheckHostKey(file, "h:22", "SHA256:changed");
            Assert.Equal(
                $"HOST KEY CHANGED for h:22!\nsaved: {fp}\nnow:   SHA256:changed\nSomeone may be intercepting the connection. If the server was reinstalled, remove the entry from {file}",
                err);
            Assert.Equal(fp, JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!["h:22"]); // not replaced
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void KnownHostsReadsRustFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "mavyex-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, "{\n  \"srv:22\": \"SHA256:abc/def+ghi\"\n}");
            Assert.Null(Sftp.CheckHostKey(file, "srv:22", "SHA256:abc/def+ghi"));
            Assert.NotNull(Sftp.CheckHostKey(file, "srv:22", "SHA256:other"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ChownValidationAndCommand()
    {
        Assert.True(Sftp.ValidId("www-data"));
        Assert.True(Sftp.ValidId("1000"));
        Assert.True(Sftp.ValidId("user@DOMAIN.x$"));
        Assert.False(Sftp.ValidId("-R"));
        Assert.False(Sftp.ValidId("a b"));
        Assert.False(Sftp.ValidId("a;rm"));
        Assert.False(Sftp.ValidId(new string('a', 65)));
        Assert.Equal("'it'\\''s'", Sftp.ShQuote("it's"));

        string Err(IReadOnlyList<string> paths, string o, string g) =>
            Assert.Throws<IOException>(() => Sftp.ChownPrepare(paths, o, g)).Message;
        Assert.Equal("enter an owner and/or a group", Err([@"sftp:\u@h\x"], " ", ""));
        Assert.Equal("owner / group may contain only letters, digits and _ - . $ @", Err([@"sftp:\u@h\x"], "a b", ""));
        Assert.Equal("nothing selected", Err([], "root", ""));
        Assert.Equal("select items on one server only", Err([@"sftp:\u@h\x", @"sftp:\u@h2\y"], "root", ""));
        Assert.Equal("select a folder or file inside the server, not the connection itself", Err([@"sftp:\u@h"], "root", ""));
        Assert.StartsWith("not a valid SFTP location", Err([@"C:\x"], "root", ""));

        var (spec, locs) = Sftp.ChownPrepare([@"sftp:\u@h\~\a b", @"sftp:\u@h:22\etc\it's"], " www ", " web ");
        Assert.Equal("www:web", spec);
        Assert.Equal("www", Sftp.ChownPrepare([@"sftp:\u@h\x"], "www", "").Spec);
        Assert.Equal(":web", Sftp.ChownPrepare([@"sftp:\u@h\x"], "", "web").Spec);
        var cmd = Sftp.ChownCommand(spec, locs.Select(l => Sftp.RemotePath(l, "/home/u")), recursive: true, sudo: true);
        Assert.Equal("sudo -n chown -h -R -- 'www:web' '/home/u/a b' '/etc/it'\\''s'", cmd);
        Assert.Equal("chown -h -- 'x' '/a'", Sftp.ChownCommand("x", ["/a"], false, false));
    }

    [Fact]
    public void UniqueRemoteNames()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { @"sftp:\u@h\d\a.txt", @"sftp:\u@h\d\a (2).txt", @"sftp:\u@h\d\.bashrc", @"sftp:\u@h\d\x" };
        Assert.Equal(@"sftp:\u@h\d\a (3).txt", Sftp.UniqueRemote(@"sftp:\u@h\d", "a.txt", taken.Contains));
        Assert.Equal(@"sftp:\u@h\d\b.txt", Sftp.UniqueRemote(@"sftp:\u@h\d", "b.txt", taken.Contains));
        Assert.Equal(@"sftp:\u@h\d\.bashrc (2)", Sftp.UniqueRemote(@"sftp:\u@h\d", ".bashrc", taken.Contains)); // empty stem
        Assert.Equal(@"sftp:\u@h\d\x (2)", Sftp.UniqueRemote(@"sftp:\u@h\d", "x", taken.Contains));
        Assert.Equal(@"sftp:\u@h\d\a.txt", Sftp.UniqueRemote(@"sftp:\u@h\d", "a.txt", _ => true)); // all taken -> first
    }

    [Fact]
    public void InvalidLocationsThrowIo()
    {
        Assert.Throws<IOException>(() => Sftp.Mkdir("sftp:"));
        Assert.Throws<IOException>(() => Sftp.RemoveAll(@"sftp:\u@h"));
        Assert.Equal("refusing to delete the remote home / root folder", Assert.Throws<IOException>(() => Sftp.RemoveAll(@"sftp:\u@h\~")).Message);
        Assert.Equal("can't rename across servers", Assert.Throws<IOException>(() => Sftp.Rename(@"sftp:\u@a\x", @"sftp:\u@b\x")).Message);
    }

    [Fact]
    public void RootListsKnownConnections()
    {
        try
        {
            var st = new Store();
            st.Favorites.Add(@"sftp:\bob@srv\~\docs");
            st.Favorites.Add(@"C:\local");
            st.Visits[@"sftp:\bob@srv:22\etc"] = new Visit();
            st.Visits[@"sftp:\ann@other:2022\x"] = new Visit();
            Sftp.SetAliases(new() { ["ann@other:2022"] = "dev" });
            var items = Sftp.RootEntries(st);
            Assert.Equal(2, items.Count);
            Assert.Equal(("bob@srv", @"sftp:\bob@srv", true), (items[0].Name, items[0].Path, items[0].IsDir));
            Assert.Equal(("dev  —  ann@other", @"sftp:\ann@other:2022"), (items[1].Name, items[1].Path));
        }
        finally
        {
            Sftp.SetAliases([]);
        }
    }
}
