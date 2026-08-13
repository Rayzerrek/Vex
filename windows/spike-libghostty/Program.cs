using Vex.Libghostty;

var t = new GhosttyTerminal(40, 5);
t.Feed("apple banana cherry\r\nsecond line here\r\nthird\r\n");

// Drag-select "apple" on row 0 with pixel positions at the END of each cell
// (past the 60% threshold ghostty applies to the cell under the pointer).
t.SelectionPress(0, 0, 1.0, 8.0);
t.SelectionDrag(4, 0, 4 * 8 + 7.0, 8.0);
t.UpdateFrame();
Console.WriteLine($"drag text: '{t.GetSelectedText()}'");

// Double-click word selection (gesture derives from press timing).
t.ClearSelection();
t.SelectionPress(7, 0, 7 * 8 + 1.0, 8.0);
t.SelectionRelease(7, 0);
t.SelectionPress(8, 0, 8 * 8 + 1.0, 8.0);
t.UpdateFrame();
Console.WriteLine($"double-click word text: '{t.GetSelectedText()}'");

// Cross-row drag.
t.ClearSelection();
t.SelectionPress(2, 0, 2 * 8 + 1.0, 8.0);
t.SelectionDrag(4, 1, 4 * 8 + 7.0, 8 + 17.0);
t.UpdateFrame();
Console.WriteLine($"cross-row text: '{t.GetSelectedText().Replace("\r", "").Replace("\n", "|")}'");

// Scroll then select on scrolled viewport.
t.Feed("\r\n\r\n\r\n\r\n\r\n\r\n");
t.ScrollBy(-3);
t.UpdateFrame();
t.ClearSelection();
t.SelectionPress(0, 0, 1.0, 8.0);
t.SelectionDrag(6, 0, 6 * 8 + 7.0, 8.0);
t.UpdateFrame();
Console.WriteLine($"scrolled-viewport text: '{t.GetSelectedText().Replace("\r", "").Replace("\n", "|")}'");

t.Dispose();
Console.WriteLine("selection spike OK");

