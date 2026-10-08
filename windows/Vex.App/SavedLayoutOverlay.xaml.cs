using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Vex.App.Model;

namespace Vex.App;

/// <summary>Edits an isolated saved layout copy; saving never starts a process.</summary>
public sealed partial class SavedLayoutOverlay : OverlayControl
{
    private SavedTerminalLayout? _layout;
    private SavedTerminalLayout? _original;
    private sealed record LayoutPaneRow(int Number, SavedLayoutPane Pane);

    public event Action? Hidden;

    public SavedLayoutOverlay()
    {
        InitializeComponent();
    }

    public void Show(SavedTerminalLayout layout, bool editing = false, string? error = null)
    {
        _original = editing ? layout : null;
        _layout = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(layout, VexJsonContext.Default.SavedTerminalLayout),
            VexJsonContext.Default.SavedTerminalLayout)!;
        Heading.Text = editing ? "Edit layout" : "Save layout";
        LayoutName.Text = _layout.Name;
        PaneRows.ItemsSource = SavedTerminalLayouts.EnumerateLayoutPanes(_layout.Root).Select((pane, index) => new LayoutPaneRow(index + 1, pane)).ToList();
        DeleteButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = error ?? "";
        Visibility = Visibility.Visible;
        AnimateOverlayOpen(Backdrop, Panel, PanelScale, PanelTranslate);
        Dispatcher.BeginInvoke(() => { LayoutName.Focus(); LayoutName.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    protected override void HideCore() => Hide();
    public void Hide() => HideWithAnimation(Backdrop, Panel, () => Hidden?.Invoke());
    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Hide();
    private void Panel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null) return;
        _layout.Name = LayoutName.Text.Trim();
        if (!SavedTerminalLayouts.ValidateLayout(_layout))
        {
            ErrorText.Text = "Enter a name and a directory for each pane.";
            return;
        }
        var settings = AppSettings.Instance;
        var existing = settings.SavedLayouts.Find(layout => layout.Name.Equals(_layout.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null && _original is null && settings.SavedLayouts.Count >= SavedTerminalLayouts.MaxLayouts)
        {
            ErrorText.Text = "Remove a saved layout before adding another.";
            return;
        }
        if (_original is not null) settings.SavedLayouts.Remove(_original);
        if (existing is not null) settings.SavedLayouts.Remove(existing);
        settings.SavedLayouts.Add(_layout);
        settings.SaveSoon();
        Hide();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_original is not null)
        {
            AppSettings.Instance.SavedLayouts.Remove(_original);
            AppSettings.Instance.SaveSoon();
        }
        Hide();
    }

    private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }

#if DEBUG || VEX_SELFTEST
    internal bool SelfTestLayoutEditor()
    {
        if (!IsLoaded || !IsVisible || LayoutName.Text != "Vex layout self-test" || PaneRows.Items.Count == 0 || _layout is null)
            throw new InvalidOperationException($"Saved layout editor not ready: loaded={IsLoaded}, visible={IsVisible}, name={LayoutName.Text}, rows={PaneRows.Items.Count}");
        PaneRows.UpdateLayout();
        var fields = FindPaneFields(PaneRows).ToArray();
        if (fields.Length < 2 || fields[0].Text != SavedTerminalLayouts.EnumerateLayoutPanes(_layout.Root).First().Directory)
            throw new InvalidOperationException($"Saved layout fields not bound: fields={fields.Length}");
        fields[1].Text = "echo layout binding";
        fields[1].GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
        return SavedTerminalLayouts.EnumerateLayoutPanes(_layout.Root).First().Command == "echo layout binding"
            && SavedTerminalLayouts.ValidateLayout(_layout);
    }

    private static IEnumerable<System.Windows.Controls.TextBox> FindPaneFields(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.TextBox field) yield return field;
            else foreach (var nested in FindPaneFields(child)) yield return nested;
        }
    }
#endif
}
