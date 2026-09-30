using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Vex.App.Terminal;
using Vex.Terminal;

namespace Vex.App.Model;

/// <summary>
/// Result of evaluating whether a tab or pane needs confirmation before closing.
/// </summary>
public sealed record TabCloseInfo(
    bool NeedsConfirmation,
    string AppName,
    bool IsAgent,
    bool IsDirty,
    AppIcon? Icon,
    string Title,
    string Message
)
{
    public static readonly TabCloseInfo SafeToClose = new(
        NeedsConfirmation: false,
        AppName: "",
        IsAgent: false,
        IsDirty: false,
        Icon: null,
        Title: "",
        Message: ""
    );
}

/// <summary>
/// A single running application or unsaved buffer in a project, displayed when exiting the app.
/// </summary>
public sealed record WorkspaceExitItem(
    string ProjectName,
    string AppName,
    AppIcon? Icon
);

/// <summary>
/// Result of evaluating whether exiting the entire application requires confirmation,
/// containing all running applications from all projects.
/// </summary>
public sealed record WorkspaceExitInfo(
    bool NeedsConfirmation,
    IReadOnlyList<WorkspaceExitItem> Items
)
{
    public static readonly WorkspaceExitInfo SafeToClose = new(false, Array.Empty<WorkspaceExitItem>());
}

/// <summary>
/// Inspects tabs and panes to determine if an active agent, running process,
/// or unsaved file requires user confirmation before closing.
/// Universal for every shell: does not hardcode shell names.
/// </summary>
public static class TabCloseConfirmation
{
    private static readonly FrozenSet<string> KnownAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pi",
        "claude",
        "claudecode",
        "claude-code",
        "agy",
        "antigravity",
        "gemini",
        "googlegemini",
        "oc",
        "opencode",
        "open-code",
        "codex",
        "aider",
        "cursor",
        "copilot",
        "githubcopilot",
        "deepseek"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks whether an application or tool name corresponds to an AI coding agent.
    /// </summary>
    public static bool IsAgent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return KnownAgents.Contains(name) || name.StartsWith("pi-", StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessTree.Index? GetOrRefreshIndex()
    {
        if (AppIconTracker.LatestIndex is { } cached &&
            Environment.TickCount64 - AppIconTracker.LatestIndexTimestamp < 1500)
        {
            return cached;
        }

        var entries = ProcessTree.Snapshot();
        return entries is not null ? ProcessTree.Index.Build(entries) : AppIconTracker.LatestIndex;
    }

    private static TabCloseInfo BuildTerminalDirtyInfo(TerminalPane terminal)
    {
        var clean = terminal.Title.TrimEnd('*');
        var parsed = TerminalTitleFormatter.Parse(terminal.Title);
        var appKey = !string.IsNullOrEmpty(terminal.ActiveProcessName)
            ? terminal.ActiveProcessName
            : parsed.AppName;

        var displayName = ResolveEditorDisplayName(appKey);
        var icon = terminal.AppIcon ?? (!string.IsNullOrEmpty(appKey) ? (AppIconCatalog.Resolve(appKey, null, null) ?? AppIconCatalog.FromProcess(appKey)) : null);
        var title = displayName.Equals("Editor", StringComparison.OrdinalIgnoreCase)
            ? "Unsaved Changes"
            : $"Unsaved Changes in {displayName}";

        return new TabCloseInfo(
            NeedsConfirmation: true,
            AppName: displayName,
            IsAgent: false,
            IsDirty: true,
            Icon: icon,
            Title: title,
            Message: $"\"{clean}\" has unsaved buffer changes. Closing this tab will discard them."
        );
    }

    private static string ResolveEditorDisplayName(string? appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey))
            return "Editor";

        return appKey.ToLowerInvariant() switch
        {
            "nvim" or "neovim" => "Neovim",
            "vim" => "Vim",
            "lazyvim" => "LazyVim",
            "opencode" => "OpenCode",
            _ => Capitalize(appKey)
        };
    }

    private static string Capitalize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        if (name.Length == 1)
            return char.ToUpperInvariant(name[0]).ToString();
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static TabCloseInfo BuildAgentInfo(string toolName, TerminalPane terminal)
    {
        var displayName = char.ToUpperInvariant(toolName[0]) + toolName[1..];
        var icon = terminal.AppIcon ?? AppIconCatalog.ResolveIcon(toolName);
        return new TabCloseInfo(
            NeedsConfirmation: true,
            AppName: displayName,
            IsAgent: true,
            IsDirty: false,
            Icon: icon,
            Title: $"{displayName} is running",
            Message: $"An agent session ({displayName}) is currently active. Closing this tab will terminate it."
        );
    }

    private static TabCloseInfo BuildProcessInfo(string toolName, TerminalPane terminal)
    {
        var icon = terminal.AppIcon ?? AppIconCatalog.ResolveIcon(toolName);
        return new TabCloseInfo(
            NeedsConfirmation: true,
            AppName: toolName,
            IsAgent: false,
            IsDirty: false,
            Icon: icon,
            Title: $"{toolName} is running",
            Message: $"Process \"{toolName}\" is actively running in this tab. Closing will terminate it."
        );
    }

    private static TabCloseInfo BuildTuiInfo(string name, TerminalPane terminal)
    {
        var isAgent = IsAgent(name);
        return new TabCloseInfo(
            NeedsConfirmation: true,
            AppName: name,
            IsAgent: isAgent,
            IsDirty: false,
            Icon: terminal.AppIcon,
            Title: $"{name} is running",
            Message: isAgent
                ? $"An agent session ({name}) is active in full-screen mode. Closing will terminate it."
                : $"\"{name}\" is running in full-screen mode. Closing will terminate it."
        );
    }

    /// <summary>
    /// Inspects all leaf panes in <paramref name="tab"/> to determine if closing
    /// needs user confirmation. Returns immediately using cached pane telemetry
    /// whenever available, avoiding UI-thread hitches.
    /// </summary>
    public static TabCloseInfo GetCloseInfo(WorkspaceTab tab)
    {
        var needsProcessLookup = false;
        TabCloseInfo? firstProcess = null;

        // Pass 1: 0ms fast check on in-memory pane properties
        foreach (var leaf in tab.Leaves)
        {
            if (leaf is TerminalPane terminal)
            {
                if (terminal.IsDirty)
                    return BuildTerminalDirtyInfo(terminal);

                if (terminal.HasActiveProcess && !string.IsNullOrEmpty(terminal.ActiveProcessName))
                {
                    var toolName = terminal.ActiveProcessName;
                    var isAgent = IsAgent(toolName);
                    if (isAgent)
                        return BuildAgentInfo(toolName, terminal);

                    firstProcess ??= BuildProcessInfo(toolName, terminal);
                }
                else if (terminal.State == PaneState.Busy)
                {
                    var name = !string.IsNullOrWhiteSpace(terminal.Title) && !terminal.Title.Equals("Terminal", StringComparison.OrdinalIgnoreCase)
                        ? terminal.Title.TrimEnd('*')
                        : "Full-screen process";
                    firstProcess ??= BuildTuiInfo(name, terminal);
                }
                else
                {
                    needsProcessLookup = true;
                }
            }
        }

        if (firstProcess is not null)
            return firstProcess;

        // Pass 2: If no pane had an active process cached, verify against index
        if (needsProcessLookup)
        {
            var index = GetOrRefreshIndex();
            if (index is not null)
            {
                foreach (var leaf in tab.Leaves)
                {
                    var info = GetCloseInfo(leaf, index);
                    if (!info.NeedsConfirmation)
                        continue;
                    if (info.IsDirty || info.IsAgent)
                        return info;
                    firstProcess ??= info;
                }
            }
        }

        return firstProcess ?? TabCloseInfo.SafeToClose;
    }

    /// <summary>
    /// Inspects all projects and tabs in the workspace to determine if exiting the application
    /// requires user confirmation. Returns all running applications across all projects in a list.
    /// </summary>
    public static WorkspaceExitInfo GetWorkspaceExitInfo(Workspace workspace)
    {
        var index = GetOrRefreshIndex();
        var items = new List<WorkspaceExitItem>();

        foreach (var project in workspace.Projects)
        {
            foreach (var tab in project.Tabs)
            {
                foreach (var leaf in tab.Leaves)
                {
                    var info = GetCloseInfo(leaf, index);
                    if (!info.NeedsConfirmation)
                        continue;

                    items.Add(new WorkspaceExitItem(
                        ProjectName: project.Name,
                        AppName: info.AppName,
                        Icon: info.Icon
                    ));
                }
            }
        }

        return new WorkspaceExitInfo(items.Count > 0, items);
    }

    /// <summary>
    /// Inspects a single leaf pane to determine if closing needs user confirmation.
    /// </summary>
    public static TabCloseInfo GetCloseInfo(LeafPane leaf)
    {
        return GetCloseInfo(leaf, GetOrRefreshIndex());
    }

    internal static TabCloseInfo GetCloseInfo(LeafPane leaf, ProcessTree.Index? index)
    {
        if (leaf is TerminalPane terminal)
        {
            // 1. Neovim/Vim buffer with unsaved changes
            if (terminal.IsDirty)
                return BuildTerminalDirtyInfo(terminal);

            // 2. Cached active process from TerminalPane tracker
            if (terminal.HasActiveProcess && !string.IsNullOrEmpty(terminal.ActiveProcessName))
            {
                var toolName = terminal.ActiveProcessName;
                var isAgent = IsAgent(toolName);
                return isAgent ? BuildAgentInfo(toolName, terminal) : BuildProcessInfo(toolName, terminal);
            }

            // 3. Alternate screen buffer active (TUI like htop/vim)
            if (terminal.State == PaneState.Busy)
            {
                var name = !string.IsNullOrWhiteSpace(terminal.Title) && !terminal.Title.Equals("Terminal", StringComparison.OrdinalIgnoreCase)
                    ? terminal.Title.TrimEnd('*')
                    : "Full-screen process";
                return BuildTuiInfo(name, terminal);
            }

            // 4. Universal check: does the shell at ProcessId have any non-helper child process running?
            if (terminal.ProcessId is { } pid && index is not null)
            {
                var childProcess = ProcessTree.DeepestChildProcess(index, (uint)pid);
                if (childProcess is { } proc)
                {
                    string toolName;
                    if (AppIconCatalog.IsShimHost(proc.Name))
                    {
                        var cmdLine = ProcessCommandLine.Get(proc.Pid);
                        toolName = AppIconCatalog.ResolveToolName(proc.Name, cmdLine) ?? proc.Name;
                    }
                    else
                    {
                        toolName = proc.Name;
                    }

                    var isAgent = IsAgent(toolName);
                    return isAgent ? BuildAgentInfo(toolName, terminal) : BuildProcessInfo(toolName, terminal);
                }
            }

            // 5. Title-based agent detection: ONLY if the title specifically names a KNOWN agent (e.g. "pi", "claude")
            // NEVER trigger on arbitrary folder names like "Vex" or directory paths!
            var parsed = TerminalTitleFormatter.Parse(terminal.Title);
            if (!string.IsNullOrEmpty(parsed.AppName) && IsAgent(parsed.AppName))
            {
                return BuildAgentInfo(parsed.AppName, terminal);
            }
        }

        return TabCloseInfo.SafeToClose;
    }
}
