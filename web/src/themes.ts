export type TerminalTheme = {
  name: string;
  background: string;
  foreground: string;
  cursor: string;
  selectionBackground: string;
  black: string;
  red: string;
  green: string;
  yellow: string;
  blue: string;
  magenta: string;
  cyan: string;
  white: string;
  brightBlack: string;
  brightRed: string;
  brightGreen: string;
  brightYellow: string;
  brightBlue: string;
  brightMagenta: string;
  brightCyan: string;
  brightWhite: string;
};

const theme = (name: string, colors: Omit<TerminalTheme, "name">): TerminalTheme => ({ name, ...colors });

export const themes: TerminalTheme[] = [
  theme("Vex Dark", { background: "#282C34", foreground: "#E4E4E7", cursor: "#E4E4E7", selectionBackground: "#3D4354", black: "#282C34", red: "#FF5A5A", green: "#5AF05A", yellow: "#F0D25A", blue: "#5A5AFF", magenta: "#F05AF0", cyan: "#5AF0F0", white: "#E4E4E7", brightBlack: "#3D4354", brightRed: "#FF8C8C", brightGreen: "#8CFF8C", brightYellow: "#FFE68C", brightBlue: "#8C8CFF", brightMagenta: "#FF8CFF", brightCyan: "#8CFFFF", brightWhite: "#FFFFFF" }),
  theme("One Dark", { background: "#282C34", foreground: "#ABB2BF", cursor: "#528BFF", selectionBackground: "#3E4452", black: "#282C34", red: "#E06C75", green: "#98C379", yellow: "#E5C07B", blue: "#61AFEF", magenta: "#C678DD", cyan: "#56B6C2", white: "#ABB2BF", brightBlack: "#5C6370", brightRed: "#E06C75", brightGreen: "#98C379", brightYellow: "#E5C07B", brightBlue: "#61AFEF", brightMagenta: "#C678DD", brightCyan: "#56B6C2", brightWhite: "#FFFFFF" }),
  theme("Dracula", { background: "#282A36", foreground: "#F8F8F2", cursor: "#F8F8F2", selectionBackground: "#44475A", black: "#21222C", red: "#FF5555", green: "#50FA7B", yellow: "#F1FA8C", blue: "#BD93F9", magenta: "#FF79C6", cyan: "#8BE9FD", white: "#F8F8F2", brightBlack: "#6272A4", brightRed: "#FF6E6E", brightGreen: "#69FF94", brightYellow: "#FFFFA5", brightBlue: "#D6ACFF", brightMagenta: "#FF92DF", brightCyan: "#A4FFFF", brightWhite: "#FFFFFF" }),
  theme("Solarized Dark", { background: "#002B36", foreground: "#839496", cursor: "#93A1A1", selectionBackground: "#073642", black: "#073642", red: "#DC322F", green: "#859900", yellow: "#B58900", blue: "#268BD2", magenta: "#D33682", cyan: "#2AA198", white: "#EEE8D5", brightBlack: "#002B36", brightRed: "#CB4B16", brightGreen: "#586E75", brightYellow: "#657B83", brightBlue: "#839496", brightMagenta: "#6C71C4", brightCyan: "#93A1A1", brightWhite: "#FDF6E3" }),
  theme("Nord", { background: "#2E3440", foreground: "#D8DEE9", cursor: "#D8DEE9", selectionBackground: "#434C5E", black: "#3B4252", red: "#BF616A", green: "#A3BE8C", yellow: "#EBCB8B", blue: "#81A1C1", magenta: "#B48EAD", cyan: "#88C0D0", white: "#E5E9F0", brightBlack: "#4C566A", brightRed: "#BF616A", brightGreen: "#A3BE8C", brightYellow: "#EBCB8B", brightBlue: "#81A1C1", brightMagenta: "#B48EAD", brightCyan: "#8FBCBB", brightWhite: "#ECEFF4" }),
  theme("Tokyo Night", { background: "#1A1B26", foreground: "#C0CAF5", cursor: "#C0CAF5", selectionBackground: "#33467C", black: "#15161E", red: "#F7768E", green: "#9ECE6A", yellow: "#E0AF68", blue: "#7AA2F7", magenta: "#BB9AF7", cyan: "#7DCFFF", white: "#A9B1D6", brightBlack: "#414868", brightRed: "#F7768E", brightGreen: "#9ECE6A", brightYellow: "#E0AF68", brightBlue: "#7AA2F7", brightMagenta: "#BB9AF7", brightCyan: "#7DCFFF", brightWhite: "#C0CAF5" }),
  theme("Catppuccin Mocha", { background: "#1E1E2E", foreground: "#CDD6F4", cursor: "#F5E0DC", selectionBackground: "#45475A", black: "#45475A", red: "#F38BA8", green: "#A6E3A1", yellow: "#F9E2AF", blue: "#89B4FA", magenta: "#F5C2E7", cyan: "#94E2D5", white: "#BAC2DE", brightBlack: "#585B70", brightRed: "#F38BA8", brightGreen: "#A6E3A1", brightYellow: "#F9E2AF", brightBlue: "#89B4FA", brightMagenta: "#F5C2E7", brightCyan: "#94E2D5", brightWhite: "#A6ADC8" }),
  theme("Gruvbox Dark", { background: "#282828", foreground: "#EBDBB2", cursor: "#EBDBB2", selectionBackground: "#504945", black: "#282828", red: "#CC241D", green: "#98971A", yellow: "#D79921", blue: "#458588", magenta: "#B16286", cyan: "#689D6A", white: "#A89984", brightBlack: "#928374", brightRed: "#FB4934", brightGreen: "#B8BB26", brightYellow: "#FABD2F", brightBlue: "#83A598", brightMagenta: "#D3869B", brightCyan: "#8EC07C", brightWhite: "#EBDBB2" })
];
