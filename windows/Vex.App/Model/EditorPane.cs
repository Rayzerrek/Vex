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
        return OneDarkHighlighting.ForExtension(ext);
    }
}
