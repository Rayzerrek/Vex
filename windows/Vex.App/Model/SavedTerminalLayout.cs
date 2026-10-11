using System.IO;
using System.Windows.Controls;

namespace Vex.App.Model;

/// <summary>A saved tab layout launches commands only when explicitly opened by the user.</summary>
public sealed class SavedTerminalLayout
{
    public string Name { get; set; } = "";
    public SavedLayoutPane Root { get; set; } = new();
}

/// <summary>One saved split or terminal; directories inside a project are stored relative to its root.</summary>
public sealed class SavedLayoutPane
{
    public string? Orientation { get; set; }
    public double Ratio { get; set; } = 0.5;
    public SavedLayoutPane? First { get; set; }
    public SavedLayoutPane? Second { get; set; }
    public string Directory { get; set; } = ".";
    public string Command { get; set; } = "";
    public string? ShellId { get; set; }
    public bool IsFocused { get; set; }
}

/// <summary>Captures, validates and opens bounded saved layouts without realizing terminal views during editing.</summary>
public static class SavedTerminalLayouts
{
    public const int MaxLayouts = 32;
    private const int MaxLayoutPanes = 32;
    private const int MaxLayoutDepth = MaxLayoutPanes - 1;

    public static SavedTerminalLayout CaptureLayout(WorkspaceTab tab, string projectDirectory) => new()
    {
        Name = tab.Title,
        Root = CaptureLayoutPane(tab.Root, projectDirectory),
    };

    private static SavedLayoutPane CaptureLayoutPane(PaneNode node, string projectDirectory) => node switch
    {
        SplitPane split => new()
        {
            Orientation = split.Orientation == Orientation.Horizontal ? "Horizontal" : "Vertical",
            Ratio = split.Ratio,
            First = CaptureLayoutPane(split.First, projectDirectory),
            Second = CaptureLayoutPane(split.Second, projectDirectory),
        },
        TerminalPane terminal => new()
        {
            Directory = Path.GetRelativePath(projectDirectory, terminal.WorkingDirectory),
            Command = terminal.InitialCommand ?? "",
            ShellId = terminal.ShellId,
            IsFocused = terminal.IsFocused,
        },
        _ => new(),
    };

    public static IEnumerable<SavedLayoutPane> EnumerateLayoutPanes(SavedLayoutPane root)
    {
        if (root.Orientation is not null && root.First is { } first && root.Second is { } second)
        {
            foreach (var pane in EnumerateLayoutPanes(first)) yield return pane;
            foreach (var pane in EnumerateLayoutPanes(second)) yield return pane;
        }
        else yield return root;
    }

    /// <summary>Checks saved layout structure and text limits before allowing any command to launch.</summary>
    public static bool ValidateLayout(SavedTerminalLayout? layout)
    {
        if (layout is null || string.IsNullOrWhiteSpace(layout.Name) || layout.Name.Length > 64 || layout.Name.Any(char.IsControl))
            return false;
        var count = 0;
        return ValidateLayoutPane(layout.Root, 0, ref count);
    }

    private static bool ValidateLayoutPane(SavedLayoutPane? pane, int depth, ref int count)
    {
        if (pane is null || depth > MaxLayoutDepth || !double.IsFinite(pane.Ratio)) return false;
        if (pane.Orientation is not null)
            return pane.Orientation is "Horizontal" or "Vertical"
                && ValidateLayoutPane(pane.First, depth + 1, ref count) && ValidateLayoutPane(pane.Second, depth + 1, ref count);
        return ++count <= MaxLayoutPanes && pane.First is null && pane.Second is null
            && pane.Directory is { Length: > 0 and <= 1024 } && !pane.Directory.Any(char.IsControl)
            && pane.Command is { Length: <= 4096 } && !pane.Command.Contains('\0')
            && (pane.ShellId is null || ShellRegistry.ValidShellId(pane.ShellId) is not null);
    }

    /// <summary>Returns the pane tree only after every directory has been verified; commands are not executed here.</summary>
    internal static PaneNode CreateLayoutPaneTree(SavedLayoutPane pane, string projectDirectory, string? defaultShellId = null)
    {
        if (pane is { Orientation: { } orientation, First: { } first, Second: { } second })
            return new SplitPane(orientation == "Horizontal" ? Orientation.Horizontal : Orientation.Vertical,
                CreateLayoutPaneTree(first, projectDirectory, defaultShellId), CreateLayoutPaneTree(second, projectDirectory, defaultShellId)) { Ratio = pane.Ratio };
        var directory = Path.GetFullPath(pane.Directory, projectDirectory);
        var shellId = pane.ShellId ?? defaultShellId ?? WslShellPaths.ShellIdForDirectory(AppSettings.Instance.ShellId, directory);
        if (!ShellRegistry.IsWsl(shellId) && WslShellPaths.ParseNetworkPath(directory) is not null)
            throw new IOException("Saved layout Windows shell requires a Windows directory: " + directory);
        if (!System.IO.Directory.Exists(directory))
            throw new DirectoryNotFoundException("Saved layout directory does not exist: " + directory);
        return new TerminalPane(directory, string.IsNullOrWhiteSpace(pane.Command) ? null : pane.Command,
            shellId) { IsFocused = pane.IsFocused };
    }
}
