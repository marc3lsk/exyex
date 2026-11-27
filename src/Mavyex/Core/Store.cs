using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mavyex.Core;

public enum SortKey { Name, Ext, Size, Modified }

public enum SearchMode { Smart, Fuzzy, Wildcard, Regex }

/// <summary>Search options (search.rs SearchOpts). A record struct: value equality (used in cache keys).</summary>
public record struct SearchOpts
{
    public SearchMode Mode { get; set; }
    public bool MatchCase { get; set; }
    /// <summary>Also search inside subfolders (background walk).</summary>
    public bool Deep { get; set; }
    /// <summary>Search the contents of files (implies subfolders).</summary>
    public bool Content { get; set; }
}

public enum ThemeChoice { System, Light, Dark }

public enum ViewMode { Details, Compact, Grid }

public sealed class Visit
{
    public uint Count { get; set; }
    public long Last { get; set; }
}

public sealed class TabSession
{
    public string Path { get; set; } = "";
    public SortKey Sort { get; set; }
    public bool Asc { get; set; } = true;
}

public sealed class PaneSession
{
    public List<TabSession> Tabs { get; set; } = [];
    public int Active { get; set; }
}

/// <summary>Which detail columns are shown (the name column is always there).</summary>
public sealed class Cols
{
    public bool Ext { get; set; }
    public bool Size { get; set; } = true;
    public bool Date { get; set; } = true;
}

/// <summary>Remembered window placement (outer position, inner size), in device-independent pixels.</summary>
public sealed class WinGeom
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public bool Maximized { get; set; }

    /// <summary>The title bar must still be reachable (monitors can disappear).</summary>
    public bool OnScreen()
    {
        double vx = System.Windows.SystemParameters.VirtualScreenLeft, vy = System.Windows.SystemParameters.VirtualScreenTop;
        double vw = System.Windows.SystemParameters.VirtualScreenWidth, vh = System.Windows.SystemParameters.VirtualScreenHeight;
        double cx = X + W / 2, cy = Y + 16;
        return W >= 200 && H >= 120 && cx >= vx && cx <= vx + vw && cy >= vy && cy <= vy + vh;
    }
}

/// <summary>
/// Persistent state <c>%APPDATA%\mavyex\state.json</c> – favorites, frecency visits, session, settings.
/// JSON is compatible with the Rust version (snake_case fields, enum variant names).
/// </summary>
public sealed class Store
{
    /// <summary>SFTP connection aliases: user@host:port -> short name.</summary>
    public Dictionary<string, string> Aliases { get; set; } = [];
    /// <summary>F4 editor command (empty = Notepad); see Editor.</summary>
    public string Editor { get; set; } = "";
    /// <summary>Per-extension editor rules, one "ext1,ext2 = command" per line.</summary>
    public string EditorRules { get; set; } = "";
    /// <summary>Theme of the text-compare window (null = same as the main window).</summary>
    public bool? DiffDark { get; set; }
    public ViewMode View { get; set; }
    public Cols Cols { get; set; } = new();
    public WinGeom? Window { get; set; }
    public WinGeom? JobsWin { get; set; }
    public ThemeChoice Theme { get; set; }
    public SearchOpts Search { get; set; }
    /// <summary>Manual exclude list for subfolder search (names / globs, comma-separated).</summary>
    public string Exclude { get; set; } = "";
    public List<string> Favorites { get; set; } = [];
    public Dictionary<string, Visit> Visits { get; set; } = [];
    public List<PaneSession> Panes { get; set; } = [];
    public int ActivePane { get; set; }
    public bool ShowHidden { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>%APPDATA%\mavyex (override with MAVYEX_HOME for tests); a folder left by the former name (exyex) is moved there once.</summary>
    public static string ConfigDir()
    {
        var home = Environment.GetEnvironmentVariable("MAVYEX_HOME");
        if (!string.IsNullOrEmpty(home)) return home;
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(baseDir, "mavyex"), old = Path.Combine(baseDir, "exyex");
        if (!Directory.Exists(dir) && Directory.Exists(old))
        {
            try { Directory.Move(old, dir); } catch { }
        }
        return dir;
    }

    private static string FilePath() => Path.Combine(ConfigDir(), "state.json");

    public static Store Load()
    {
        try
        {
            var st = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath()), Json) ?? new Store();
            st.MergeCaseDuplicates();
            return st;
        }
        catch
        {
            return new Store();
        }
    }

    /// <summary>Folds visit entries that differ only by letter case (c:\x vs C:\x) into one.</summary>
    private void MergeCaseDuplicates()
    {
        var merged = new Dictionary<string, (string Key, Visit V)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Visits)
        {
            if (!merged.TryGetValue(k, out var e)) e = (k, new Visit());
            if (v.Last >= e.V.Last) e.Key = k; // keep the spelling used most recently
            e.V.Count += v.Count;
            e.V.Last = Math.Max(e.V.Last, v.Last);
            merged[k] = e;
        }
        Visits = merged.Values.ToDictionary(x => x.Key, x => x.V);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir());
            var tmp = Path.ChangeExtension(FilePath(), "tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath(), overwrite: true);
        }
        catch { }
    }

    public void RecordVisit(string p)
    {
        var key = p.TrimEnd('\\');
        if (key.EndsWith(':')) key += "\\";
        // Windows paths are case-insensitive: reuse an existing entry that differs only by case
        var existing = Visits.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (!Visits.TryGetValue(existing ?? key, out var v)) Visits[existing ?? key] = v = new Visit();
        v.Count++;
        v.Last = Now();
    }

    /// <summary>Folders ranked by how often they were opened (ties: most recent first).</summary>
    public List<(string Path, double Score)> Frecent() =>
        Visits.OrderByDescending(kv => kv.Value.Count).ThenByDescending(kv => kv.Value.Last)
              .Select(kv => (kv.Key, (double)kv.Value.Count)).ToList();

    public bool RemoveFavorite(string p) => Favorites.RemoveAll(f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Forgets a folder's visit history so it no longer shows up as "recent".</summary>
    public bool ForgetVisit(string p)
    {
        var keys = Visits.Keys.Where(k => string.Equals(k, p, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var k in keys) Visits.Remove(k);
        return keys.Count > 0;
    }

    public bool IsFavorite(string p) => Favorites.Any(f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns true when it is a favorite now.</summary>
    public bool ToggleFavorite(string p)
    {
        int i = Favorites.FindIndex(f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            Favorites.RemoveAt(i);
            return false;
        }
        Favorites.Add(p);
        return true;
    }
}
