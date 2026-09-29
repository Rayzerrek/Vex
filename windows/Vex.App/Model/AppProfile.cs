using System.IO;

namespace Vex.App.Model;

/// <summary>Resolves the app profile directory; VEX_PROFILE_DIR isolates diagnostic runs.</summary>
internal static class AppProfile
{
    internal static string DirectoryPath { get; } =
        Environment.GetEnvironmentVariable("VEX_PROFILE_DIR") is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vex");
}
