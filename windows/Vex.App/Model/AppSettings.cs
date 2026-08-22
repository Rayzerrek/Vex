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

    /// <summary>App-wide appearance: "Dark" or "Light". The chrome derives its
    /// palette variant from this, and the theme picker offers only themes of
    /// the matching kind. Defaults to the OS setting on first run.</summary>
    private string _appearance = "";
    public string Appearance
    {
        get => _appearance;
        set
        {
            var v = value == LightAppearance ? LightAppearance : DarkAppearance;
            if (Set(ref _appearance, v))
                Save();
        }
    }

    public const string DarkAppearance = "Dark";
    public const string LightAppearance = "Light";

    /// <summary>True unless the appearance is explicitly "Light"; an empty
    /// (unset) value means dark, the app's original look.</summary>
    [JsonIgnore]
    public bool IsDarkAppearance => Appearance != LightAppearance;

    /// <summary>Sets the appearance and moves ThemeName into it in one go, so
    /// chrome and terminal re-tint together through the normal pipeline.</summary>
    public void SetAppearance(string appearance)
    {
        var dark = appearance != LightAppearance;
        if (!IsDarkAppearance && dark || IsDarkAppearance && !dark)
        {
            // Crossing the boundary: pick a theme of the new kind. The current
            // name is kept when it already belongs to the target appearance.
            ThemeName = BuiltInThemes.ResolveInAppearance(ThemeName, dark).Name;
        }
        Appearance = dark ? DarkAppearance : LightAppearance;
    }

    /// <summary>Resolves the persisted appearance once at startup; an empty or
    /// unknown value follows the OS "app light" preference.</summary>
    public void InitializeAppearance()
    {
        if (_appearance != DarkAppearance && _appearance != LightAppearance)
            _appearance = UsesOsLightApps() ? LightAppearance : DarkAppearance;
    }

    private static bool UsesOsLightApps()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
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

    // Legacy single-name setting kept only so older settings.json files
    // deserialize; MigrateLegacyShell folds it into ShellId on load.
    private string _shell = "Nushell";
    public string Shell
    {
        get => _shell;
        set { if (Set(ref _shell, value)) Save(); }
    }

    /// <summary>Selected shell profile id; <see cref="ShellRegistry.SystemDefaultId"/>
    /// means "whatever the OS default shell is".</summary>
    private string _shellId = ShellRegistry.SystemDefaultId;
    public string ShellId
    {
        get => _shellId;
        set { if (Set(ref _shellId, value)) Save(); }
    }

    private List<ShellProfile> _customShells = new();
    public List<ShellProfile> CustomShells
    {
        get => _customShells;
        set { if (Set(ref _customShells, value)) Save(); }
    }

    [JsonIgnore]
    public bool ShellMigrated { get; set; }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize(json, VexJsonContext.Default.AppSettings);
                if (settings != null)
                {
                    MigrateLegacyShell(settings, json);
                    return settings;
                }
            }
        }
        catch
        {
            // Fallback to defaults
        }
        return new AppSettings();
    }

    // Settings written before shells had profile ids carry only a display
    // name ("Shell": "Nushell"). A ShellId key in any form means the file is
    // already in the new format, even when it selects the system default.
    private static void MigrateLegacyShell(AppSettings settings, string json)
    {
        if (settings.ShellMigrated)
            return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(nameof(ShellId), out _))
                return;
            settings.ShellId = ShellRegistry.MigrateLegacyName(settings.Shell);
        }
        catch
        {
            // Undecidable JSON: keep the default id rather than guess.
        }
    }

    private void Save()
    {
        _savePending = true;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    /// <summary>Schedules a debounced save for changes the property setters
    /// cannot see, such as edits inside the custom-shell list.</summary>
    public void SaveSoon() => Save();

    private void WriteSettings(bool waitForWrite = false)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null)
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, VexJsonContext.Default.AppSettings);
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
