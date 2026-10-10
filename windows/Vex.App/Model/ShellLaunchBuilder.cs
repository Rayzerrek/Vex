using System.IO;
using System.Text;
using Vex.Terminal;

namespace Vex.App.Model;

/// <summary>Builds the same shell integration launch for direct startup and prewarmed sessions.</summary>
internal static class ShellLaunchBuilder
{
    private const string PowerShellIntegration = """
        $global:__vexOriginalPrompt = $function:prompt
        $global:__vexCommandRunning = $false
        function global:prompt {
            $ok = $?
            $code = if ($ok) { 0 } elseif ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }
            $esc = [char]27
            if ($global:__vexCommandRunning) {
                [Console]::Write("$esc]133;D;$code" + [char]7)
                $global:__vexCommandRunning = $false
            }
            [Console]::Write("$esc]133;A" + [char]7)
            if ($PWD.Provider.Name -eq 'FileSystem') {
                $uri = ([uri]$PWD.ProviderPath).AbsoluteUri
                [Console]::Write("$esc]7;$uri" + [char]7)
            }
            if ($global:__vexOriginalPrompt) { & $global:__vexOriginalPrompt } else { "PS $PWD> " }
        }
        try {
            Import-Module PSReadLine -ErrorAction Stop
            $global:__vexHistoryHandler = (Get-PSReadLineOption).AddToHistoryHandler
            Set-PSReadLineOption -AddToHistoryHandler {
                param($line)
                if (-not [string]::IsNullOrWhiteSpace($line)) {
                    $global:__vexCommandRunning = $true
                    [Console]::Write([char]27 + ']133;C' + [char]7)
                }
                if ($global:__vexHistoryHandler) { $global:__vexHistoryHandler.Invoke($line) } else { $true }
            }
        } catch { }
        """;

    internal static (string Program, string Arguments) BuildShellLaunch(string? shellId, string? initialCommand = null, string? workingDirectory = null)
    {
        var resolved = ShellRegistry.Resolve(shellId);
        var program = resolved?.Program ?? TerminalSession.DefaultShell();
        var arguments = resolved?.Arguments ?? "";
        if (shellId == "wsl" && resolved is not null && workingDirectory is not null)
        {
            var network = WslShellPaths.ParseNetworkPath(workingDirectory);
            if (network is { } folder)
            {
                // WSL preserves unnecessary quotes in the distribution name.
                var distribution = folder.Distribution;
                arguments += " --distribution " + (distribution.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
                    ? distribution : QuoteWindowsArgument(distribution));
            }
            arguments += " --cd " + QuoteWindowsArgument(WslShellPaths.ToLinuxPath(workingDirectory));
            return (program, arguments.TrimStart());
        }
        if (resolved is null && shellId is "pwsh" or "powershell")
            shellId = "system";
        if (shellId is not (null or "system" or "cmd" or "pwsh" or "powershell" or "nu"))
            return (program, arguments);
        if (shellId is "pwsh" or "powershell")
        {
            var script = PowerShellIntegration;
            if (!string.IsNullOrWhiteSpace(initialCommand))
                script += "\n$global:__vexCommandRunning = $true\n[Console]::Write([char]27 + ']133;C' + [char]7)\n" + initialCommand;
            return (program, "-NoLogo -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        }
        var integrationDirectory = Path.Combine(AppContext.BaseDirectory, "ShellIntegration");
        if (shellId == "nu")
            return (program, BuildNushellArguments(Path.Combine(integrationDirectory, "Vex.nu"), initialCommand));
        var bootstrap = Path.Combine(integrationDirectory, "Vex.cmd");
        var command = "call \"" + bootstrap + "\"";
        if (!string.IsNullOrWhiteSpace(initialCommand)) command += " & " + initialCommand;
        return (program, "/q /k \"" + command + "\"");
    }

    internal static bool RunsInitialCommandAtStartup(string? shellId) => shellId is null or "system" or "cmd" or "pwsh" or "powershell" or "nu";

    /// <summary>Quotes the integration path for Nushell, then quotes its complete script for Windows argv.</summary>
    internal static string BuildNushellArguments(string integrationScriptPath, string? initialCommand)
    {
        var path = integrationScriptPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script = "source \"" + path + "\"";
        if (!string.IsNullOrWhiteSpace(initialCommand))
        {
            // Startup commands bypass Nu's REPL exit markers. Catch failures
            // before the next prompt resets LAST_EXIT_CODE, preserving their output.
            script += "; print -n $\"(char --integer 27)]133;C(char --integer 7)\"; try {\n" + initialCommand + "\n} catch {|error| print -e $error.rendered; print -n $\"(char --integer 27)]133;D;($error.exit_code? | default 1)(char --integer 7)\" }";
        }
        return "--execute " + QuoteWindowsArgument(script);
    }

    private static string QuoteWindowsArgument(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            result.Append(character);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
