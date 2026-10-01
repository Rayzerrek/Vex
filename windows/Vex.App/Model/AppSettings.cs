using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vex.App.Model;

public sealed class AppSettings : ObservableObject
{
    private static readonly string SettingsPath = Path.Combine(AppProfile.DirectoryPath, "settings.json");

    private static AppSettings? _instance;
    public static AppSettings Instance => _instance ??= Load();

    /// <summary>Preloads settings on a background thread so the UI thread can
    /// continue WPF initialization in parallel. After this returns,
    /// <see cref="Instance"/> returns the pre-loaded instance without any
    /// further I/O.</summary>
    public static void Preload()
    {
        if (_instance is null)
            _instance = Load();
    }

    private HalfDebouncer? _saveDebouncer;
    private readonly object _writeLock = new();
    private long _writeVersion;
    private long _lastWrittenVersion;
    private bool _savePending;
    // Only the loaded singleton owns settings.json. Deserialization must not
    // start save timers or persist a partially populated object.
    private bool _persistenceEnabled;

    public AppSettings()
    {
        // HalfDebouncer is instantiated lazily on the first Save() call,
        // so Preload() can deserialize on a background thread without
        // creating or allocating any timers.
    }

    /// <summary>Persists any pending change; called on window close so the
    /// debounce never swallows the last edit.</summary>
    public void Flush()
    {
        if (!_savePending)
            return;
        _saveDebouncer?.Cancel();
        _savePending = false;
        WriteSettings(waitForWrite: true);
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
        var target = appearance == LightAppearance ? LightAppearance : DarkAppearance;
        var dark = target != LightAppearance;
        var crossing = IsDarkAppearance != dark;
        _appearance = target;
        if (crossing)
        {
            ThemeName = BuiltInThemes.ResolveInAppearance(ThemeName, dark).Name;
        }
        OnPropertyChanged(nameof(Appearance));
        OnPropertyChanged(nameof(IsDarkAppearance));
        Save();
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

    private bool _confirmOnExit = true;
    /// <summary>Whether to prompt for confirmation before closing the window when processes or agents are running.</summary>
    public bool ConfirmOnExit
    {
        get => _confirmOnExit;
        set { if (Set(ref _confirmOnExit, value)) Save(); }
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
                var bytes = File.ReadAllBytes(SettingsPath);
                var settings = DeserializeSettings(bytes);
                settings._persistenceEnabled = true;
                MigrateLegacyShell(settings, bytes);
                return settings;
            }
        }
        catch
        {
            // Fallback to defaults
        }
        return new AppSettings { _persistenceEnabled = true };
    }

    /// <summary>Normalizes persisted shell profiles without starting timers or writing settings.</summary>
    internal static AppSettings DeserializeSettings(byte[] bytes)
    {
        var settings = JsonSerializer.Deserialize(bytes, VexJsonContext.Default.AppSettings) ?? new AppSettings();
        settings._customShells ??= new();
        settings._customShells.RemoveAll(static shell => shell is null
            || string.IsNullOrWhiteSpace(shell.Id) || string.IsNullOrWhiteSpace(shell.Program));
        foreach (var shell in settings._customShells)
        {
            if (string.IsNullOrWhiteSpace(shell.Name))
                shell.Name = shell.Id;
            shell.Arguments ??= "";
        }
        settings._fontFamily = string.IsNullOrWhiteSpace(settings._fontFamily) ? "Cascadia Mono" : settings._fontFamily;
        settings._fontSize = Math.Clamp(settings._fontSize, 8, 72);
        settings._themeName ??= "Vex Dark";
        settings._shellId ??= ShellRegistry.SystemDefaultId;
        return settings;
    }

    // Settings written before shells had profile ids carry only a display
    // name ("Shell": "Nushell"). A ShellId key in any form means the file is
    // already in the new format, even when it selects the system default.
    private static void MigrateLegacyShell(AppSettings settings, byte[] bytes)
    {
        if (settings.ShellMigrated)
            return;
        // Fast path: SIMD check for "ShellId" key in UTF-8 bytes to avoid JsonDocument allocation.
        if (bytes.AsSpan().IndexOf("\"ShellId\""u8) >= 0)
            return;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
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
        if (!_persistenceEnabled)
            return;
        _savePending = true;
        _saveDebouncer ??= new HalfDebouncer(TimeSpan.FromMilliseconds(400), () =>
        {
            _savePending = false;
            WriteSettings();
        }, leadingEdge: false);
        _saveDebouncer.Trigger();
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

            // AppSettings has UI-bound collections (e.g. CustomShells).
            // When invoked from a background timer thread, marshal serialization to the
            // UI thread to guarantee a thread-safe, consistent snapshot.
            string json;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                json = dispatcher.Invoke(() => JsonSerializer.Serialize(this, VexJsonContext.Default.AppSettings));
            }
            else
            {
                json = JsonSerializer.Serialize(this, VexJsonContext.Default.AppSettings);
            }

            var version = Interlocked.Increment(ref _writeVersion);
            var write = Task.Run(() =>
            {
                try
                {
                    lock (_writeLock)
                    {
                        if (version < _lastWrittenVersion)
                            return;

                        File.WriteAllText(SettingsPath, json);
                        _lastWrittenVersion = version;
                    }
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
