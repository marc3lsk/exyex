using Mavyex.Services;

namespace Mavyex.Core;

/// <summary>Result of polling a pending navigation.</summary>
public readonly record struct LoadPoll(bool Pending, string? Err, string? Arrived);

/// <summary>
/// One tab: folder content, filtered/sorted view, cursor, selection (by path), history, search state.
/// Port of model.rs Tab. <see cref="Entries"/> = raw content (+ virtual ".." row); <see cref="View"/> = indices after
/// filter/sort. Always call <see cref="RebuildView"/> after changing Filter / Search / Sort / Entries.
/// </summary>
public sealed class Tab
{
    public const double RowH = 22;

    private enum NavKind { Go, Back, Forward }

    private sealed class Loading
    {
        public required string To;
        public required NavKind Kind;
        public string? Focus;
        public required Task<(List<Entry>? Entries, string? Err)> Task;
        public DateTime Since = DateTime.Now;
    }

    public string Path { get; private set; }
    public List<string> Back { get; } = [];
    public List<string> Fwd { get; } = [];
    public List<Entry> Entries { get; private set; } = [];
    public List<int> View { get; private set; } = [];
    public int Cursor { get; set; }
    public int Anchor { get; set; }
    public HashSet<string> Selected { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Filter { get; set; } = "";
    public bool FilterOpen { get; set; }
    public SearchOpts Search { get; set; }
    public Matcher Matcher { get; private set; } = Matcher.All;
    public string Exclude { get; set; } = "";
    public List<Entry> DeepRes { get; } = [];
    public DeepJob? DeepJob { get; private set; }
    private string deepKey = "";
    /// <summary>Renamed results that stay listed although they no longer match the search.</summary>
    private readonly HashSet<string> kept = new(StringComparer.OrdinalIgnoreCase);
    private string keptKey = "";
    public SortKey Sort { get; set; } = SortKey.Name;
    public bool Asc { get; set; } = true;
    public bool ShowHidden { get; set; }
    public string? Error { get; private set; }
    public DateTime? Mtime { get; set; }
    public DateTime LastPoll { get; set; } = DateTime.Now;
    private Loading? loading;
    /// <summary>Set while a slow folder is being read in the background (path being loaded).</summary>
    public string? LoadingTo => loading?.To;
    public DateTime? LoadingSince => loading?.Since;
    /// <summary>Raised (from a worker thread) when deep search results arrived – the UI schedules a poll.</summary>
    public Action? DeepResultsAvailable { get; set; }

    public Tab(string path, bool showHidden)
    {
        Path = path;
        ShowHidden = showHidden;
        Reload(null);
    }

    public string Title => Sftp.Title(Path) ?? VPath.DisplayName(Path);

    public bool HasUp => Entries.Any(e => e.IsUp);

    public Entry? Current => Cursor >= 0 && Cursor < View.Count ? Entries[View[Cursor]] : null;

    /// <summary>Selected entries (in view order), or the entry under the cursor if nothing is selected.</summary>
    public List<string> Targets()
    {
        if (Selected.Count == 0)
            return Current is { IsUp: false } c ? [c.Path] : [];
        return View.Select(i => Entries[i]).Where(e => !e.IsUp && Selected.Contains(e.Path)).Select(e => e.Path).ToList();
    }

    /// <summary>Re-reads the directory. focus = name to put the cursor on; null keeps the current entry.</summary>
    public void Reload(string? focus)
    {
        var keep = focus ?? Current?.Name;
        try
        {
            Entries = Fs.WithUp(Fs.ReadDir(Path), Path);
            Error = null;
        }
        catch (Exception e)
        {
            Entries = Fs.WithUp([], Path);
            Error = e.Message;
        }
        Mtime = Fs.DirMtime(Path);
        if (DeepRes.Count > 0) Entries.AddRange(DeepRes.Select(e => e));
        var names = new HashSet<string>(Entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        Selected.RemoveWhere(p => !names.Contains(p));
        RebuildView(keep);
    }

    public void RebuildView(string? keep = null)
    {
        keep ??= Current?.Name;
        var (key, asc) = (Sort, Asc);
        Matcher = Services.Search.Compile(Filter, Search);
        SyncDeep();
        bool deep = DeepActive;
        bool content = deep && Search.Content;
        bool ranked = Matcher.Ranked && !content;
        // results the user renamed stay listed until the query / folder changes
        var kk = $"{Filter}|{Search}|{Path}";
        if (kk != keptKey)
        {
            kept.Clear();
            keptKey = kk;
        }
        var scored = new List<(int Score, int Index)>();
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            if (e.IsUp || e.Deep != deep || (!ShowHidden && e.Hidden)) continue;
            if (kept.Contains(e.Path))
            {
                scored.Add((0, i));
                continue;
            }
            if (!Matcher.FiltersOk(e)) continue;
            if (content && e.Deep)
            {
                scored.Add((0, i)); // the walker already matched the file contents
                continue;
            }
            if (Matcher.Score(e.Name) is { } sc) scored.Add((sc, i));
        }
        var entries = Entries;
        if (ranked)
        {
            // fuzzy: best match first (ties: natural name order)
            scored.Sort((a, b) =>
            {
                int o = b.Score.CompareTo(a.Score);
                return o != 0 ? o : Text.NaturalCmp(entries[a.Index].Name, entries[b.Index].Name);
            });
        }
        else
        {
            scored.Sort((pa, pb) =>
            {
                var x = entries[pa.Index];
                var y = entries[pb.Index];
                // folders first, except when sorting by date: then files and folders are mixed
                int group = key == SortKey.Modified ? 0 : y.IsDir.CompareTo(x.IsDir);
                if (group != 0) return group;
                int byName = Text.NaturalCmp(x.Name, y.Name);
                int o = key switch
                {
                    SortKey.Name => byName,
                    SortKey.Ext => Then(string.CompareOrdinal(x.Ext, y.Ext), byName),
                    SortKey.Size => x.IsDir ? byName : Then(x.Size.CompareTo(y.Size), byName),
                    _ => Then(Nullable.Compare(x.Modified, y.Modified), byName),
                };
                return asc ? o : -o;
            });
        }
        var idx = scored.Select(s => s.Index).ToList();
        // the ".." row only shows while not searching
        if (Matcher.IsAll)
        {
            int up = Entries.FindIndex(e => e.IsUp);
            if (up >= 0) idx.Insert(0, up);
        }
        View = idx;
        int found = keep == null ? -1 : View.FindIndex(i => Entries[i].Name == keep);
        Cursor = Math.Min(found >= 0 ? found : Cursor, Math.Max(0, View.Count - 1));
        Anchor = Cursor;
    }

    private static int Then(int a, int b) => a != 0 ? a : b;

    /// <summary>Subfolder search is on and there is something to look for.</summary>
    public bool DeepActive => (Search.Deep || Search.Content) && Filter.Length > 0;

    /// <summary>(running, truncated) while a subfolder search is active.</summary>
    public (bool Running, bool Truncated)? DeepStatus => DeepJob != null && DeepActive ? (DeepJob.Running, DeepJob.Truncated) : null;

    /// <summary>Starts / restarts / stops the background walk when the query changed.</summary>
    private void SyncDeep()
    {
        var k = DeepActive ? $"{Filter}|{Search}|{Exclude}|{ShowHidden}" : "";
        if (k == deepKey) return;
        deepKey = k;
        DeepJob?.Dispose(); // cancels the old walk
        DeepJob = null;
        DeepRes.Clear();
        Entries.RemoveAll(e => e.Deep);
        if (DeepActive)
            DeepJob = Deep.Spawn(Path, Filter, Search, Deep.ParseExclude(Exclude), ShowHidden, () => DeepResultsAvailable?.Invoke());
    }

    /// <summary>Takes the results found so far. Returns true while the walk is still running.</summary>
    public bool PollDeep()
    {
        if (DeepJob == null) return false;
        var got = DeepJob.Drain();
        bool running = DeepJob.Running;
        if (got.Count > 0)
        {
            var known = new HashSet<string>(DeepRes.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            got.RemoveAll(e => known.Contains(e.Path)); // a renamed result may be found again
        }
        if (got.Count > 0)
        {
            DeepRes.AddRange(got);
            Entries.AddRange(got);
            RebuildView(null);
        }
        return running;
    }

    /// <summary>A file or folder of this tab was renamed: keep it in the (search) results even if the new name no longer
    /// matches. Returns the name the entry has in the list.</summary>
    public string NoteRename(string old, string @new)
    {
        foreach (var e in DeepRes.Where(e => VPath.StartsWith(e.Path, old)))
        {
            var rest = VPath.StripPrefix(e.Path, old);
            if (rest == null) continue;
            var moved = rest.Length == 0 ? @new : VPath.Join(@new, rest);
            if (VPath.StripPrefix(moved, Path) is { } rel) e.Name = rel;
            if (VPath.Same(e.Path, old) && VPath.FileName(@new) is { } f) e.Ext = e.IsDir ? "" : VPath.Extension(f);
            e.Path = moved;
        }
        kept.Add(@new);
        return DeepRes.FirstOrDefault(e => VPath.Same(e.Path, @new))?.Name ?? VPath.FileName(@new) ?? "";
    }

    public void SetSort(SortKey k)
    {
        if (Sort == k) Asc = !Asc;
        else
        {
            Sort = k;
            // newest / biggest first by default
            Asc = k is not (SortKey.Modified or SortKey.Size);
        }
        RebuildView(null);
    }

    // ------------------------------------------------------------------ navigation

    private void Commit(string to, List<Entry> entries, string? focus)
    {
        Path = to;
        Entries = entries;
        Error = null;
        Selected.Clear();
        Filter = "";
        FilterOpen = false;
        Cursor = 0;
        Mtime = Fs.DirMtime(Path);
        RebuildView(focus ?? "");
        ScrollToTop?.Invoke();
    }

    /// <summary>Asks the view to scroll to the top (after arriving in a folder).</summary>
    public Action? ScrollToTop { get; set; }

    private static (List<Entry>? Entries, string? Err) Load(string to)
    {
        try
        {
            return (Fs.WithUp(Fs.ReadDir(to), to), null);
        }
        catch (Exception e)
        {
            return (null, $"{to}: {e.Message}");
        }
    }

    public string? Go(string to) => GoFocus(to, null);

    /// <summary>Like Go, but puts the cursor on focus (a name in the target folder). Returns an error message or null.</summary>
    public string? GoFocus(string to, string? focus)
    {
        if (to == Path)
        {
            if (focus != null)
            {
                Filter = "";
                FilterOpen = false;
            }
            Reload(focus);
            return null;
        }
        focus ??= VPath.Parent(Path) is { } par && VPath.Same(par, to) ? VPath.FileName(Path) : null;
        var r = LoadTimed(to, NavKind.Go, focus);
        if (r is not { } res) return null; // finishes in PollLoad
        if (res.Err != null) return res.Err;
        FinishNav(NavKind.Go, to, res.Entries!, focus);
        return null;
    }

    /// <summary>Reads a folder on a worker thread. Fast folders complete right away; slow ones (network, huge) keep
    /// the old view and finish later in PollLoad.</summary>
    private (List<Entry>? Entries, string? Err)? LoadTimed(string to, NavKind kind, string? focus)
    {
        var task = Task.Run(() => Load(to));
        if (task.Wait(TimeSpan.FromMilliseconds(120)))
        {
            loading = null;
            return task.Result;
        }
        loading = new Loading { To = to, Kind = kind, Focus = focus, Task = task };
        return null;
    }

    private void FinishNav(NavKind kind, string to, List<Entry> entries, string? focus)
    {
        var old = Path;
        switch (kind)
        {
            case NavKind.Go:
                Back.Add(old);
                Fwd.Clear();
                break;
            case NavKind.Back:
                Fwd.Add(old);
                break;
            case NavKind.Forward:
                Back.Add(old);
                break;
        }
        Commit(to, entries, focus);
    }

    /// <summary>Completes a pending slow navigation when its folder has been read.</summary>
    public LoadPoll PollLoad()
    {
        if (loading == null) return default;
        if (!loading.Task.IsCompleted) return new LoadPoll(true, null, null);
        var l = loading;
        loading = null;
        var (entries, err) = l.Task.Result;
        if (err == null)
        {
            FinishNav(l.Kind, l.To, entries!, l.Focus);
            return new LoadPoll(false, null, l.To);
        }
        if (l.Kind == NavKind.Back) Back.Add(l.To);
        else if (l.Kind == NavKind.Forward) Fwd.Add(l.To);
        return new LoadPoll(false, err, null);
    }

    public string? Up()
    {
        if (Sftp.IsRoot(Path)) return null;
        return VPath.Parent(Path) is { } p ? Go(p) : null;
    }

    public string? GoBack() => History(Back, NavKind.Back);

    public string? GoForward() => History(Fwd, NavKind.Forward);

    private string? History(List<string> stack, NavKind kind)
    {
        if (stack.Count == 0) return null;
        var to = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        var r = LoadTimed(to, kind, null);
        if (r is not { } res) return null;
        if (res.Err != null)
        {
            stack.Add(to);
            return res.Err;
        }
        FinishNav(kind, to, res.Entries!, null);
        return null;
    }

    // ------------------------------------------------------------------ cursor / selection

    public void SetCursor(int i)
    {
        if (View.Count == 0) return;
        Cursor = Math.Clamp(i, 0, View.Count - 1);
    }

    public void MoveCursor(int delta, bool extend)
    {
        if (View.Count == 0) return;
        int old = Cursor;
        int n = Math.Clamp(Cursor + delta, 0, View.Count - 1);
        SetCursor(n);
        if (extend) SelectRange(old, n);
        else Anchor = n;
    }

    public void SelectRange(int a, int b)
    {
        int lo = Math.Min(a, b), hi = Math.Min(Math.Max(a, b), View.Count - 1);
        for (int vi = lo; vi <= hi; vi++)
        {
            var e = Entries[View[vi]];
            if (!e.IsUp) Selected.Add(e.Path);
        }
    }

    public void ToggleAt(int vi)
    {
        if (vi < 0 || vi >= View.Count || Entries[View[vi]].IsUp) return;
        var p = Entries[View[vi]].Path;
        if (!Selected.Remove(p)) Selected.Add(p);
    }

    public void SelectAll() =>
        Selected = new HashSet<string>(View.Select(i => Entries[i]).Where(e => !e.IsUp).Select(e => e.Path), StringComparer.OrdinalIgnoreCase);

    public void Invert()
    {
        var all = View.Select(i => Entries[i]).Where(e => !e.IsUp).Select(e => e.Path);
        Selected = new HashSet<string>(all.Where(p => !Selected.Contains(p)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Type-ahead: jump to the next entry starting with prefix (falls back to "contains").</summary>
    public void JumpToPrefix(string prefix)
    {
        if (View.Count == 0) return;
        var p = prefix.ToLowerInvariant();
        int? Find(Func<string, bool> pred)
        {
            for (int k = 0; k < View.Count; k++)
            {
                int vi = (Cursor + k) % View.Count;
                if (pred(Entries[View[vi]].Name.ToLowerInvariant())) return vi;
            }
            return null;
        }
        if ((Find(n => n.StartsWith(p, StringComparison.Ordinal)) ?? Find(n => n.Contains(p, StringComparison.Ordinal))) is { } hit)
        {
            SetCursor(hit);
            Anchor = hit;
        }
    }

    public string SelectionSummary()
    {
        int files = View.Count(i => !Entries[i].IsDir);
        int dirs = View.Count(i => Entries[i].IsDir && !Entries[i].IsUp);
        if (Selected.Count == 0) return $"{dirs} folders, {files} files";
        long size = View.Select(i => Entries[i]).Where(e => Selected.Contains(e.Path)).Sum(e => e.Size);
        return $"{Selected.Count} selected · {Text.FmtSize(size)}";
    }

    /// <summary>Replaces the session sort (restore).</summary>
    public void SetSortRaw(SortKey k, bool asc)
    {
        Sort = k;
        Asc = asc;
    }
}
