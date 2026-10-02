using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Vex.App;

public sealed partial class MainWindow
{
    private Popup? _projectPickerPopup;
    private Popup? _tabClosePopup;

    private Popup ProjectPickerPopup => _projectPickerPopup ??= CreatePopup("ProjectPickerTemplate", ProjectPillButton);
    private Popup TabClosePopup => _tabClosePopup ??= CreatePopup("TabCloseTemplate", null);
    private ListBox ProjectPickerList => (ListBox)ProjectPickerPopup.FindName(nameof(ProjectPickerList));
    private Image TabClosePopupIcon => (Image)TabClosePopup.FindName(nameof(TabClosePopupIcon));
    private TextBlock TabClosePopupWarning => (TextBlock)TabClosePopup.FindName(nameof(TabClosePopupWarning));
    private TextBlock TabClosePopupTitle => (TextBlock)TabClosePopup.FindName(nameof(TabClosePopupTitle));
    private TextBlock TabClosePopupMessage => (TextBlock)TabClosePopup.FindName(nameof(TabClosePopupMessage));
    private Button TabClosePopupConfirmBtn => (Button)TabClosePopup.FindName(nameof(TabClosePopupConfirmBtn));

    private Popup CreatePopup(string templateName, UIElement? placementTarget)
    {
        // Closed popups have large visual trees. Templates defer their BAML
        // and bindings until the user first opens them, keeping startup clear.
        var popup = (Popup)((ControlTemplate)Resources[templateName]).LoadContent();
        popup.DataContext = _workspace;
        popup.PlacementTarget = placementTarget;
        MainGrid.Children.Add(popup);
        return popup;
    }

#if DEBUG || VEX_SELFTEST
    /// <summary>Exercises deferred popup bindings and close actions in the real window.</summary>
    internal bool SelfTestDeferredPopups()
    {
        // Scripted dispatcher pumps also run unrelated focus callbacks.
        // Keep popups open explicitly while checking bindings/actions.
        ProjectPickerPopup.StaysOpen = true;
        TabClosePopup.StaysOpen = true;
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        ToggleProjectPicker();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        var pickerReady = ProjectPickerPopup.IsOpen
            && ReferenceEquals(ProjectPickerPopup.PlacementTarget, ProjectPillButton)
            && ProjectPickerList.Items.Count == _workspace.Projects.Count
            && ReferenceEquals(ProjectPickerList.SelectedItem, _workspace.SelectedProject);
        ToggleProjectPicker();

        var project = _workspace.SelectedProject!;
        var selected = project.SelectedTab;
        var background = project.NewTab()!;
        project.SelectedTab = selected;
        background.ActiveLeaf!.IsDirty = true;
        try
        {
            RequestCloseTab(background, ProjectPillButton);
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var expectedIcon = Model.TabCloseConfirmation.GetCloseInfo(background).Icon?.Image;
            var confirmationReady = TabClosePopup.IsOpen
                && !string.IsNullOrEmpty(TabClosePopupTitle.Text)
                && !string.IsNullOrEmpty(TabClosePopupMessage.Text)
                && (expectedIcon is null
                    ? TabClosePopupWarning.Visibility == Visibility.Visible && TabClosePopupIcon.Visibility == Visibility.Collapsed
                    : TabClosePopupIcon.Visibility == Visibility.Visible && ReferenceEquals(TabClosePopupIcon.Source, expectedIcon));
            TabClosePopupConfirmBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return pickerReady && confirmationReady && !project.Tabs.Contains(background) && !TabClosePopup.IsOpen;
        }
        finally
        {
            TabClosePopup.IsOpen = false;
            ProjectPickerPopup.StaysOpen = false;
            TabClosePopup.StaysOpen = false;
            if (project.Tabs.Contains(background))
                project.CloseTab(background);
        }
    }
#endif
}
