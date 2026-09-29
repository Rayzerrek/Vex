namespace Vex.Libghostty;

public readonly record struct CursorState(
    int X, int Y, bool Visible, bool Blinking, CursorShape Shape);

/// <summary>
/// One rendered cell: the grapheme text ("" for empty cells and wide-char
/// spacer tails), the grid width, style flags, and the cell's fg/bg colors
/// as tagged values (palette index or packed RGB, or None for the default).
/// </summary>
public struct CellInfo
{
    public string Text = "";
    public bool Wide = false;
    public bool Tail = false;
    public CellFlags Flags = CellFlags.None;
    public ColorTag FgTag = ColorTag.None;
    public int FgValue = 0;
    public ColorTag BgTag = ColorTag.None;
    public int BgValue = 0;

    public CellInfo()
    {
        Text = "";
    }
}

public sealed class FrameRow
{
    public bool Dirty;
    public bool HasSelection;
    public int SelectionStart;
    public int SelectionEnd;
    public CellInfo[] Cells = Array.Empty<CellInfo>();
}
