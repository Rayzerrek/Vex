using System.IO;
using System.Text.Json.Serialization;

namespace Vex.App.Model;

/// <summary>
/// One launchable shell: what the settings UI lists and what a terminal pane
/// spawns. Built-ins come from <see cref="ShellRegistry"/> detection; custom
/// ones are user-entered programs with optional arguments and are the only
/// kind persisted in settings.
/// </summary>
public sealed class ShellProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Program { get; set; } = "";
    public string Arguments { get; set; } = "";

    /// <summary>Provided by the app or discovered on this machine; custom
    /// profiles are false and this never needs to reach disk.</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }
}

public static class ShellRegistry
{
    /// <summary>Pseudo-profile meaning "whatever the OS login shell is";
    /// resolved at spawn time so a later-installed default is picked up.</summary>
    public const string SystemDefaultId = "system";

    private static readonly Lazy<IReadOnlyList<ShellProfile>> _detected =
        new(DetectInstalled);

    /// <summary>Shells found on this machine, best-first order. Cheap
    /// existence probes, so the result is computed once and cached.</summary>
    public static IReadOnlyList<ShellProfile> Detected() => _detected.Value;

    public static bool HasDetected(string id) => _detected.Value.Any(p => p.Id == id);

    /// <summary>
    /// Maps a shell id to the program and arguments a pane should spawn.
    /// Null means fall back to the OS default: either the id is the system
    /// default, or the configured shell no longer exists on this machine.
    /// Runs on the pane-spawn path, so it must stay cheap: no PATH scan and
    /// no full detection sweep — at most a couple of File.Exists probes.
    /// </summary>
    private static readonly Dictionary<string, (string Program, string Arguments)> _resolvedCache = new();

    public static (string Program, string Arguments)? Resolve(string? shellId)
    {
        if (string.IsNullOrEmpty(shellId) || shellId == SystemDefaultId)
            return null;

        lock (_resolvedCache)
        {
            if (_resolvedCache.TryGetValue(shellId, out var cached))
                return cached;
        }

        (string Program, string Arguments)? result = null;
        switch (shellId)
        {
            case "nu":
                var nuPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "scoop", "apps", "nu", "current", "nu.exe");
                result = (File.Exists(nuPath) ? nuPath : FindOnPath("nu.exe") ?? "nu.exe", "");
                break;
            case "pwsh":
            case "powershell":
                var exe = PowerShellPath(shellId == "pwsh");
                result = exe is null ? null : (exe, "");
                break;
            default:
                var custom = AppSettings.Instance.CustomShells.FirstOrDefault(p => p.Id == shellId);
                if (custom is not null && !string.IsNullOrWhiteSpace(custom.Program))
                {
                    // If custom program is a bare name, try to resolve it so CreateProcessW doesn't stall.
                    var prog = custom.Program;
                    if (!prog.Contains('\\') && !prog.Contains('/'))
                    {
                        var resolvedPath = FindOnPath(prog);
                        if (resolvedPath != null)
                            prog = resolvedPath;
                    }
                    result = (prog, custom.Arguments);
                }
                break;
        }

        if (result.HasValue && shellId is "nu" or "pwsh" or "powershell")
        {
            lock (_resolvedCache)
            {
                _resolvedCache[shellId] = result.Value;
            }
        }

        return result;
    }

    private static string? PowerShellPath(bool pwsh)
    {
        if (pwsh)
        {
            var pwshPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe");
            return File.Exists(pwshPath) ? pwshPath : null;
        }
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    /// <summary>Older settings stored a display name instead of an id.</summary>
    public static string MigrateLegacyName(string? legacyName) => legacyName switch
    {
        // Runs while settings load, so it must not trigger the full
        // detection sweep — a single File.Exists probe at most.
        "PowerShell" => PowerShellPath(pwsh: true) is null ? "powershell" : "pwsh",
        "Nushell" => "nu",
        _ => SystemDefaultId,
    };

    private static List<ShellProfile> DetectInstalled()
    {
        var found = new List<ShellProfile>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

        void Add(string id, string name, string? path, string arguments = "")
        {
            // First hit wins: earlier probes shadow later duplicates.
            if (path is not null && File.Exists(path) && !found.Any(p => p.Id == id))
                found.Add(new ShellProfile { Id = id, Name = name, Program = path, Arguments = arguments, IsBuiltIn = true });
        }

        Add("pwsh", "PowerShell 7", Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        Add("nu", "Nushell", FindOnPath("nu.exe"));
        Add("powershell", "Windows PowerShell",
            Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe"));
        Add("cmd", "Command Prompt", Path.Combine(system32, "cmd.exe"));

        // Git Bash ships in three usual roots depending on installer scope.
        var localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        foreach (var root in new[] { programFiles,
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     localPrograms })
            Add("gitbash", "Git Bash", Path.Combine(root, "Git", "bin", "bash.exe"), "--login -i");

        Add("wsl", "WSL", Path.Combine(system32, "wsl.exe"));

        return found;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; keep scanning the rest.
            }
        }
        return null;
    }
}
