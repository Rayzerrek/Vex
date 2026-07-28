using System.IO;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kero.App.Model;

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
            _content = "Failed to read file.";
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

    protected override object CreateView()
    {
        var textBox = new TextBox
        {
            Text = _content,
            AcceptsReturn = true,
            AcceptsTab = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Background = Brushes.Transparent,
            Foreground = (Brush)System.Windows.Application.Current.Resources["KeroText"],
            BorderThickness = new System.Windows.Thickness(0)
        };

        textBox.GotFocus += (s, e) => RequestFocus();
        
        return textBox;
    }

    public override void Focus()
    {
        if (View is TextBox tb)
            tb.Focus();
    }
}

