namespace Mavyex.Core;

/// <summary>One row of a folder listing (local, archive-inner or SFTP). Port of model.rs `Entry`.</summary>
public sealed class Entry
{
    public string Name { get; set; } = "";
    /// <summary>Full (possibly virtual) path: <c>C:\a\b</c>, <c>C:\x.zip\inner\file</c>, <c>sftp:\user@host\~\dir</c>.</summary>
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public long Size { get; set; }
    /// <summary>Last write time in UTC (null = unknown).</summary>
    public DateTime? Modified { get; set; }
    public bool Hidden { get; set; }
    /// <summary>The virtual ".." row that goes to the parent folder.</summary>
    public bool IsUp { get; set; }
    /// <summary>A result of the subfolder search (<see cref="Name"/> is then the relative path).</summary>
    public bool Deep { get; set; }
    /// <summary>Content search: first matching line ("12: text  (+3 more)").</summary>
    public string Snippet { get; set; } = "";
    /// <summary>Lower-case extension without dot ("" for folders).</summary>
    public string Ext { get; set; } = "";

    public Entry Clone() => (Entry)MemberwiseClone();

    public static string ExtOf(string name, bool isDir)
    {
        if (isDir) return "";
        var e = System.IO.Path.GetExtension(name);
        return string.IsNullOrEmpty(e) ? "" : e[1..].ToLowerInvariant();
    }

    public override string ToString() => Path;
}
