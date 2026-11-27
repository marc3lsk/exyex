using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mavyex.Core;

namespace Mavyex.UI;
// STUB – to be replaced by the port (see the agent brief). Keep the public signatures.
// Theme colours: application DynamicResources (Theme.cs) – PanelBg InputBg MenuBg Fg WeakFg Line Accent AccentFill
// ButtonBg ButtonPressed Overlay RowBg RowHover RowSel RowSelBorder HlFg HlBg ErrorFg; Theme.IsDark = current mode.

/// <summary>Real shell icons per extension (SHGetFileInfo, cached; folders, drives, .exe/.lnk per file where needed).</summary>
public static class ShellIcons
{
    /// <summary>16 px (large = 48 px for the grid view) icon, cached by extension / kind. UI thread only.</summary>
    public static ImageSource? Get(string ext, bool isDir, bool large = false) => null;
}
