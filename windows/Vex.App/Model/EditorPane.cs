using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Vex.App.Model;

public sealed class EditorPane : LeafPane
{
    private string _filePath;
    private string _content;

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
            // Read the live text from the editor instead of the _content
            // snapshot; it is only copied once per save, not per keystroke.
            // (editor.Save() would add a UTF-8 BOM with the default encoding.)
            var text = View is TextEditor editor ? editor.Text : _content;
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
            Text = _content,
            IsReadOnly = false,
            ShowLineNumbers = true,
            FontFamily = new FontFamily(AppSettings.Instance.FontFamily),
            FontSize = AppSettings.Instance.FontSize,
            Background = Brushes.Transparent,
            Foreground = (Brush)Application.Current.Resources["VexText"],
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
            // O(1): AvalonEdit tracks modification against the undo stack, so
            // no whole-buffer copy or compare per keystroke.
            IsDirty = editor.IsModified;
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
        return OneDarkHighlighting.ForExtension(ext);
    }
}
