using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Vex.App.Model;

/// <summary>
/// Lightweight, dependency-free preview of a single <see cref="WorkspaceTab"/>,
/// built once when the Tab Peek overlay opens. The split tree is flattened into
/// a <see cref="MiniNode"/> graph that the XAML renders as nested boxes — no
/// terminal controls are created and no framebuffers are captured, so opening
/// the overlay is O(panes) and stays off the render hot path.
/// </summary>
public sealed class TabPeekPreview : ObservableObject
{
    public WorkspaceTab Tab { get; }
    public string Title { get; }
    public int PaneCount { get; }

    /// <summary>A UI element (nested StackPanels/Borders) representing the
    /// split layout. Built once on construction and reused across renders.
    /// </summary>
    public FrameworkElement Layout { get; }

    public TabPeekPreview(WorkspaceTab tab)
    {
        Tab = tab;
        Title = tab.Title;
        PaneCount = tab.PaneCount;
        Layout = BuildLayout(tab.Root, tab.ActiveLeaf);
    }

    /// <summary>Recursively builds a lightweight visual tree mirroring the
    /// split structure. Leaf panes become labelled boxes with a state dot;
    /// split panes become a Grid with a proportional divider.</summary>
    private static FrameworkElement BuildLayout(PaneNode node, LeafPane? active)
    {
        return node switch
        {
            LeafPane leaf => BuildLeaf(leaf, ReferenceEquals(leaf, active)),
            SplitPane split => BuildSplit(split, active),
            _ => new Border { Height = 1 },
        };
    }

    private static FrameworkElement BuildLeaf(LeafPane leaf, bool isActive)
    {
        var dotColor = leaf.State switch
        {
            PaneState.Busy => "#7AA2F7",
            PaneState.Exited => "#FF6B4B",
            _ => "#4A5568",
        };

        var dot = new Ellipse
        {
            Width = 5, Height = 5, Fill = new SolidColorBrush(FastColor.ParseHex(dotColor)),
            Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = leaf.Title,
            FontSize = 9,
            Foreground = isActive
                ? new SolidColorBrush(FastColor.ParseHex("#E5E9F0"))
                : new SolidColorBrush(FastColor.ParseHex("#6B7280")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(dot);
        panel.Children.Add(label);

        var border = new Border
        {
            Background = new SolidColorBrush(FastColor.ParseHex(
                isActive ? "#2D3548" : "#1A1F2E")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4, 6, 4),
            BorderBrush = isActive
                ? new SolidColorBrush(FastColor.ParseHex("#5A6B9E"))
                : new SolidColorBrush(FastColor.ParseHex("#2A3040")),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
        return border;
    }

    private static FrameworkElement BuildSplit(SplitPane split, LeafPane? active)
    {
        var grid = new Grid();
        var first = BuildLayout(split.First, active);
        var second = BuildLayout(split.Second, active);

        if (split.Orientation == Orientation.Horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(split.Ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - split.Ratio, GridUnitType.Star) });
            Grid.SetColumn(first, 0);
            Grid.SetColumn(second, 1);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(split.Ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - split.Ratio, GridUnitType.Star) });
            Grid.SetRow(first, 0);
            Grid.SetRow(second, 1);
        }

        grid.Children.Add(first);
        grid.Children.Add(second);
        return grid;
    }
}
