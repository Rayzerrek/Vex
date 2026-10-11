using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Vex.App.Terminal.Native;

namespace Vex.App.Model;

/// <summary>Live split-tree preview of existing terminal surfaces; never realizes a pane or changes its geometry.</summary>
public sealed class TabPeekPreview : ObservableObject, IDisposable
{
    private readonly List<(LeafPane Pane, Image Image)> _images = new();
    private FrameworkElement _layout;
    private int _nextPane;

    public WorkspaceTab Tab { get; }
    public FrameworkElement Layout => _layout;

    public TabPeekPreview(WorkspaceTab tab)
    {
        Tab = tab;
        _layout = BuildLayout(tab.Root);
        tab.LayoutChanged += RebuildLayout;
    }

    /// <summary>Refreshes only frames whose terminal drawings changed; called while the overview is visible.</summary>
    internal void RefreshTerminalPreviews()
    {
        var started = Stopwatch.GetTimestamp();
        for (var visited = 0; visited < _images.Count; visited++)
        {
            var (pane, image) = _images[_nextPane];
            _nextPane = (_nextPane + 1) % _images.Count;
            if (pane.ExistingView is NativeTerminalControl terminal)
                image.Source = terminal.CaptureTerminalPreview();
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 3) break;
        }
    }

    private void RebuildLayout()
    {
        _images.Clear();
        _nextPane = 0;
        _layout = BuildLayout(Tab.Root);
        OnPropertyChanged(nameof(Layout));
    }

    private FrameworkElement BuildLayout(PaneNode node) => node switch
    {
        LeafPane leaf => BuildLeaf(leaf),
        SplitPane split => BuildSplit(split),
        _ => new Border(),
    };

    private FrameworkElement BuildLeaf(LeafPane leaf)
    {
        var badge = new ProgramStatusBadge { FontSize = 9, Width = 10, Margin = new Thickness(0, 0, 3, 0) };
        badge.SetBinding(ProgramStatusBadge.StatusProperty, new Binding(nameof(LeafPane.ProgramStatus)) { Source = leaf });
        var title = new TextBlock { FontSize = 9, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(LeafPane.Title)) { Source = leaf });
        title.SetResourceReference(TextBlock.ForegroundProperty, "VexTextDim");
        var header = new DockPanel { Margin = new Thickness(3, 2, 3, 2) };
        header.Children.Add(badge);
        header.Children.Add(title);
        var image = new Image { Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
        _images.Add((leaf, image));
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(header);
        Grid.SetRow(image, 1);
        grid.Children.Add(image);
        var border = new Border { BorderThickness = new Thickness(0.5), Child = grid, ClipToBounds = true };
        border.SetResourceReference(Border.BackgroundProperty, "VexBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "VexBorder");
        return border;
    }

    private FrameworkElement BuildSplit(SplitPane split)
    {
        var grid = new Grid();
        var first = BuildLayout(split.First);
        var second = BuildLayout(split.Second);
        if (split.Orientation == Orientation.Horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(split.Ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - split.Ratio, GridUnitType.Star) });
            Grid.SetColumn(second, 1);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(split.Ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - split.Ratio, GridUnitType.Star) });
            Grid.SetRow(second, 1);
        }
        grid.Children.Add(first);
        grid.Children.Add(second);
        return grid;
    }

    public void Dispose() => Tab.LayoutChanged -= RebuildLayout;
}
