using System.IO;

namespace Vex.App.Terminal.Native;

/// <summary>Formats inserted paths for the pane's shell; raw insertion is available for application prompts.</summary>
internal static class PathCompletionText
{
    internal static string FormatPath(string path, string shellId, bool raw)
    {
        if (shellId is "gitbash" or "wsl")
        {
            path = path.Replace('\\', '/');
            if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':')
                path = (shellId == "wsl" ? "/mnt/" : "/") + char.ToLowerInvariant(path[0]) + path[2..];
        }
        // Filesystem names may contain newlines or ESC on remote mounts. Never send terminal control input.
        if (path.Any(char.IsControl))
            throw new ArgumentException("Path completion cannot insert control characters.", nameof(path));
        if (raw)
            return path;
        if (path.All(c => char.IsLetterOrDigit(c) || c is '/' or '\\' or ':' or '.' or '_' or '-'))
            return path;
        return shellId switch
        {
            "pwsh" or "powershell" => "'" + path.Replace("'", "''") + "'",
            "nu" => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            "gitbash" or "wsl" => "'" + path.Replace("'", "'\"'\"'") + "'",
            _ => "\"" + path + "\"",
        };
    }

    /// <summary>Resolves typed directory prefixes without doing filesystem IO on the UI thread.</summary>
    internal static string ResolveDirectory(string directory, string prefix)
    {
        if (prefix == "~" || prefix.StartsWith("~\\") || prefix.StartsWith("~/"))
            prefix = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), prefix.Length > 2 ? prefix[2..] : "");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefix.Replace('/', '\\'), directory));
    }

    /// <summary>Recognizes default cmd/PowerShell prompts when no OSC working directory is available.</summary>
    internal static string? ReadPromptDirectory(string line, string shellId)
    {
        if (shellId is not ("system" or "cmd" or "pwsh" or "powershell"))
            return null;
        if (line.StartsWith("PS ", StringComparison.Ordinal))
            line = line[3..];
        var end = line.IndexOf('>');
        if (end < 0)
            return null;
        var path = line[..end];
        return Path.IsPathFullyQualified(path) && path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
               !path.Any(char.IsControl) ? path : null;
    }
}
