using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

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
        var editor = new TextEditor
        {
            Text = GetContent(),
            IsReadOnly = false,
            ShowLineNumbers = true,
            FocusVisualStyle = null,
            FontFamily = new FontFamily(AppSettings.Instance.FontFamily),
            FontSize = AppSettings.Instance.FontSize,
            Background = Brushes.Transparent,
            Foreground = (Brush)Application.Current.Resources["VexText"],
            // Line-number gutter: a mid-gray that holds contrast on both the
            // dark and the light chrome; re-derived on appearance flips.
            LineNumbersForeground = new SolidColorBrush(EditorGutterColor()),
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            WordWrap = false,
            Padding = new Thickness(4, 4, 0, 0),
        };

        // Apply syntax highlighting based on file extension and appearance.
        ApplyHighlighting(editor);

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

    private static IHighlightingDefinition? ResolveHighlighting(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return EditorHighlighting.ForExtension(ext);
    }

    /// <summary>(Re)applies the highlighting definition for the active
    /// appearance; called at creation and on every appearance flip.</summary>
    private void ApplyHighlighting(TextEditor editor)
    {
        var highlighting = ResolveHighlighting(_filePath);
        editor.SyntaxHighlighting = highlighting;
    }

    private static Color EditorGutterColor()
        => AppSettings.Instance.IsDarkAppearance
            ? Color.FromRgb(0x76, 0x76, 0x7C)
            : Color.FromRgb(0x8B, 0x8F, 0x98);

    /// <summary>Live re-tint of open editors when the app switches between
    /// dark and light: definitions are immutable once loaded, so the cache is
    /// dropped and the One Light set loads on next lookup; the gutter brush
    /// and the definition are then re-applied to every open editor.</summary>
    internal static void OnAppearanceChanged()
    {
        EditorHighlighting.ResetCache();
        if (Application.Current?.Dispatcher is null)
            return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var project in SessionStore.Current.Projects)
            foreach (var tab in project.Tabs)
            foreach (var leaf in tab.Leaves.OfType<EditorPane>())
            {
                if (leaf.ViewIfCreated is TextEditor editor)
                {
                    // Null first so re-assigning the new definition always
                    // invalidates AvalonEdit's rendered lines.
                    editor.SyntaxHighlighting = null;
                    ApplyHighlightingStatic(editor, leaf.FilePath);
                    editor.LineNumbersForeground =
                        new SolidColorBrush(EditorGutterColor());
                }
            }
        });
    }

    private static void ApplyHighlightingStatic(TextEditor editor, string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        editor.SyntaxHighlighting = EditorHighlighting.ForExtension(ext);
    }
}
