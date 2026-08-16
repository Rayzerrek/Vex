using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

/// <summary>
/// Renders the matched characters of a <see cref="FileSearchResult"/> inside
/// a TextBlock as accent-colored bold runs. WPF cannot bind Inlines, so this
/// attached property rebuilds them whenever the result changes.
/// </summary>
public static class TextHighlighter
{
    private static readonly Brush HighlightBrush = CreateFrozenBrush(Color.FromRgb(0x7F, 0xC4, 0xFF));

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static readonly DependencyProperty HighlightProperty =
        DependencyProperty.RegisterAttached(
            "Highlight",
            typeof(FileSearchResult),
            typeof(TextHighlighter),
            new PropertyMetadata(null, OnHighlightChanged));

    public static FileSearchResult? GetHighlight(DependencyObject obj) =>
        (FileSearchResult?)obj.GetValue(HighlightProperty);

    public static void SetHighlight(DependencyObject obj, FileSearchResult? value) =>
        obj.SetValue(HighlightProperty, value);

    private static void OnHighlightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock || e.NewValue is not FileSearchResult result)
            return;

        textBlock.Inlines.Clear();

        var text = result.FileName;
        if (string.IsNullOrEmpty(text))
            return;

        var positions = result.MatchPositions;
        if (positions.Length == 0)
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        // Avoid HashSet allocation: MatchPositions is sorted ascending, so an index scan is O(1) per char.
        var posIndex = 0;
        var runStart = 0;
        var inMatch = positions[0] == 0;
        if (inMatch)
            posIndex++;

        for (var i = 1; i <= text.Length; i++)
        {
            var nextIsMatch = false;
            if (i < text.Length && posIndex < positions.Length && positions[posIndex] == i)
            {
                nextIsMatch = true;
                posIndex++;
            }

            if (i == text.Length || nextIsMatch != inMatch)
            {
                var run = new Run(text[runStart..i]);
                if (inMatch)
                {
                    run.Foreground = HighlightBrush;
                    run.FontWeight = FontWeights.Bold;
                }
                textBlock.Inlines.Add(run);

                runStart = i;
                inMatch = nextIsMatch;
            }
        }
    }
}
