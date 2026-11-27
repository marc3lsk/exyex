using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mavyex.Core;

namespace Mavyex.UI;
// STUB – to be replaced by the port (see the agent brief). Keep the public signatures.
// Theme colours: application DynamicResources (Theme.cs) – PanelBg InputBg MenuBg Fg WeakFg Line Accent AccentFill
// ButtonBg ButtonPressed Overlay RowBg RowHover RowSel RowSelBorder HlFg HlBg ErrorFg; Theme.IsDark = current mode.

/// <summary>Side-by-side text diff of two files in its own window (DiffPlex). F7/F8 prev/next difference, ignore
/// whitespace / case, Esc closes. Works on local, SFTP and archive-inner files (text only, ≤ 4 MB).</summary>
public sealed class TextDiffWindow : Window
{
    /// <summary>dark: own theme of the diff window (null = follow the app). darkChanged reports a user toggle (to persist).</summary>
    public TextDiffWindow(string a, string b, bool? dark, Action<bool>? darkChanged) { }
}
