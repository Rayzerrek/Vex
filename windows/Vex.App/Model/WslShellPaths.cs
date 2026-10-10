namespace Vex.App.Model;

/// <summary>Translates WSL network folders without probing or starting a Linux distribution.</summary>
internal static class WslShellPaths
{
    /// <summary>WSL launcher and transport processes belong to the shell, not to user commands.</summary>
    internal static readonly IReadOnlySet<string> ShellHelpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "wsl", "wslhost", "wslrelay", "wslservice", "conhost", "OpenConsole",
    };

    internal static (string Distribution, string LinuxPath)? ParseNetworkPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        foreach (var host in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            if (!normalized.StartsWith(host, StringComparison.OrdinalIgnoreCase))
                continue;
            var rest = normalized[host.Length..];
            var separator = rest.IndexOf('\\');
            var distribution = separator < 0 ? rest : rest[..separator];
            if (distribution.Length == 0)
                return null;
            return (distribution, separator < 0 ? "/" : rest[separator..].Replace('\\', '/'));
        }
        return null;
    }

    internal static string ToLinuxPath(string path)
    {
        if (ParseNetworkPath(path) is { } network)
            return network.LinuxPath;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
        return path.Replace('\\', '/');
    }
}
