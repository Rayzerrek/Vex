using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;

namespace Vex.App.Model;

public sealed class EditorPane : LeafPane
{
    private string _filePath;
    private string _content = "";
    private bool _contentLoaded;

    public EditorPane(string filePath)
    {
        _filePath = filePath;
        Title = Path.GetFileName(filePath);
    }

    /// <summary>Loads the file content on first use. Session restore creates
    /// one editor pane per restored tab on the UI thread, so reading every
    /// file eagerly would stall startup on slow disks.</summary>
    private string GetContent()
    {
        if (!_contentLoaded)
        {
            _contentLoaded = true;
            try
            {
                _content = File.ReadAllText(_filePath);
            }
            catch
            {
                _content = "";
            }
        }
        return _content;
    }

    public string FilePath
    {
        get => _filePath;
        set => Set(ref _filePath, value);
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(_filePath))
            return;

        try
        {
            // Read the live text from the editor instead of the _content
            // snapshot; it is only copied once per save, not per keystroke.
            // (editor.Save() would add a UTF-8 BOM with the default encoding.)
            var text = View is TextEditor editor ? editor.Text : GetContent();
            File.WriteAllText(_filePath, text);
            IsDirty = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save file: {ex.Message}", "Error Saving File", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override object CreateView()
    {
        var themeName = AppSettings.Instance.ThemeName;
        var editor = new TextEditor
        {
            Text = GetContent(),
            IsReadOnly = false,
            ShowLineNumbers = true,
            FocusVisualStyle = null,
            FontFamily = new FontFamily(AppSettings.Instance.FontFamily),
            FontSize = AppSettings.Instance.FontSize,
            // The live chrome brush: ChromePalette mutates it in place on a
            // theme switch, so the text follows without being re-assigned.
            Foreground = (Brush)Application.Current.Resources["VexText"],
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            WordWrap = false,
            Padding = new Thickness(4, 4, 0, 0),
        };

        // Grayscale antialiasing, like the terminal surface: ClearType
        // subpixel fringes need a solid backdrop and mis-register when the
        // pane is composited over the acrylic window.
        TextOptions.SetTextRenderingMode(editor, TextRenderingMode.Grayscale);

        // Apply syntax highlighting based on file extension and theme variant.
        ApplyTheme(editor, _filePath, themeName);

        editor.GotFocus += (_, _) => RequestFocus();

        editor.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.W)
            {
                RequestClose();
                e.Handled = true;
            }
            else if (modifiers == ModifierKeys.Control && key == Key.S)
            {
                Save();
                e.Handled = true;
            }
        };

        editor.TextChanged += (_, _) =>
        {
            // O(1): AvalonEdit tracks modification against the undo stack, so
            // no whole-buffer copy or compare per keystroke.
            IsDirty = editor.IsModified;
        };

        return editor;
    }

    private bool _focusPendingLoaded;

    public override void Focus()
    {
        if (View is not TextEditor te)
            return;

        if (te.IsKeyboardFocused)
            return;

        void FocusWhenVisible()
        {
            te.Dispatcher.BeginInvoke(() =>
            {
                if (IsFocused && te.IsVisible && !te.IsKeyboardFocused)
                    te.Focus();
            }, DispatcherPriority.Input);
        }

        if (te.IsLoaded)
        {
            FocusWhenVisible();
            return;
        }

        if (_focusPendingLoaded)
            return;

        _focusPendingLoaded = true;
        RoutedEventHandler onLoaded = null!;
        onLoaded = (_, _) =>
        {
            _focusPendingLoaded = false;
            te.Loaded -= onLoaded;
            FocusWhenVisible();
        };
        te.Loaded += onLoaded;
    }

    /// <summary>True when the theme named by <paramref name="themeName"/> has a dark
    /// background. The editor's surface, gutter and syntax palette all follow this single
    /// answer, derived from the theme itself — the same rule ChromePalette uses for the
    /// chrome variant. Keying off <see cref="AppSettings.Appearance"/> instead let a light
    /// theme (an edited "Custom", a stale settings.json) pair One Dark rules with a light
    /// surface, which is what made highlighted code look washed out or invisible.</summary>
    internal static bool IsDarkFor(string? themeName)
        => BuiltInThemes.Resolve(themeName).IsDark;

    /// <summary>Opaque version of the theme background. Opaque editor pixels give DWM a
    /// coherent backing store for the glyphs; a translucent surface is what lets stale
    /// glyph tiles survive a theme flip.</summary>
    internal static Color SurfaceColorFor(string? themeName)
    {
        var background = FastColor.ParseHex(BuiltInThemes.Resolve(themeName).Background);
        return Color.FromRgb(background.R, background.G, background.B);
    }

    /// <summary>Gutter color: the theme's foreground stepped back toward its background so
    /// line numbers read as chrome rather than code — but never dimmer than the legibility
    /// floor, so a theme whose own foreground is soft keeps readable numbers instead of the
    /// near-invisible fixed gray this used to be.</summary>
    internal static Color GutterColorFor(string? themeName)
    {
        var theme = BuiltInThemes.Resolve(themeName);
        var foreground = FastColor.ParseHex(theme.Foreground);
        var background = FastColor.ParseHex(theme.Background);

        for (var step = theme.IsDark ? 0.45 : 0.32; step > 0; step -= 0.05)
        {
            var candidate = Mix(foreground, background, step);
            if (ContrastRatio(candidate, background) >= MinGutterContrast)
                return candidate;
        }
        return foreground;
    }

    /// <summary>Minimum contrast between the gutter numbers and the editor surface; the
    /// 3:1 floor keeps them readable without competing with the code itself.</summary>
    private const double MinGutterContrast = 3.0;

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static double ContrastRatio(Color a, Color b)
    {
        var first = RelativeLuminance(a);
        var second = RelativeLuminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    /// <summary>(Re)applies the surface, gutter and syntax palette of the active theme to
    /// an editor, then regenerates its visual lines. AvalonEdit caches text and gutter as
    /// per-line drawings behind a reused highlighting-definition instance, so a same-variant
    /// theme switch leaves the redraw to whatever property assignment happens to invalidate
    /// those caches; the explicit call makes the new palette take effect on the first frame
    /// instead of relying on that.</summary>
    internal static void ApplyTheme(TextEditor editor, string filePath, string? themeName)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        editor.SyntaxHighlighting = EditorHighlighting.ForExtension(ext, IsDarkFor(themeName));
        // Opaque theme surface, exactly like a terminal pane: the pane chrome
        // behind it (VexSurface) is translucent in the dark appearance, and DWM
        // can retain stale glyph tiles when text is composited onto a
        // translucent layer (see TerminalPalette).
        editor.Background = new SolidColorBrush(SurfaceColorFor(themeName));
        editor.LineNumbersForeground = new SolidColorBrush(GutterColorFor(themeName));
        editor.TextArea.TextView.Redraw();
    }

    /// <summary>Live re-tint of every open editor after a theme or appearance change: the
    /// syntax palette, surface and gutter are re-derived from the resolved theme and the
    /// cached line visuals regenerated.</summary>
    internal static void OnThemeChanged()
    {
        if (Application.Current?.Dispatcher is null)
            return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var themeName = AppSettings.Instance.ThemeName;
            foreach (var project in SessionStore.Current.Projects)
            foreach (var tab in project.Tabs)
            foreach (var leaf in tab.Leaves.OfType<EditorPane>())
            {
                if (leaf.ViewIfCreated is TextEditor editor)
                    ApplyTheme(editor, leaf.FilePath, themeName);
            }
        });
    }
}
