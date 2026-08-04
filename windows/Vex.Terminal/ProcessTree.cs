using Vex.Terminal.Native;

namespace Vex.Terminal;

/// <summary>
/// Reads the live process tree of a terminal session from a Toolhelp32
/// snapshot. The shell spawned by a pane (e.g. pwsh.exe) is the root; the
/// app running inside the pane is the deepest non-shell descendant, which is
/// what the tab icon should reflect. PID order breaks ties: Windows allocates
/// PIDs roughly monotonically, so the highest PID at a given depth was
/// started last.
/// </summary>
public static class ProcessTree
{
    /// <summary>
    /// Finds the deepest process under <paramref name="rootPid"/> that is not
    /// in <paramref name="excludedNames"/>, preferring the highest PID on ties.
    /// Returns the process name without extension and its PID, or null when
    /// the snapshot fails or the root no longer exists.
    /// </summary>
    public static (string Name, uint Pid)? DeepestDescendant(uint rootPid, IReadOnlySet<string> excludedNames)
    {
        var entries = Snapshot();
        if (entries is null)
            return null;

        var children = new Dictionary<uint, List<(string Name, uint Pid)>>();
        foreach (var e in entries)
        {
            if (!children.TryGetValue(e.ParentPid, out var list))
                children[e.ParentPid] = list = new List<(string, uint)>();
            list.Add((e.Name, e.Pid));
        }

        if (!children.TryGetValue(rootPid, out var rootChildren))
            return null;

        // Breadth-first over the tree; each level keeps the best candidate.
        (string Name, uint Pid)? best = null;
        (string Name, uint Pid)? firstExcluded = null;
        var frontier = new Queue<(string Name, uint Pid)>(rootChildren);
        while (frontier.Count > 0)
        {
            var next = new List<(string Name, uint Pid)>();
            foreach (var node in frontier)
            {
                if (!excludedNames.Contains(node.Name))
                    best = best is { } b ? (node.Pid > b.Pid ? node : b) : node;
                else if (firstExcluded is null)
                    firstExcluded = node;
                if (children.TryGetValue(node.Pid, out var kids))
                    next.AddRange(kids);
            }
            // A deeper level exists only if some node in it is not excluded;
            // otherwise the deepest non-excluded candidate stands.
            if (next.Any(k => !excludedNames.Contains(k.Name)))
                frontier = new Queue<(string Name, uint Pid)>(next);
            else
                break;
        }

        // No non-shell process at all: fall back to the shell itself, then to
        // the root, so the pane still gets its shell icon.
        if (best is { } winner)
            return winner;
        if (firstExcluded is { } shell)
            return shell;
        return ProcessName(rootPid) is { } rootName ? (rootName, rootPid) : null;
    }

    /// <summary>Process name (no extension) for a PID, or null.</summary>
    public static string? ProcessName(uint pid)
    {
        var entries = Snapshot();
        if (entries is null)
            return null;
        foreach (var e in entries)
        {
            if (e.Pid == pid)
                return e.Name;
        }
        return null;
    }

    private static List<(uint Pid, uint ParentPid, string Name)>? Snapshot()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero)
            return null;

        try
        {
            var result = new List<(uint, uint, string)>();
            var entry = new NativeMethods.PROCESSENTRY32
            {
                dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESSENTRY32>(),
            };
            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
                return result.Count > 0 ? result : null;
            do
            {
                // szExeFile carries the extension (and sometimes a full path);
                // the catalog keys are bare names, so normalize here.
                var name = System.IO.Path.GetFileNameWithoutExtension(entry.szExeFile);
                result.Add((entry.th32ProcessID, entry.th32ParentProcessID, name));
            } while (NativeMethods.Process32NextW(snapshot, ref entry));
            return result;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }
}
