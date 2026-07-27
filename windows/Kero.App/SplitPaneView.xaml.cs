using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Kero.App.Model;

namespace Kero.App;

/// <summary>
/// Renders a <see cref="SplitPane"/>: two recursively templated children
/// separated by a <see cref="GridSplitter"/>. Built in code because the
/// row/column layout depends on the split orientation.
/// </summary>
public partial class SplitPaneView : UserControl
{
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

        var first = new ContentPresenter { Content = pane.First };
        var second = new ContentPresenter { Content = pane.Second };
        var splitterBrush = (Brush)FindResource("KeroBorder");
        var splitter = new GridSplitter { Background = splitterBrush };

        if (pane.Orientation == Orientation.Horizontal)
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            splitter.Width = 4;
            splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            splitter.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            splitter.Height = 4;
            splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            splitter.VerticalAlignment = VerticalAlignment.Stretch;
        }

        Grid.SetColumn(splitter, pane.Orientation == Orientation.Horizontal ? 1 : 0);
        Grid.SetRow(splitter, pane.Orientation == Orientation.Vertical ? 1 : 0);
        if (pane.Orientation == Orientation.Horizontal)
        {
            Grid.SetColumn(second, 2);
        }
        else
        {
            Grid.SetRow(second, 2);
        }

        Root.Children.Add(first);
        Root.Children.Add(splitter);
        Root.Children.Add(second);
    }
}
