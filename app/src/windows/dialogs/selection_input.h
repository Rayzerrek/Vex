#pragma once

// Layout, paint and scroll can replay hover at a stationary pointer. Only a
// changed pointer position may take selection back after keyboard navigation.
struct SelectionInput {
    int x = 0, y = 0;
    bool known = false, keyboard = false;
    constexpr void fromKeyboard() { keyboard = true; }
    constexpr bool hover(int nextX, int nextY) {
        const bool moved = known && (x != nextX || y != nextY);
        const bool accept = moved || (!known && !keyboard);
        x = nextX; y = nextY; known = true;
        if (moved) keyboard = false;
        return accept;
    }
    constexpr void press(int nextX, int nextY) {
        x = nextX; y = nextY; known = true; keyboard = false;
    }
};

// Theme cards are stored column first by Win32, but arrows follow screen space.
constexpr int themeGridNeighbor(int selected, int count, int rows, int dx, int dy) {
    if (count <= 0 || rows <= 0) return -1;
    const int column = selected / rows, row = selected % rows;
    const int columns = (count + rows - 1) / rows;
    const int nextColumn = column + dx < 0 ? 0 : column + dx >= columns ? columns - 1 : column + dx;
    const int rowsInColumn = count - nextColumn * rows < rows ? count - nextColumn * rows : rows;
    const int nextRow = row + dy < 0 ? 0 : row + dy >= rowsInColumn ? rowsInColumn - 1 : row + dy;
    return nextColumn * rows + nextRow;
}
