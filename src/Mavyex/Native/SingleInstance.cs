using System.IO.Pipes;
using System.Runtime.InteropServices;
using Mavyex.Core;

namespace Mavyex.Native;

/// <summary>
/// Single instance (single.rs): a second <c>mavyex &lt;path&gt;</c> hands the path to the running window (which opens it
/// as a new tab) and exits. Detection uses a named mutex; the path travels over a per-user named pipe
/// (CurrentUserOnly: only this user can write to it).
/// </summary>
internal static class SingleInstance
{
    private static Mutex? mutex;

    private static string Suffix()
    {
        var user = Environment.UserName;
        // test instances with their own data folder must not talk to the user's real mavyex
        var home = Environment.GetEnvironmentVariable("MAVYEX_HOME");
        if (!string.IsNullOrEmpty(home)) user += "-" + Fnv(home.ToLowerInvariant()).ToString("x");
        return user;
    }

    private static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
        {
            h ^= c;
            h *= 16777619;
        }
        return h;
    }

    private static string PipeName => $"mavyex-{Suffix()}";

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);

    /// <summary>true = forwarded to the running instance (exit now); false = we are the first (or standalone).</summary>
    public static bool Forwarded(string[] args)
    {
        try
        {
            mutex = new Mutex(false, $@"Local\mavyex-instance-{Suffix()}", out bool created);
            if (created) return false; // keep the mutex for the lifetime of the process
            // resolve relative paths ("mavyex .") against *our* working directory
            var path = args.Length > 0 ? VPath.ParseUserPath(args[0], Environment.CurrentDirectory) ?? "" : "";
            AllowSetForegroundWindow(-1);
            // the first instance may still be creating its pipe: retry briefly
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                    c.Connect(50);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(path);
                    c.Write(bytes);
                    return true;
                }
                catch (Exception)
                {
                    Thread.Sleep(50);
                }
            }
            return false; // the other instance is not answering: run standalone
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Listens for requests of later launches; onRequest(path or null) is called on a worker thread.</summary>
    public static void Listen(Action<string?> onRequest)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    s.WaitForConnection();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    var text = System.Text.Encoding.UTF8.GetString(ms.ToArray()).Trim();
                    onRequest(text.Length == 0 ? null : text);
                }
                catch (Exception)
                {
                    Thread.Sleep(500);
                }
            }
        }) { IsBackground = true, Name = "single-instance" };
        t.Start();
    }
}
