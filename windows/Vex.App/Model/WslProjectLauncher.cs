using System.Diagnostics;
using System.IO;
using System.Text;

namespace Vex.App.Model;

/// <summary>Resolves WSL projects through Linux, without shell shortcuts or guessing the user's home directory.</summary>
internal static class WslProjectLauncher
{
    private const string DirectoryQuery = """
        target=$1
        case "$target" in
            '~'|'') target=$HOME ;;
            '~/'*) target=$HOME/${target#\~/} ;;
            /*) ;;
            *) printf 'Use ~ or an absolute Linux directory.\n' >&2; exit 1 ;;
        esac
        cd "$target" || exit 1
        printf '%s\n' "$WSL_DISTRO_NAME"
        pwd -P
        """;

    private const string ShellQuery = """
        login=${SHELL:-/bin/sh}
        if [ -x "$login" ]; then printf '%s\n' "$login"; else printf '\n'; fi
        for shell in bash zsh fish nu pwsh sh; do
            path=$(command -v "$shell") || continue
            [ -x "$path" ] && printf '%s\n' "$path"
        done
        if [ -r /etc/shells ]; then
            while IFS= read -r path || [ -n "$path" ]; do
                [ -x "$path" ] && printf '%s\n' "$path"
            done < /etc/shells
        fi
        :
        """;

    /// <summary>Lists installed distributions only; listing does not boot each distribution.</summary>
    internal static async Task<string[]> ListDistributionsAsync(CancellationToken cancellationToken) =>
        ParseDistributions(await RunWslAsync(["--list", "--quiet"], Encoding.Unicode, cancellationToken).ConfigureAwait(false));

    /// <summary>Resolves Home in WSL's default distribution as its default Linux user.</summary>
    internal static async Task<string> ResolveDefaultHomeAsync(CancellationToken cancellationToken) =>
        ParseProjectDirectory(await RunWslAsync(["--exec", "sh", "-c", DirectoryQuery.Replace("\r", ""), "vex-project", "~"],
            Encoding.UTF8, cancellationToken).ConfigureAwait(false));

    /// <summary>Discovers installed Linux shells without confusing them with Windows shell profiles.</summary>
    internal static async Task<ShellProfile[]> ListShellsAsync(string? distribution, CancellationToken cancellationToken)
    {
        var arguments = new List<string>();
        if (distribution is not null) { arguments.Add("--distribution"); arguments.Add(distribution); }
        arguments.AddRange(["--exec", "sh", "-c", ShellQuery.Replace("\r", "")]);
        return ParseShells(await RunWslAsync(arguments.ToArray(), Encoding.UTF8, cancellationToken).ConfigureAwait(false));
    }

    internal static ShellProfile[] ParseShells(string text)
    {
        var loginPath = text.Split('\n')[0].TrimEnd('\r');
        var loginProgram = ShellRegistry.WslExecutable("wsl:" + loginPath) ?? "";
        var result = new List<ShellProfile> { new() { Id = "wsl", Name = "Default Linux shell", Program = loginProgram, IsBuiltIn = true } };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var id = "wsl:" + path;
            var name = Path.GetFileName(path);
            if (name is not ("bash" or "zsh" or "fish" or "nu" or "pwsh" or "sh" or "dash" or "ksh" or "csh" or "tcsh") ||
                !ShellRegistry.IsWsl(id) || !names.Add(name)) continue;
            result.Add(new ShellProfile { Id = id, Name = name + " · " + path, IsBuiltIn = true });
        }
        return result.ToArray();
    }

    /// <summary>Checks the directory as the distribution's default user and returns its persistent WSL network path.</summary>
    internal static async Task<string> ResolveDirectoryAsync(string distribution, string directory, CancellationToken cancellationToken)
    {
        if (!IsDistributionName(distribution)) throw new ArgumentException("Invalid WSL distribution name.", nameof(distribution));
        if (directory.Any(char.IsControl)) throw new ArgumentException("Invalid WSL directory.", nameof(directory));
        var text = await RunWslAsync(["--distribution", distribution, "--exec", "sh", "-c", DirectoryQuery.Replace("\r", ""), "vex-project", directory],
            Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return ParseProjectDirectory(text, distribution);
    }

    internal static string[] ParseDistributions(string text)
    {
        var names = text.Replace("\0", "").TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Any(name => !IsDistributionName(name))) throw new FormatException("Invalid WSL distribution list.");
        return names.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static string ParseProjectDirectory(string text, string? distribution = null)
    {
        var lines = text.TrimEnd('\r', '\n').Split('\n');
        var resolvedDistribution = lines[0].TrimEnd('\r');
        if (lines.Length != 2 || !IsDistributionName(resolvedDistribution) ||
            (distribution is not null && resolvedDistribution != distribution))
            throw new FormatException("Invalid WSL project response.");
        var path = lines[1].TrimEnd('\r');
        if (!path.StartsWith('/') || path.Contains('\\') || path.Any(char.IsControl))
            throw new FormatException("Invalid WSL project directory.");
        return WslShellPaths.ToNetworkPath(resolvedDistribution, path);
    }

    private static bool IsDistributionName(string name) => name.Length > 0 && !name.Any(char.IsControl) && name.IndexOfAny(['/', '\\', ':']) < 0;

    private static async Task<string> RunWslAsync(string[] arguments, Encoding outputEncoding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = await Task.Run(() => Process.Start(start), cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("WSL project query could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            var error = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("WSL project query failed: " + error.Trim());
            return text;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (!cancellationToken.IsCancellationRequested) throw new IOException("WSL did not respond within 15 seconds. Try again once the distribution has started.");
            throw;
        }
    }
}
