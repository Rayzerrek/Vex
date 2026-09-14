using System.Reflection;

namespace Vex.App.Model;

/// <summary>
/// The single source of truth for the version shown in the UI. It reads the
/// assembly metadata that the SDK fills from &lt;Version&gt; in Vex.App.csproj,
/// so a release only has to bump that one property — and Package.wxs — instead
/// of also hunting down hardcoded strings in XAML.
/// </summary>
public static class AppInfo
{
    /// <summary>Semantic version without the build suffix, e.g. "1.2.1".</summary>
    public static string Version { get; } = Resolve();

    /// <summary>"Vex 1.2.1", the form used in the settings sidebar.</summary>
    public static string VersionLabel { get; } = $"Vex {Version}";

    private static string Resolve()
    {
        var assembly = typeof(AppInfo).Assembly;

        // InformationalVersion carries any suffix (e.g. "+abc123"); the product
        // version is the clean semantic number the release is named after.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
