using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mavyex.Core;

namespace Mavyex.UI;

/// <summary>Image thumbnails for the grid view (thumbs.rs): decoded on worker threads, cached as frozen bitmaps.
/// Local files only (the caller skips remote / archive entries, like app.rs).</summary>
public static class Thumbs
{
    private static readonly string[] Exts = ["png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "svg", "tiff"];
    internal const long MaxFile = 40L * 1024 * 1024;
    private const int MaxInflight = 4;
    private const int MaxCached = 500;
    internal const int Thumb = 192;

    private sealed class Slot
    {
        public bool Loading = true;
        public ImageSource? Img;           // null when done = failed
        public List<Action> Waiters = [];  // ready callbacks of everyone who asked while it was loading
    }

    private static readonly Dictionary<string, Slot> Map = new(StringComparer.Ordinal);
    private static int inflight;
    // callers turned away because MaxInflight decodes were running: asked again shortly (Rust: repaint after 40 ms)
    private static readonly List<Action> Retry = [];
    private static DispatcherTimer? retryTimer;

    public static bool IsThumbnailable(string ext, long size) => size <= MaxFile && Exts.Contains(ext);

    /// <summary>Cached thumbnail or null; when null and loadable, it is decoded in the background and
    /// <paramref name="ready"/> is invoked on the UI thread once it is available (or once a decode slot frees up –
    /// then simply call Get again). UI thread only.</summary>
    public static ImageSource? Get(string path, Action ready)
    {
        if (Map.TryGetValue(path, out var slot))
        {
            if (slot.Loading && !slot.Waiters.Contains(ready)) slot.Waiters.Add(ready);
            return slot.Img;
        }
        var ui = Dispatcher.CurrentDispatcher;
        if (inflight >= MaxInflight)
        {
            if (!Retry.Contains(ready)) Retry.Add(ready);
            retryTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => FireRetry(), ui);
            retryTimer.Start();
            return null;
        }
        if (Map.Count > MaxCached)
        {
            // drop old bitmaps, keep the in-flight slots (their results are still awaited)
            foreach (var k in Map.Where(kv => !kv.Value.Loading).Select(kv => kv.Key).ToList()) Map.Remove(k);
        }
        slot = new Slot();
        slot.Waiters.Add(ready);
        Map[path] = slot;
        inflight++;
        Worker.RunSta(() =>
        {
            var img = Decode(path);
            ui.BeginInvoke(() =>
            {
                inflight = Math.Max(0, inflight - 1);
                slot.Img = img;
                slot.Loading = false;
                if (!Map.ContainsKey(path)) Map[path] = slot;
                var w = slot.Waiters;
                slot.Waiters = [];
                foreach (var a in w) a();
            });
        });
        return null;
    }

    private static void FireRetry()
    {
        retryTimer?.Stop();
        var r = Retry.ToList();
        Retry.Clear();
        foreach (var a in r) a();
    }

    /// <summary>Decodes a thumbnail that fits into 192×192; null on any failure.</summary>
    internal static BitmapSource? Decode(string path)
    {
        try
        {
            if (VPath.Extension(path) == "svg") return ImageDecode.RenderSvg(File.ReadAllBytes(path), Thumb).Img;
            return ImageDecode.Decode(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Thumb).Img;
        }
        catch (Exception)
        {
            // any decoder / IO error just means "no thumbnail" (the grid falls back to the shell icon)
            return null;
        }
    }
}
