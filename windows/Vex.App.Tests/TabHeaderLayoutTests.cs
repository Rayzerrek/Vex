using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class TabHeaderLayoutTests
{
    [Theory]
    [InlineData(60, 1)]
    [InlineData(60, 2)]
    [InlineData(160, 1)]
    [InlineData(160, 2)]
    public void LongTabTitle_KeepsIconsVisibleAndFitsAvailableWidth(int width, int iconCount)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var assembly = typeof(TabHeaderLayoutTests).Assembly;
                var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".ThemeXaml.MainWindow.xaml"));
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException("MainWindow XAML resource is missing.");
                var document = XDocument.Load(stream);
                XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
                var titleElement = document.Descendants().Single(e => (string?)e.Attribute(xaml + "Name") == "TitleBlock");
                var panelElement = new XElement(titleElement.Parent
                    ?? throw new InvalidOperationException("Tab title panel is missing."));
                foreach (var element in panelElement.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:Vex.App"))
                    element.Name = XName.Get(element.Name.LocalName, "clr-namespace:Vex.App;assembly=Vex.App");
                panelElement.SetAttributeValue(XNamespace.Xmlns + "x", xaml.NamespaceName);
                panelElement.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:Vex.App;assembly=Vex.App");
                panelElement.SetAttributeValue(XNamespace.Xmlns + "m", "clr-namespace:Vex.App.Model;assembly=Vex.App");
                var panel = Assert.IsAssignableFrom<Panel>(XamlReader.Parse(panelElement.ToString()));
                var title = Assert.Single(panel.Children.OfType<TextBlock>(), t => t.Name == "TitleBlock");
                title.Text = new string('W', 200);
                var icons = Assert.Single(panel.Children.OfType<TabIconStack>());
                icons.Icons = Enumerable.Repeat(AppIcon.Glyph("nushell", isDark: true), iconCount).ToArray();
                var host = new Grid();
                host.Children.Add(panel);
                host.Measure(new Size(width, 32));
                host.Arrange(new Rect(0, 0, width, 32));

                var iconBounds = icons.TransformToAncestor(host).TransformBounds(new Rect(icons.RenderSize));
                var titleBounds = title.TransformToAncestor(host).TransformBounds(new Rect(title.RenderSize));
                Assert.True(iconBounds.Left >= 0, $"Tab icon is clipped at {iconBounds.Left}.");
                Assert.True(iconBounds.Right <= width, "Tab icon extends beyond the header.");
                Assert.True(titleBounds.Left >= iconBounds.Right, "Tab title overlaps the icon.");
                Assert.True(titleBounds.Right <= width, "Tab title extends beyond the header.");
                Assert.True(title.ActualWidth > 0, "Tab title has no remaining space.");
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Tab header layout test did not finish.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
