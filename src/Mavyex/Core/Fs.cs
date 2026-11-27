using Mavyex.Services;

namespace Mavyex.Core;

/// <summary>Folder listing dispatch (model.rs read_dir / with_up / is_dir_path / dir_mtime): local, archive-inner, SFTP.</summary>
public static class Fs
{
    /// <summary>Lists a folder. Throws (IOException / UnauthorizedAccessException …) like Rust's io::Result.</summary>
    public static List<Entry> ReadDir(string p)
    {
        if (Sftp.IsRemote(p)) return Sftp.List(p);
        if (Archive.Split(p) is { } a) return Archive.List(a.Zip, a.Inner);
        var dir = new DirectoryInfo(p.EndsWith(':') ? p + "\\" : p);
        if (!dir.Exists) throw new DirectoryNotFoundException($"The system cannot find the path specified.");
        var outList = new List<Entry>();
        foreach (var fi in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            try
            {
                bool isDir = fi is DirectoryInfo;
                // a symlink / junction counts as what it points to (Rust: path.is_dir())
                if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint) && fi is FileInfo) isDir = Directory.Exists(fi.FullName);
                var hidden = (fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                long size = 0;
                if (!isDir && fi is FileInfo f)
                {
                    try { size = f.Length; } catch { }
                }
                DateTime? mod = null;
                try { mod = fi.LastWriteTimeUtc; } catch { }
                outList.Add(new Entry
                {
                    Name = fi.Name,
                    Path = fi.FullName,
                    IsDir = isDir,
                    Size = isDir ? 0 : size,
                    Modified = mod,
                    Hidden = hidden,
                    Ext = Entry.ExtOf(fi.Name, isDir),
                });
            }
            catch (IOException) { }
        }
        return outList;
    }

    /// <summary>Appends the virtual ".." entry (if the folder has a parent; "sftp:" has none).</summary>
    public static List<Entry> WithUp(List<Entry> v, string path)
    {
        if (Sftp.IsRoot(path)) return v;
        if (VPath.Parent(path) is { } parent)
            v.Add(new Entry { Name = "..", Path = parent, IsDir = true, IsUp = true });
        return v;
    }

    /// <summary>A folder we can navigate to. Remote folders are not probed here (that would connect).</summary>
    public static bool IsDirPath(string p) => Sftp.IsRemote(p) || Directory.Exists(p) || (Archive.Split(p) is not null);

    public static DateTime? DirMtime(string p)
    {
        try
        {
            if (Sftp.IsRemote(p) || Archive.SplitInner(p) is not null) return null;
            return Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p) : null;
        }
        catch
        {
            return null;
        }
    }
}
