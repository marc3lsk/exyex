using System.Runtime.InteropServices;

namespace Mavyex.Services;

/// <summary>Windows "Properties" dialog (props.rs) – the same sheet Explorer shows – for local files / folders / drives.</summary>
public static partial class Props
{
    /// <summary>Opens the shell Properties dialog: one item via SHObjectProperties, several via SHMultiFileProperties
    /// (a data object built from their PIDLs). Local paths only. Throws InvalidOperationException with a readable
    /// message on failure. Call from the UI (STA) thread.</summary>
    public static void Show(IReadOnlyList<string> paths)
    {
        // app.rs checked this before calling props::show; kept here so every caller gets the same message
        if (paths.Any(p => Sftp.IsRemote(p) || Archive.SplitInner(p) is not null))
            throw new InvalidOperationException("Properties work on local files only");
        if (paths.Count == 0) return;
        if (paths.Count == 1)
        {
            if (!SHObjectProperties(IntPtr.Zero, SHOP_FILEPATH, WidePath(paths[0]), null))
                throw new InvalidOperationException($"Could not open properties of {paths[0]}");
            return;
        }
        ShowMany(paths);
    }

    private static unsafe void ShowMany(IReadOnlyList<string> paths)
    {
        // The shell expects STA; the WPF UI thread already is one, other threads get initialized here.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        var pidls = new List<IntPtr>();
        IntPtr arr = IntPtr.Zero, obj = IntPtr.Zero;
        try
        {
            foreach (var p in paths)
            {
                int hr = SHParseDisplayName(WidePath(p), IntPtr.Zero, out var pidl, 0, IntPtr.Zero);
                if (hr < 0) throw new InvalidOperationException($"{p}: {HrMessage(hr)}");
                pidls.Add(pidl);
            }
            var ids = pidls.ToArray();
            int r = SHCreateShellItemArrayFromIDLists((uint)ids.Length, ids, out arr);
            if (r >= 0)
            {
                // IShellItemArray::BindToHandler is vtable slot 3 (after IUnknown's three methods).
                var bind = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, Guid*, IntPtr*, int>)(*(*(void***)arr + 3));
                Guid bhid = BHID_DataObject, iid = IID_IDataObject;
                IntPtr o;
                r = bind(arr, IntPtr.Zero, &bhid, &iid, &o);
                obj = o;
            }
            if (r >= 0) r = SHMultiFileProperties(obj, 0);
            if (r < 0) throw new InvalidOperationException($"Could not open properties: {HrMessage(r)}");
        }
        finally
        {
            if (obj != IntPtr.Zero) Marshal.Release(obj);
            if (arr != IntPtr.Zero) Marshal.Release(arr);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }

    private static string HrMessage(int hr) => $"{Marshal.GetPInvokeErrorMessage(hr)} (0x{hr:X8})";

    /// <summary><c>C:</c> → <c>C:\</c> (a bare drive letter means "current dir on that drive" to the shell).</summary>
    internal static string WidePath(string p) => p.Length == 2 && p.EndsWith(':') ? p + "\\" : p;

    private const uint SHOP_FILEPATH = 2;
    private const uint COINIT_APARTMENTTHREADED = 2;
    private static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    private static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, IntPtr psfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateShellItemArrayFromIDLists(uint cidl, [In] IntPtr[] rgpidl, out IntPtr ppsiItemArray);

    [LibraryImport("shell32.dll")]
    private static partial int SHMultiFileProperties(IntPtr pdtobj, uint dwFlags);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
}
