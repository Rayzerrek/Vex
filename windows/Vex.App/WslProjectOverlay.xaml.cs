using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Vex.App.Model;

namespace Vex.App;

/// <summary>Opens a project in an installed WSL distribution using Linux paths, including ~.</summary>
public sealed partial class WslProjectOverlay : OverlayControl
{
    private CancellationTokenSource? _query;
    private int _shellListVersion;
    public event Action? Hidden;
    public event Action<string, string>? ProjectOpened;

    public WslProjectOverlay()
    {
        InitializeComponent();
        EnvironmentIcon.Source = AppIcon.WslShellIcon().Image;
        Unloaded += (_, _) =>
        {
            _query?.Cancel();
            _query?.Dispose();
            _query = null;
        };
    }

    public async void Show(string? initialError = null)
    {
        _query?.Cancel();
        _query?.Dispose();
        var query = new CancellationTokenSource();
        _query = query;
        Visibility = Visibility.Visible;
        DirectoryPath.Text = "~";
        Distributions.ItemsSource = null;
        Shells.ItemsSource = null;
        DistributionField.Visibility = ShellField.Visibility = EnvironmentSummary.Visibility = Visibility.Collapsed;
        OpenButton.IsEnabled = false;
        StatusText.Text = "Loading installed distributions…";
        AnimateOverlayOpen(Backdrop, Panel, PanelScale, PanelTranslate);
        try
        {
            var names = await WslProjectLauncher.ListDistributionsAsync(query.Token);
            if (query.IsCancellationRequested) return;
            SetDistributions(names);
            StatusText.Text = initialError ?? (names.Length > 0 ? "" : "No distributions installed. Install one with wsl --install.");
            DirectoryPath.Focus();
            DirectoryPath.SelectAll();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or Win32Exception or FormatException)
        {
            if (!query.IsCancellationRequested) StatusText.Text = error.Message;
        }
    }

    private void SetDistributions(string[] names)
    {
        DistributionField.Visibility = names.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentSummary.Visibility = names.Length == 1 ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentName.Text = names.Length == 1 ? names[0] : "";
        Distributions.ItemsSource = names;
        Distributions.SelectedIndex = names.Length > 0 ? 0 : -1;
        OpenButton.IsEnabled = names.Length > 0;
    }

    private void SetShellChoices(ShellProfile[] profiles)
    {
        var choices = ShellChoiceList.Linux(profiles, "wsl");
        Shells.ItemsSource = choices;
        Shells.SelectedIndex = choices.Length > 0 ? 0 : -1;
        ShellField.Visibility = choices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (Distributions.SelectedItem is string distribution)
            EnvironmentName.Text = choices.Length == 1 ? distribution + " · " + choices[0].Name : distribution;
    }

#if DEBUG || VEX_SELFTEST
    internal bool SelfTestChoiceSelectors()
    {
        SetDistributions(["Ubuntu"]);
        SetShellChoices(WslProjectLauncher.ParseShells("/bin/bash\n/usr/bin/bash\n/bin/sh\n/bin/dash\n"));
        var single = DistributionField.Visibility == Visibility.Collapsed && ShellField.Visibility == Visibility.Collapsed
            && Distributions.Items.Count == 1 && Shells.Items.Count == 1 && EnvironmentName.Text == "Ubuntu · Bash";
        SetDistributions(["Ubuntu", "Debian"]);
        SetShellChoices(WslProjectLauncher.ParseShells("/bin/bash\n/usr/bin/zsh\n"));
        return single && DistributionField.Visibility == Visibility.Visible && ShellField.Visibility == Visibility.Visible
            && Distributions.Items.Count == 2 && Shells.Items.Count == 2;
    }
#endif

    protected override void HideCore() => Hide();
    public void Hide()
    {
        _query?.Cancel();
        HideWithAnimation(Backdrop, Panel, () => Hidden?.Invoke());
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!OpenButton.IsEnabled || Distributions.SelectedItem is not string distribution || _query is not { } query) return;
        var shellId = Shells.SelectedItem is ShellProfile profile ? profile.Id : "wsl";
        OpenButton.IsEnabled = false;
        StatusText.Text = "Opening Linux directory…";
        try
        {
            var directory = await WslProjectLauncher.ResolveDirectoryAsync(distribution, DirectoryPath.Text, query.Token);
            if (query.IsCancellationRequested) return;
            ProjectOpened?.Invoke(directory, shellId);
            Hide();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or Win32Exception or FormatException or ArgumentException)
        {
            if (!query.IsCancellationRequested) StatusText.Text = error.Message;
        }
        finally
        {
            if (!query.IsCancellationRequested) OpenButton.IsEnabled = true;
        }
    }

    private async void Distribution_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_query is not { } query || Distributions.SelectedItem is not string distribution) return;
        var version = ++_shellListVersion;
        Shells.ItemsSource = null;
        ShellField.Visibility = Visibility.Collapsed;
        EnvironmentName.Text = distribution;
        try
        {
            var profiles = await WslProjectLauncher.ListShellsAsync(distribution, query.Token);
            if (query.IsCancellationRequested || version != _shellListVersion) return;
            SetShellChoices(profiles);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or Win32Exception or FormatException)
        {
            if (!query.IsCancellationRequested && version == _shellListVersion) StatusText.Text = error.Message;
        }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_query is not { } query || Distributions.SelectedItem is not string distribution || !OpenButton.IsEnabled) return;
        OpenButton.IsEnabled = false;
        try
        {
            var home = await WslProjectLauncher.ResolveDirectoryAsync(distribution, "~", query.Token);
            if (query.IsCancellationRequested || !Equals(Distributions.SelectedItem, distribution)) return;
            var dialog = new OpenFolderDialog { Title = "Choose a WSL project directory · " + distribution, InitialDirectory = home };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            if (WslShellPaths.ParseNetworkPath(dialog.FolderName) is { } folder && folder.Distribution == distribution)
                DirectoryPath.Text = folder.LinuxPath;
            else StatusText.Text = "Choose a directory inside " + distribution + ".";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or Win32Exception or FormatException)
        {
            if (!query.IsCancellationRequested) StatusText.Text = error.Message;
        }
        finally { if (!query.IsCancellationRequested) OpenButton.IsEnabled = true; }
    }

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Hide();
    private void Panel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();
    private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Open_Click(sender, e); e.Handled = true; }
    }
}
