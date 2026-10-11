using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Vex.Terminal;

namespace Vex.App.Model;

/// <summary>Reads the foreground Linux application for one tagged WSL terminal, without relying on OSC titles.</summary>
internal static class WslProcessTracker
{
    internal const string SessionVariable = "VEX_WSL_SESSION=";

    // One grep scans all environments; spawning one subprocess per Linux
    // process made every pane's periodic probe contend with terminal work.
    private const string ProcessQuery = """
        root=0
        for path in $(grep -lzFx "VEX_WSL_SESSION=$1" /proc/[0-9]*/environ 2>/dev/null); do
            pid=${path#/proc/}; pid=${pid%/environ}
            if [ "$root" -eq 0 ] || [ "$pid" -lt "$root" ]; then root=$pid; fi
        done
        [ "$root" -gt 0 ] || exit 1
        printf '%s\n' "$root"
        ps -eo pid=,ppid=,pgid=,tpgid=,comm=,args= -ww
        """;

    /// <summary>Extracts only a valid per-session launch token from the Windows WSL command line.</summary>
    internal static string? SessionToken(string? commandLine)
    {
        if (commandLine is null)
            return null;
        var start = commandLine.IndexOf(SessionVariable, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var token = commandLine.AsSpan(start + SessionVariable.Length);
        return token.Length >= 32 && Guid.TryParseExact(token[..32], "N", out _)
            ? token[..32].ToString() : null;
    }

    /// <summary>Runs on the background icon tracker; a failed or timed-out probe leaves title-based detection available.</summary>
    internal static (string Name, string CommandLine)? ReadForegroundProcess(string token, string? distribution)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (distribution is not null)
        {
            start.ArgumentList.Add("--distribution");
            start.ArgumentList.Add(distribution);
        }
        foreach (var argument in new[] { "--exec", "sh", "-c", ProcessQuery.Replace("\r", ""), "vex-process-query", token })
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            {
                process.Kill();
                process.WaitForExit();
                return null;
            }
            var text = output.GetAwaiter().GetResult();
            _ = errors.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? ParseForegroundProcess(text) : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Parses Linux process metadata, excluding background jobs and retaining editors over their workers.</summary>
    internal static (string Name, string CommandLine)? ParseForegroundProcess(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || !uint.TryParse(lines[0], out var rootPid))
            return null;
        var processes = new List<(uint Pid, uint ParentPid, uint Group, int ForegroundGroup, string Name, string CommandLine)>();
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 6 || !uint.TryParse(fields[0], out var pid) ||
                !uint.TryParse(fields[1], out var parent) || !uint.TryParse(fields[2], out var group) ||
                !int.TryParse(fields[3], out var foreground))
                continue;
            processes.Add((pid, parent, group, foreground, fields[4], fields[5]));
        }
        var shell = processes.Find(process => process.Pid == rootPid);
        if (shell.Pid == 0 || shell.ForegroundGroup <= 0 || shell.Group == shell.ForegroundGroup)
            return null;
        var foregroundProcesses = processes.Where(process => process.Pid == rootPid || process.Group == shell.ForegroundGroup).ToArray();
        var index = ProcessTree.Index.Build(foregroundProcesses.Select(process => (process.Pid, process.ParentPid, process.Name)).ToArray());
        if (index is null || ProcessTree.DeepestDescendant(index, rootPid, AppIconCatalog.ConsoleHelpers, AppIconCatalog.EditorProcessNames) is not { } app || app.Pid == rootPid)
            return null;
        var selected = foregroundProcesses.First(process => process.Pid == app.Pid);
        return (selected.Name, selected.CommandLine);
    }
}
