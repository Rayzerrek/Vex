using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Vex.App.Model;

public class AppSettings : ObservableObject
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vex", "settings.json");

    private static AppSettings? _instance;
    public static AppSettings Instance => _instance ??= Load();

    private readonly DispatcherTimer _saveDebounce;
    private bool _savePending;

    public AppSettings()
    {
        // Coalesce disk writes: a font-size drag or theme toggle can fire
        // dozens of setter changes a second, and each would otherwise hit the
        // disk synchronously on the UI thread.
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            _savePending = false;
            WriteSettings();
        };
    }

    /// <summary>Persists any pending change; called on window close so the
    /// debounce never swallows the last edit.</summary>
    public void Flush()
    {
        if (!_savePending)
            return;
        _saveDebounce.Stop();
        _savePending = false;
        WriteSettings(waitForWrite: true);
    }

    private bool _sidebarVisible = true;
    public bool SidebarVisible
    {
        get => _sidebarVisible;
        set { if (Set(ref _sidebarVisible, value)) Save(); }
    }

    private string _themeName = "Vex Dark";
    public string ThemeName
    {
        get => _themeName;
        set { if (Set(ref _themeName, value)) Save(); }
    }

    // Enumerating every system font family takes hundreds of milliseconds, so
    // it is deferred until the settings overlay actually needs the list; the
    // overlay warms it on a background thread, hence the thread-safe Lazy.
    private readonly Lazy<string[]> _availableFonts = new(() =>
        System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(f => f)
            .ToArray());

    [JsonIgnore]
    public string[] AvailableFonts => _availableFonts.Value;

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

    private string _shell = "Nushell";
    public string Shell
    {
        get => _shell;
        set { if (Set(ref _shell, value)) Save(); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
        _savePending = true;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void WriteSettings(bool waitForWrite = false)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null)
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            var write = Task.Run(() =>
            {
                try
                {
                    File.WriteAllText(SettingsPath, json);
                }
                catch
                {
                    // Ignore save errors
                }
            });
            if (waitForWrite)
                write.Wait();
        }
        catch
        {
            // Ignore save errors
        }
    }
}
