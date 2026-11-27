using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Mavyex.Native;

/// <summary>Per-user install / uninstall without admin rights (install.rs):
///   mavyex --install     copy to %LOCALAPPDATA%\Programs\mavyex, add to the user PATH, register in "Apps &amp; features",
///                        create a Start menu shortcut
///   mavyex --uninstall   undo all of that
/// A copy of the exe named *_setup.exe runs the install with prompts when double-clicked.
/// **Never run on the developer's machine / untested.**</summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\mavyex";

    private static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "mavyex");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\mavyex.lnk");

    private static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "?";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, int msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    private const uint MB_OK = 0, MB_YESNO = 4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONINFORMATION = 0x40;
    private const int IDYES = 6;

    public static int Box(string title, string text, uint style) => MessageBoxW(IntPtr.Zero, text, title, style);

    public static bool IsSetupExe(string[] args) =>
        args.Length == 0 &&
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").EndsWith("_setup", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ PATH

    private static bool SameDir(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The user PATH keeping its registry type (usually REG_EXPAND_SZ, so %USERPROFILE% entries survive).</summary>
    private static (string Value, RegistryValueKind Kind) ReadPath(RegistryKey env)
    {
        var v = env.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var kind = env.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase) ? env.GetValueKind("Path") : RegistryValueKind.ExpandString;
        return (v, kind);
    }

    /// <summary>Tells Explorer and newly started shells that the environment changed.</summary>
    private static void BroadcastEnvChange() =>
        SendMessageTimeoutW((IntPtr)0xffff, 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);

    private static ProcessStartInfo Hidden(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    // ------------------------------------------------------------------ install

    public static string Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate this exe");
        var dir = InstallDir;
        var dest = Path.Combine(dir, "mavyex.exe");
        Directory.CreateDirectory(dir);
        if (!string.Equals(Path.GetFullPath(exe), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Copy(exe, dest, overwrite: true);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Could not copy to {dest}: {e.Message}\n\nIs mavyex running? Close it and try again.");
            }
        }

        // PATH (user scope)
        using (var env = Registry.CurrentUser.OpenSubKey("Environment", writable: true) ?? throw new InvalidOperationException("cannot open user environment"))
        {
            var (path, kind) = ReadPath(env);
            if (!path.Split(';').Any(p => SameDir(p, dir)))
            {
                var next = string.IsNullOrWhiteSpace(path) ? dir : path.TrimEnd(';') + ";" + dir;
                env.SetValue("Path", next, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            }
        }

        // "Apps & features" entry
        using (var u = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            u.SetValue("DisplayName", "mavyex");
            u.SetValue("DisplayVersion", Version);
            u.SetValue("Publisher", "mavyex");
            u.SetValue("InstallLocation", dir);
            u.SetValue("DisplayIcon", dest);
            u.SetValue("UninstallString", $"\"{dest}\" --uninstall");
            u.SetValue("NoModify", 1, RegistryValueKind.DWord);
            u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        // Start menu shortcut (best effort)
        static string Q(string s) => s.Replace("'", "''");
        var script = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Q(ShortcutPath)}');" +
                     $"$s.TargetPath='{Q(dest)}';$s.WorkingDirectory='{Q(dir)}';$s.Save()";
        try { Process.Start(Hidden("powershell", "-NoProfile", "-WindowStyle", "Hidden", "-Command", script))?.WaitForExit(15000); } catch { }

        BroadcastEnvChange();
        return dest;
    }

    public static void Uninstall()
    {
        var dir = InstallDir;
        using (var env = Registry.CurrentUser.OpenSubKey("Environment", writable: true))
        {
            if (env != null)
            {
                var (path, kind) = ReadPath(env);
                var kept = path.Split(';').Where(p => p.Trim().Length > 0 && !SameDir(p, dir));
                env.SetValue("Path", string.Join(";", kept), kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { File.Delete(ShortcutPath); } catch { }
        BroadcastEnvChange();
        // we may be running from the install folder: delete it a moment after we exit
        try { Process.Start(Hidden("cmd", "/c", $"ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir}\"")); } catch { }
    }

    /// <summary>mavyex --install / --uninstall: silent apart from the result box.</summary>
    public static void RunCli(bool uninstall)
    {
        try
        {
            if (uninstall)
            {
                Uninstall();
                Box("mavyex", "mavyex was removed from your PATH and uninstalled.\nYour settings in %APPDATA%\\mavyex were kept.", MB_OK | MB_ICONINFORMATION);
            }
            else
            {
                var dest = Install();
                Box("mavyex", $"Installed to {dest}\n\nOpen a NEW terminal and type:  mavyex .", MB_OK | MB_ICONINFORMATION);
            }
        }
        catch (Exception e)
        {
            Box(uninstall ? "mavyex uninstall failed" : "mavyex install failed", e.Message, MB_OK | MB_ICONERROR);
        }
    }

    /// <summary>Double-clicked mavyex_&lt;ver&gt;_x64_setup.exe: ask, install, offer to launch.</summary>
    public static void RunSetupWizard()
    {
        var ask = $"Install mavyex {Version} for the current user?\n\n• copies it to {InstallDir}\n" +
                  "• adds it to your PATH, so \"mavyex .\" works in any terminal\n• adds a Start menu shortcut";
        if (Box("mavyex setup", ask, MB_YESNO | MB_ICONQUESTION) != IDYES) return;
        try
        {
            var dest = Install();
            if (Box("mavyex setup", "Installation finished.\n\nOpen a NEW terminal and type:  mavyex .\n\nStart mavyex now?", MB_YESNO | MB_ICONINFORMATION) == IDYES)
                Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Box("mavyex setup failed", e.Message, MB_OK | MB_ICONERROR);
        }
    }
}
