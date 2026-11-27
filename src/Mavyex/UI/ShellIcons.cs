using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mavyex.UI;

/// <summary>Real Windows shell icons by file extension (icons.rs), cached as frozen bitmaps. Like Rust only two kinds
/// are distinguished: a lower-case extension, or "a folder". SHGFI_USEFILEATTRIBUTES means the file need not exist,
/// so per-file icons (.exe resources, .lnk targets) are deliberately not resolved – one lookup per extension.</summary>
public static partial class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = [];

    /// <summary>16 px icon (large = 48 px for the grid view), cached by extension / kind; null when the shell has none.
    /// UI thread only.</summary>
    public static ImageSource? Get(string ext, bool isDir, bool large = false)
    {
        var key = (large ? "L:" : "") + (isDir ? "<dir>" : ext);
        if (!Cache.TryGetValue(key, out var img))
        {
            img = Load(ext, isDir, large);
            Cache[key] = img; // failures are cached too, like Rust's Option
        }
        return img;
    }

    private static ImageSource? Load(string ext, bool isDir, bool large)
    {
        try
        {
            var name = isDir ? "folder" : $"file.{ext}";
            uint attrs = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            IntPtr hicon = large ? ExtraLarge(name, attrs) : IntPtr.Zero;
            if (hicon == IntPtr.Zero)
            {
                // Rust used SHGFI_LARGEICON (32 px) for the grid; the 48 px system list looks closer to Explorer's
                // "medium icons", 32 px stays the fallback.
                var info = new SHFILEINFOW();
                var r = SHGetFileInfoW(name, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(),
                    SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON) | SHGFI_USEFILEATTRIBUTES);
                if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                hicon = info.hIcon;
            }
            try
            {
                // handles both alpha icons and old mask-only icons (what icons.rs did by hand)
                var bmp = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally
            {
                DestroyIcon(hicon);
            }
        }
        catch (Exception e) when (e is COMException or ArgumentException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>48 px icon from the system image list (SHIL_EXTRALARGE), or 0.</summary>
    private static unsafe IntPtr ExtraLarge(string name, uint attrs)
    {
        var info = new SHFILEINFOW();
        var r = SHGetFileInfoW(name, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
        if (r == IntPtr.Zero) return IntPtr.Zero;
        var iid = IID_IImageList;
        if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out var list) < 0 || list == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            // IImageList::GetIcon is vtable slot 10 (IUnknown 0-2, Add, ReplaceIcon, SetOverlayImage, Replace,
            // AddMasked, Draw, Remove, GetIcon).
            var getIcon = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, int>)(*(*(void***)list + 10));
            IntPtr h;
            return getIcon(list, info.iIcon, ILD_TRANSPARENT, &h) >= 0 ? h : IntPtr.Zero;
        }
        finally
        {
            Marshal.Release(list);
        }
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10,
        SHGFI_SYSICONINDEX = 0x4000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int SHIL_EXTRALARGE = 2;
    private const uint ILD_TRANSPARENT = 1;
    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
