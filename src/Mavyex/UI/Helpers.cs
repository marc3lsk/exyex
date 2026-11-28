using System.Collections.Concurrent;
using System.Windows.Media;
using Mavyex.Core;
using Mavyex.Services;

namespace Mavyex.UI;

/// <summary>Small helpers from app.rs (names, volumes, drives, compare marks).</summary>
public static class Helpers
{
    /// <summary>Case-insensitive, separator-normalized key for comparing folder paths.</summary>
    public static string PathKey(string p)
    {
        var s = p.ToLowerInvariant();
        if (s.StartsWith(@"\\?\")) s = s[4..];
        return s.TrimEnd('\\');
    }

    public static List<string> ListDrives()
    {
        try
        {
            return DriveInfo.GetDrives().Select(d => d.Name).ToList(); // "C:\" …
        }
        catch
        {
            return [@"C:\"];
        }
    }

    /// <summary>Length of the name without its last extension (to preselect the stem when renaming).</summary>
    public static int StemLen(string name)
    {
        int i = name.LastIndexOf('.');
        return i > 0 ? i : name.Length;
    }

    public static string? ValidateName(string name)
    {
        if (name.Length == 0) return "Name can't be empty";
        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0) return "Name can't contain \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "Name can't end with a dot or space";
        return null;
    }

    public static bool SameVolume(string a, string b)
    {
        var ca = VPath.Components(a);
        var cb = VPath.Components(b);
        return ca.Count > 0 && cb.Count > 0 && string.Equals(ca[0].TrimEnd('\\'), cb[0].TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    public static string WithTrailingSep(string p) => p.TrimEnd('\\') + "\\";

    public static string FmtDur(double secs)
    {
        long s = (long)Math.Round(secs);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    // ------------------------------------------------------------------ compare panes

    public static (Color Color, string Tip) Style(this Diff d, bool dark) => (d, dark) switch
    {
        (Diff.OnlyHere, true) => (Color.FromRgb(110, 205, 120), "only in this pane"),
        (Diff.OnlyHere, false) => (Color.FromRgb(30, 135, 50), "only in this pane"),
        (Diff.Newer, true) => (Color.FromRgb(255, 170, 60), "newer than in the other pane"),
        (Diff.Newer, false) => (Color.FromRgb(200, 110, 0), "newer than in the other pane"),
        (Diff.Older, true) => (Color.FromRgb(235, 110, 110), "older than in the other pane"),
        (Diff.Older, false) => (Color.FromRgb(190, 50, 50), "older than in the other pane"),
        (Diff.Size, true) => (Color.FromRgb(190, 140, 255), "different size"),
        _ => (Color.FromRgb(120, 60, 200), "different size"),
    };

    public static bool IsLocal(string p) => !Sftp.IsRemote(p) && Archive.Split(p) is null;

    private sealed record ContentKey(string A, string B, DateTime? Ma, DateTime? Mb);
    /// <summary>Results of byte-for-byte comparisons: null = still running.</summary>
    private static readonly ConcurrentDictionary<ContentKey, bool?> content = new();

    private static bool BytesEqual(string a, string b)
    {
        try
        {
            using var fa = File.OpenRead(a);
            using var fb = File.OpenRead(b);
            if (fa.Length != fb.Length) return false;
            var ba = new byte[64 * 1024];
            var bb = new byte[64 * 1024];
            long left = fa.Length;
            while (left > 0)
            {
                int n = (int)Math.Min(left, ba.Length);
                fa.ReadExactly(ba, 0, n);
                fb.ReadExactly(bb, 0, n);
                if (!ba.AsSpan(0, n).SequenceEqual(bb.AsSpan(0, n))) return false;
                left -= n;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether two local files of equal size have identical bytes; computed in the background, null while
    /// pending (or for non-local files, which are never read here).</summary>
    private static bool? SameContent(Entry a, Entry b)
    {
        if (a.IsDir || b.IsDir || !IsLocal(a.Path) || !IsLocal(b.Path)) return null;
        var key = new ContentKey(a.Path, b.Path, a.Modified, b.Modified);
        if (content.TryGetValue(key, out var r)) return r;
        if (content.TryAdd(key, null))
            Task.Run(() => content[key] = BytesEqual(key.A, key.B));
        return null;
    }

    public static bool SameContentPending() => content.Values.Any(v => v == null) || ContentHash.AnyPending;

    /// <summary>true = contents differ, false = identical or still being checked (not marked meanwhile),
    /// null = can't tell (hash failed, e.g. no sha256sum on the server): the caller falls back to the date.</summary>
    private static bool? ContentDiffers(Entry a, Entry b)
    {
        if (IsLocal(a.Path) && IsLocal(b.Path)) return SameContent(a, b) == false;
        var sa = ContentHash.Get(a, out var ha);
        var sb = ContentHash.Get(b, out var hb);
        if (sa == ContentHash.State.Failed || sb == ContentHash.State.Failed) return null;
        if (sa == ContentHash.State.Pending || sb == ContentHash.State.Pending) return false;
        return ha != hb;
    }

    /// <summary>How the items of a differ from the folder shown in b (by name; same items are left out).</summary>
    public static Dictionary<string, Diff> DiffMap(Tab a, Tab b)
    {
        var other = new Dictionary<string, Entry>();
        foreach (var e in b.Entries.Where(e => !e.IsUp && !e.Deep)) other.TryAdd(e.Name.ToLowerInvariant(), e);
        var outMap = new Dictionary<string, Diff>();
        foreach (var e in a.Entries.Where(e => !e.IsUp && !e.Deep))
        {
            var key = e.Name.ToLowerInvariant();
            Diff? d;
            if (!other.TryGetValue(key, out var o)) d = Diff.OnlyHere;
            else if (o.IsDir || e.IsDir) d = null;
            else
            {
                var slack = TimeSpan.FromSeconds(2);
                // Same size but different dates: the date alone does not count – only a real content difference does
                // (local pair: byte compare; SFTP / archive: SHA-256, remote side computed by sha256sum on the server).
                // Not marked while that check still runs; if a hash can't be computed, the date rule is used.
                bool Differs(bool newer) => e.Size == o.Size ? ContentDiffers(e, o) ?? newer : newer;
                if (e.Modified is { } x && o.Modified is { } y && x > y + slack) d = Differs(true) ? Diff.Newer : null;
                else if (e.Modified is { } x2 && o.Modified is { } y2 && y2 > x2 + slack) d = Differs(true) ? Diff.Older : null;
                else if (e.Size != o.Size) d = Diff.Size;
                else d = null;
            }
            if (d is { } dd) outMap[key] = dd;
        }
        return outMap;
    }
}
