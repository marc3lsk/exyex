using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mavyex.Core;

namespace Mavyex.UI;
// STUB – to be replaced by the port (see the agent brief). Keep the public signatures.
// Theme colours: application DynamicResources (Theme.cs) – PanelBg InputBg MenuBg Fg WeakFg Line Accent AccentFill
// ButtonBg ButtonPressed Overlay RowBg RowHover RowSel RowSelBorder HlFg HlBg ErrorFg; Theme.IsDark = current mode.

/// <summary>Asynchronous image thumbnails for the grid view (png/jpg/gif/bmp/webp/ico/svg).</summary>
public static class Thumbs
{
    public static bool IsThumbnailable(string ext, long size) => false;
    /// <summary>Cached thumbnail or null; when null and loadable, it is decoded in the background and
    /// <paramref name="ready"/> is invoked on the UI thread once it is available. UI thread only.</summary>
    public static ImageSource? Get(string path, Action ready) => null;
}
