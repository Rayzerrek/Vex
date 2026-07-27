using System.IO;
using System.Text.Json;

namespace Kero.App.Model;

public class AppSettings : ObservableObject
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kero", "settings.json");

    private static AppSettings? _instance;
    public static AppSettings Instance => _instance ??= Load();

    private string _themeName = "Kero Dark";
    public string ThemeName
    {
        get => _themeName;
        set { if (Set(ref _themeName, value)) Save(); }
    }

    public string[] AvailableFonts { get; } = new[] 
    { 
        "Cascadia Mono", "Cascadia Code", "Consolas", "Courier New", "Fira Code", "JetBrains Mono", "Hack", "Source Code Pro", "Ubuntu Mono", "MesloLGS NF" 
    };

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
