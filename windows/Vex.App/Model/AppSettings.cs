using System.IO;
using System.Text.Json;

namespace Vex.App.Model;

public class AppSettings : ObservableObject
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vex", "settings.json");

    private static AppSettings? _instance;
    public static AppSettings Instance => _instance ??= Load();

    private string _themeName = "Vex Dark";
    public string ThemeName
    {
        get => _themeName;
        set { if (Set(ref _themeName, value)) Save(); }
    }

    public string[] AvailableFonts { get; } = System.Windows.Media.Fonts.SystemFontFamilies
        .Select(f => f.Source)
        .OrderBy(f => f)
        .ToArray();

    private string _fontFamily = "Cascadia Mono";
    public string FontFamily
    {
        get => _fontFamily;
        set { if (Set(ref _fontFamily, value)) Save(); }
    }

    private int _fontSize = 14;
    public int FontSize
    {
        get => _fontSize;
        set { if (Set(ref _fontSize, value)) Save(); }
    }

    private bool _cursorBlink = true;
    public bool CursorBlink
    {
        get => _cursorBlink;
        set { if (Set(ref _cursorBlink, value)) Save(); }
    }

    public string[] AvailableShells { get; } = { "PowerShell", "Nushell" };

    private string _shell = "Nushell";
    public string Shell
    {
        get => _shell;
        set { if (Set(ref _shell, value)) Save(); }
    }

    public string[] AvailableTerminalBackends { get; } = { "Native", "xterm.js" };

    private string _terminalBackend = "Native";
    /// <summary>
    /// Terminal rendering backend: the native WPF renderer (XtermSharp core)
    /// or xterm.js hosted in WebView2. Applies to panes created afterwards.
    /// </summary>
    public string TerminalBackend
    {
        get => _terminalBackend;
        set { if (Set(ref _terminalBackend, value)) Save(); }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                    return settings;
            }
        }
        catch
        {
            // Fallback to defaults
        }
        return new AppSettings();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null)
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }
}
