using System.IO;
using Mavyex.Services;
using Xunit;

namespace Mavyex.Tests;

public class EditorTests
{
    [Fact]
    public void PicksRuleByExtensionThenDefault()
    {
        var rules = "# comment\nrs, toml = code {file}\n*.md = typora\n.log = \"C:\\Tools\\log viewer.exe\" {file}";
        Assert.Equal("code {file}", Editor.CommandFor("", rules, "a.RS"));
        Assert.Equal("\"C:\\Tools\\log viewer.exe\" {file}", Editor.CommandFor("", rules, "x.log"));
        Assert.Equal("vim", Editor.CommandFor("vim", rules, "a.txt"));
        Assert.Equal(Editor.Default, Editor.CommandFor("  ", rules, "a.txt"));
    }

    [Fact]
    public void RulesDetails()
    {
        // first match wins, * matches anything, CRLF line ends, rules without '=' or command are skipped
        var rules = "md = \r\nbroken line\r\n*;txt = first\r\ntxt = second";
        Assert.Equal("first", Editor.CommandFor("", rules, @"C:\x\a.txt"));
        Assert.Equal("first", Editor.CommandFor("", rules, @"C:\x\a.md"));
        Assert.Equal("first", Editor.CommandFor("", rules, @"C:\x\noext"));
        Assert.Equal("ed", Editor.CommandFor(" ed ", "", @"C:\.bashrc"));
    }

    [Fact]
    public void BuildsArgumentsWithPlaceholders()
    {
        var f = "C:\\my dir\\a b.txt";
        Assert.Equal(["C:\\Program Files\\N\\n.exe", "-n1", "C:\\my dir\\a b.txt"], Editor.Build("\"C:\\Program Files\\N\\n.exe\" -n{line} {file}", f));
        Assert.Equal(["code", "C:\\my dir\\a b.txt"], Editor.Build("code", f)); // file appended
        Assert.Equal("--dir=C:\\my dir", Editor.Build("ed --dir={dir} {file}", f)[1]);
        Assert.Equal(["a", "", "b"], Editor.SplitArgs("a \"\" b"));
        Assert.Empty(Editor.SplitArgs("   "));
        Assert.Equal(["a b", "c"], Editor.SplitArgs("a\" \"b  c"));
    }

    [Fact]
    public void ResolvesProgramsOnPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mavyex-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "myed.cmd"), "@echo off");
            File.WriteAllText(Path.Combine(dir, "bare"), ""); // no extension: ignored like in Rust
            var path = $"C:\\nonexistent-mavyex;;{dir}";
            Assert.Equal(Path.Combine(dir, "myed.cmd"), Editor.ResolveProgram("myed", path));
            Assert.Equal("bare", Editor.ResolveProgram("bare", path));
            Assert.Equal("nothing-here", Editor.ResolveProgram("nothing-here", path));
            // with a directory the PATH is not searched, extensions are still tried
            Assert.Equal(Path.Combine(dir, "myed") + ".cmd", Editor.ResolveProgram(Path.Combine(dir, "myed"), ""));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void QuotesForCmd()
    {
        Assert.Equal("plain", Editor.QuoteForCmd("plain"));
        Assert.Equal("\"C:\\a b\\c.txt\"", Editor.QuoteForCmd("C:\\a b\\c.txt"));
        Assert.Equal("\"a&b\"", Editor.QuoteForCmd("a&b"));
        Assert.Equal("\"\"", Editor.QuoteForCmd(""));
    }

    [Fact]
    public void LaunchReportsMissingProgram()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Editor.Launch("mavyex-no-such-editor-xyz {file}", @"C:\nope\a.txt"));
        Assert.StartsWith("could not start \"mavyex-no-such-editor-xyz\": ", e.Message);
    }
}

public class PropsTests
{
    [Fact]
    public void BareDriveGetsBackslash()
    {
        Assert.Equal(@"C:\", Props.WidePath("C:"));
        Assert.Equal(@"C:\a", Props.WidePath(@"C:\a"));
    }

    [Fact]
    public void RemotePathsAreRejected()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Props.Show([@"C:\", @"sftp:\u@h\~\x"]));
        Assert.Equal("Properties work on local files only", e.Message);
        Props.Show([]); // nothing to show, no dialog
    }
}
