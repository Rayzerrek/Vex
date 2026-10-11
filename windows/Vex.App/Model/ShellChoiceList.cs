using System.IO;

namespace Vex.App.Model;

/// <summary>Curates installed shell choices without adding default aliases or hiding the project's chosen shell.</summary>
internal static class ShellChoiceList
{
    internal sealed record MenuChoices(IReadOnlyList<ShellProfile> Primary, IReadOnlyList<ShellProfile> More);

    /// <summary>Names the actual Windows default without offering it twice under two profile ids.</summary>
    internal static ShellProfile[] Windows(IEnumerable<ShellProfile> installed, string defaultShellId, string systemProgram)
    {
        return installed.Select(profile => defaultShellId == ShellRegistry.SystemDefaultId && profile.IsBuiltIn
            && string.Equals(Path.GetFileName(profile.Program), Path.GetFileName(systemProgram), StringComparison.OrdinalIgnoreCase)
                ? new ShellProfile { Id = ShellRegistry.SystemDefaultId, Name = profile.Name, Program = profile.Program, IsBuiltIn = true }
                : profile).ToArray();
    }

    /// <summary>Linux login shells and executable aliases count as one choice; compatibility shells stay out of the picker.</summary>
    internal static ShellProfile[] Linux(IEnumerable<ShellProfile> installed, string defaultShellId)
    {
        return installed.Where(profile => ShellRegistry.IsWsl(profile.Id) && Executable(profile).Length > 0)
            .Where(profile => profile.Id == "wsl" || profile.Id == defaultShellId || Path.GetFileName(Executable(profile)) is not ("sh" or "dash"))
            .OrderByDescending(profile => profile.Id == defaultShellId)
            .DistinctBy(profile => Path.GetFileName(Executable(profile)), StringComparer.Ordinal)
            .Select(profile => new ShellProfile { Id = profile.Id, Name = Name(profile), Program = Executable(profile), IsBuiltIn = true })
            .ToArray();
    }

    /// <summary>Keeps at most three common choices visible; all remaining installed profiles stay under More shells.</summary>
    internal static MenuChoices ForMenu(IReadOnlyList<ShellProfile> available, string defaultShellId)
    {
        var powershellId = available.Any(profile => profile.Id == "pwsh") ? "pwsh" : "powershell";
        var primary = available.Where(profile => profile.Id == defaultShellId)
            .Concat(available.Where(profile => ShellRegistry.IsWsl(profile.Id)
                ? Path.GetFileName(Executable(profile)) is "bash" or "zsh" or "fish" or "nu" or "pwsh"
                : profile.Id == powershellId || profile.Id == "nu"))
            .DistinctBy(profile => profile.Id).Take(3).ToArray();
        if (primary.Length == 0) primary = available.Take(1).ToArray();
        var primaryIds = primary.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        return new(primary, available.Where(profile => !primaryIds.Contains(profile.Id)).ToArray());
    }

    internal static string Name(ShellProfile profile) => ShellRegistry.IsWsl(profile.Id)
        ? Path.GetFileName(Executable(profile)) switch
        {
            "bash" => "Bash", "zsh" => "Zsh", "fish" => "Fish", "nu" => "Nushell", "pwsh" => "PowerShell",
            "" => "Linux shell", var name => name,
        } : profile.Name;

    internal static string Executable(ShellProfile profile) => ShellRegistry.WslExecutable(profile.Id) ?? profile.Program;
}
