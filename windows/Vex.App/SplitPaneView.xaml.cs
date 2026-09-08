using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

/// <summary>
/// Renders a <see cref="SplitPane"/>: two recursively templated children
/// separated by a draggable <see cref="GridSplitter"/>. Built in code because
/// the row/column layout depends on the split orientation. The divider ratio
/// is read from and written back to <see cref="SplitPane.Ratio"/>, so the
/// layout survives a rebuild (and, via the session store, a restart). Minimum
/// pane sizes are enforced on the grid definitions rather than in code, so a
/// drag costs nothing beyond the splitter's own hit-testing.
/// </summary>
public sealed partial class SplitPaneView : UserControl
{
    // Small enough that a pane can be split again (nested splits) even on a
    // half-width pane of a modest window; terminals simply get fewer columns.
    private const double MinPaneSize = 100;

    public static readonly DependencyProperty PaneProperty =
        DependencyProperty.Register(nameof(Pane), typeof(SplitPane), typeof(SplitPaneView),
            new PropertyMetadata(null, OnPaneChanged));

    public SplitPaneView()
    {
        InitializeComponent();
    }

    public SplitPane? Pane
    {
        get => (SplitPane?)GetValue(PaneProperty);
        set => SetValue(PaneProperty, value);
    }

    private static void OnPaneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SplitPaneView)d).Rebuild();
    }

    private void Rebuild()
    {
        Root.Children.Clear();
        Root.ColumnDefinitions.Clear();
        Root.RowDefinitions.Clear();
        if (Pane is not { } pane)
            return;

        // Bound, not fixed: a later split rewrites SplitPane.First/Second in
        // place and the presenters pick up the new subtree by themselves.
        var first = new ContentPresenter();
        first.SetBinding(ContentProperty, new Binding(nameof(SplitPane.First)) { Source = pane });
        var second = new ContentPresenter();
        second.SetBinding(ContentProperty, new Binding(nameof(SplitPane.Second)) { Source = pane });
        var splitterBrush = (Brush)FindResource("VexBorder");
        var splitter = new GridSplitter { Background = splitterBrush };

        var ratio = pane.Ratio;

        if (pane.Orientation == Orientation.Horizontal)
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star), MinWidth = MinPaneSize });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star), MinWidth = MinPaneSize });
            splitter.Width = 4;
            splitter.ResizeDirection = GridResizeDirection.Columns;
            splitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(second, 2);
        }
        else
        {
            Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ratio, GridUnitType.Star), MinHeight = MinPaneSize });
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - ratio, GridUnitType.Star), MinHeight = MinPaneSize });
            splitter.Height = 4;
            splitter.ResizeDirection = GridResizeDirection.Rows;
            splitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            Grid.SetRow(splitter, 1);
            Grid.SetRow(second, 2);
        }

        // Persist the divider position once the drag ends, not on every delta,
        // so a drag only recomputes the ratio a single time.
        splitter.DragCompleted += (_, _) =>
        {
            var (firstSize, secondSize) = pane.Orientation == Orientation.Horizontal
                ? (Root.ColumnDefinitions[0].ActualWidth, Root.ColumnDefinitions[2].ActualWidth)
                : (Root.RowDefinitions[0].ActualHeight, Root.RowDefinitions[2].ActualHeight);
            var total = firstSize + secondSize;
            if (total > 0)
                pane.Ratio = firstSize / total;
        };

        Root.Children.Add(first);
        Root.Children.Add(splitter);
        Root.Children.Add(second);
    }
}
