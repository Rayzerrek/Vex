using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class CompactLayoutTests
{
    [Fact]
    public void TerminalTemplate_HasNoIslandMarginsOrRoundedClip()
    {
        var assembly = typeof(CompactLayoutTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".ThemeXaml.MainWindow.xaml"));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("MainWindow XAML resource is missing.");
        var document = XDocument.Load(stream);
        var template = document.Descendants().Single(e => e.Name.LocalName == "DataTemplate"
            && (string?)e.Attribute("DataType") == "{x:Type m:TerminalPane}");
        Assert.Null(template.Elements().Single(e => e.Name.LocalName == "Grid").Attribute("Margin"));
        Assert.DoesNotContain(template.Descendants().Attributes(), a => a.Name.LocalName.Contains("RoundedClip"));
        Assert.DoesNotContain(template.Descendants().Where(e => e.Name.LocalName == "Border"),
            e => e.Attribute("CornerRadius") is not null);
        Assert.Contains(template.Descendants(), e => e.Name.LocalName == "RowDefinition" && (string?)e.Attribute("Height") == "24");
    }

    [Theory]
    [InlineData(Orientation.Horizontal)]
    [InlineData(Orientation.Vertical)]
    public void Splitter_ReservesOneDipButKeepsAWiderDragTarget(Orientation orientation)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new SplitPaneView { Pane = new SplitPane(orientation, new EmptyPane(), new EmptyPane()) };
                var grid = Assert.IsType<Grid>(view.Content);
                var splitter = Assert.Single(grid.Children.OfType<GridSplitter>());
                view.Measure(new Size(800, 500));
                view.Arrange(new Rect(0, 0, 800, 500));
                var size = orientation == Orientation.Horizontal ? grid.ColumnDefinitions[1].ActualWidth : grid.RowDefinitions[1].ActualHeight;
                Assert.Equal(1, size);
                Assert.Equal(5, orientation == Orientation.Horizontal ? splitter.ActualWidth : splitter.ActualHeight);
                Assert.Equal(0.5, Assert.IsType<SplitPane>(view.Pane).Ratio);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Compact split layout test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class EmptyPane : LeafPane
    {
        protected override object CreateView() => new Border();
        public override void Focus() { }
    }
}
