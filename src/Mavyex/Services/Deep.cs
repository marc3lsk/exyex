using Mavyex.Core;

namespace Mavyex.Services;

// STUB – to be replaced by the port of deep.rs (subagent "search"). Keep these signatures.

/// <summary>A running subfolder (or content) search. Results are drained by the UI with <see cref="Drain"/>.
/// Dispose cancels the walk.</summary>
public sealed class DeepJob : IDisposable
{
    public const int MaxResults = 50_000;
    public bool Running { get; internal set; }
    public bool Truncated { get; internal set; }
    /// <summary>Entries found since the last call (thread-safe); also updates Running / Truncated.</summary>
    public List<Entry> Drain() => [];
    public void Dispose() { }
}

public static class Deep
{
    /// <summary>Splits the manual exclude list: names / globs separated by ',' or ';'.</summary>
    public static List<string> ParseExclude(string s) =>
        s.Split([',', ';']).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    /// <summary>Starts the background walk. Result names are paths relative to root; content search fills Entry.Snippet.
    /// onResults is called (from a worker thread) whenever new results are available, so the UI can schedule a refresh.</summary>
    public static DeepJob Spawn(string root, string query, SearchOpts opts, List<string> exclude, bool showHidden, Action? onResults = null) =>
        new();
}
