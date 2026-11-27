using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mavyex.Core;

namespace Mavyex.UI;
// STUB – to be replaced by the port (see the agent brief). Keep the public signatures.
// Theme colours: application DynamicResources (Theme.cs) – PanelBg InputBg MenuBg Fg WeakFg Line Accent AccentFill
// ButtonBg ButtonPressed Overlay RowBg RowHover RowSel RowSelBorder HlFg HlBg ErrorFg; Theme.IsDark = current mode.

/// <summary>Quick Look side panel (F3): text head, images, folder summary, archive contents, hex head; loads in the background.</summary>
public sealed class PreviewPanel : UserControl
{
    /// <summary>Raised when the user closes the panel with its ✕.</summary>
    public event Action? CloseRequested;
    /// <summary>Shows the entry under the cursor (null = nothing). Cheap to call repeatedly with the same entry.</summary>
    public void ShowEntry(Entry? e) { }
    internal void RaiseClose() => CloseRequested?.Invoke();
}
