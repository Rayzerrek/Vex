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

        var match = new HashSet<int>(result.MatchPositions);
        var text = result.FileName;
        var runStart = 0;
        var inMatch = match.Contains(0);

        for (var i = 1; i <= text.Length; i++)
        {
            var nextIsMatch = i < text.Length && match.Contains(i);
            if (i == text.Length || nextIsMatch != inMatch)
            {
                // Run from runStart to i with the current inMatch state.
                var run = new Run(text[runStart..i]);
                if (inMatch)
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xC4, 0xFF));
                    run.FontWeight = FontWeights.Bold;
                }
                textBlock.Inlines.Add(run);

                runStart = i;
                inMatch = nextIsMatch;
            }
        }
    }
}
