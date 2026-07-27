using System.Windows;
using Kero.App.Model;
using Microsoft.Win32;

namespace Kero.App;

public partial class MainWindow : Window
{
    private readonly Workspace _workspace = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _workspace;
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a project directory" };
        if (dialog.ShowDialog(this) == true)
            _workspace.NewProject(dialog.FolderName);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        _workspace.SelectedProject?.NewTab();
    }
}
