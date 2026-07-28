using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Kero.App.Model;

public sealed class EditorPane : LeafPane
{
    private string _filePath;
    private string _content;
    private string _originalContent;

    public EditorPane(string filePath)
    {
        _filePath = filePath;
        Title = Path.GetFileName(filePath);

        try
        {
            _content = File.ReadAllText(filePath);
        }
        catch
        {
            _content = "";
        }
        _originalContent = _content;
    }

    public string FilePath
    {
        get => _filePath;
        set => Set(ref _filePath, value);
    }

    public string Content
    {
        get => _content;
        set => Set(ref _content, value);
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(_filePath))
            return;

        try
        {
            File.WriteAllText(_filePath, _content);
            _originalContent = _content;
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
            Text = _content,
            IsReadOnly = false,
            ShowLineNumbers = true,
            FontFamily = new FontFamily(AppSettings.Instance.FontFamily),
            FontSize = AppSettings.Instance.FontSize,
            Background = Brushes.Transparent,
            Foreground = (Brush)Application.Current.Resources["KeroText"],
            LineNumbersForeground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            WordWrap = false,
            Padding = new Thickness(4, 4, 0, 0),
        };

        // Apply syntax highlighting based on file extension.
        var highlighting = ResolveHighlighting(_filePath);
        if (highlighting != null)
            editor.SyntaxHighlighting = highlighting;

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
            _content = editor.Text;
            IsDirty = _content != _originalContent;
        };

        return editor;
    }

    public override void Focus()
    {
        if (View is TextEditor te)
            te.Focus();
    }

    private static IHighlightingDefinition? ResolveHighlighting(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // AvalonEdit built-in definitions are resolved by extension.
        var builtin = HighlightingManager.Instance.GetDefinitionByExtension(ext);
        if (builtin != null)
            return builtin;

        // Map common extensions AvalonEdit doesn't know about to
        // similar built-in definitions.
        return ext switch
        {
            ".ts" or ".tsx" or ".mts" or ".cts" => HighlightingManager.Instance.GetDefinition("JavaScript"),
            ".jsx" or ".mjs" or ".cjs" => HighlightingManager.Instance.GetDefinition("JavaScript"),
            ".go" => HighlightingManager.Instance.GetDefinition("C++"),
            ".rs" => HighlightingManager.Instance.GetDefinition("C++"),
            ".swift" => HighlightingManager.Instance.GetDefinition("C++"),
            ".kt" or ".kts" => HighlightingManager.Instance.GetDefinition("Java"),
            ".json" or ".jsonc" => HighlightingManager.Instance.GetDefinition("JavaScript"),
            ".yml" or ".yaml" => HighlightingManager.Instance.GetDefinition("XML"),
            ".md" or ".markdown" => HighlightingManager.Instance.GetDefinition("HTML"),
            ".toml" => HighlightingManager.Instance.GetDefinition("XML"),
            ".sh" or ".bash" or ".zsh" or ".nu" => HighlightingManager.Instance.GetDefinition("C++"),
            ".dockerfile" or ".containerfile" => HighlightingManager.Instance.GetDefinition("C++"),
            _ => null,
        };
    }
}
