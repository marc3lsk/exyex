namespace Mavyex.Services;

// STUB – to be replaced by the port of ops.rs (subagent "ops"). Keep these signatures.

public enum OpKind { Copy, Move, Trash, Delete, Zip }

/// <summary>What to do when the target of a copy / move already exists.</summary>
public enum Policy { Rename, Overwrite, Skip, Newer }

public enum JobState { Running, Done, Cancelled, Failed }

/// <summary>A background file operation (copy / move / trash / delete / zip) with progress and cancel.
/// Progress fields are updated from the worker thread; the UI polls them (e.g. with a DispatcherTimer).</summary>
public sealed class Job
{
    public long Id { get; init; }
    public OpKind Kind { get; init; }
    /// <summary>e.g. "Copying 5 items".</summary>
    public string Title { get; init; } = "";
    public string? Dest { get; init; }
    public DateTime Started { get; init; } = DateTime.Now;
    public DateTime? Finished { get; set; }
    public volatile bool Cancelling;
    public long Done;     // Interlocked
    public long Total;    // Interlocked
    public string Current = "";
    public JobState State { get; set; } = JobState.Running;
    public string? Error { get; set; }
    /// <summary>Raised on the worker thread when the job finished (any state).</summary>
    public event Action<Job>? Completed;
    public void Cancel() => Cancelling = true;
    internal void RaiseCompleted() => Completed?.Invoke(this);
}

public static class Ops
{
    public static string Verb(this OpKind k) => k switch
    {
        OpKind.Zip => "Zipping",
        OpKind.Copy => "Copying",
        OpKind.Move => "Moving",
        OpKind.Trash => "Recycling",
        _ => "Deleting",
    };

    public static Job Spawn(OpKind kind, IReadOnlyList<string> srcs, string? dest) => SpawnWith(kind, srcs, dest, new());

    /// <summary>policies maps a source path to what to do when its target exists (default: keep both).</summary>
    public static Job SpawnWith(OpKind kind, IReadOnlyList<string> srcs, string? dest, Dictionary<string, Policy> policies) =>
        throw new NotImplementedException();

    /// <summary>"name.ext" -> "name (2).ext" until it does not exist (local folders).</summary>
    public static string UniqueDest(string dir, string name) => Path.Combine(dir, name);
}
