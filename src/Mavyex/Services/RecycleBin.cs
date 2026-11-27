using System.Runtime.InteropServices;

namespace Mavyex.Services;

/// <summary>Sends local files / folders to the Recycle Bin with the shell's IFileOperation in one batch
/// (what Rust's `trash` crate does): no UI, no confirmation, undo record. Call from an STA thread.</summary>
internal static class RecycleBin
{
    private const uint FOF_SILENT = 0x4;
    private const uint FOF_NOCONFIRMATION = 0x10;
    private const uint FOF_ALLOWUNDO = 0x40;
    private const uint FOF_NOERRORUI = 0x400;
    private const uint FOFX_RECYCLEONDELETE = 0x80000; // Windows 8+: recycle instead of deleting
    private const int COPYENGINE_E_USER_CANCELLED = unchecked((int)0x80270000);

    public static void DeleteAll(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var type = Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), throwOnError: true)!;
        var op = (IFileOperation)Activator.CreateInstance(type)!;
        var items = new List<IShellItem>();
        try
        {
            op.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOFX_RECYCLEONDELETE);
            foreach (var p in paths)
            {
                var item = ItemFor(p);
                items.Add(item);
                op.DeleteItem(item, IntPtr.Zero);
            }
            int hr = op.PerformOperations();
            if (hr == COPYENGINE_E_USER_CANCELLED) throw new IOException("Recycle Bin: the operation was cancelled");
            if (hr < 0) throw new IOException($"Recycle Bin: {Marshal.GetExceptionForHR(hr)?.Message ?? $"error 0x{hr:X8}"}");
            op.GetAnyOperationsAborted(out var aborted);
            if (aborted) throw new IOException("Recycle Bin: some items could not be recycled");
        }
        finally
        {
            foreach (var i in items) Marshal.ReleaseComObject(i);
            Marshal.ReleaseComObject(op);
        }
    }

    private static IShellItem ItemFor(string p)
    {
        var full = Path.GetFullPath(p);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException($"{p}: The system cannot find the file specified.");
        var iid = typeof(IShellItem).GUID;
        int hr = SHCreateItemFromParsingName(full, IntPtr.Zero, ref iid, out var item);
        if (hr < 0 && full.Length >= 248)
        {
            // over MAX_PATH the shell parser only takes the \\?\ form
            var ext = full.StartsWith(@"\\") ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
            hr = SHCreateItemFromParsingName(ext, IntPtr.Zero, ref iid, out item);
        }
        if (hr < 0 || item == null)
            throw new IOException($"{p}: {Marshal.GetExceptionForHR(hr)?.Message ?? $"error 0x{hr:X8}"}");
        return item;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        // methods are never called from here; the item is only handed to IFileOperation
    }

    // Full vtable order matters even for the methods we do not call.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr hwnd);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem dest);
        void CopyItem(IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem dest);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem dest, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
