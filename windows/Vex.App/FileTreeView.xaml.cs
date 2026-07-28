using System.Windows.Controls;

namespace Vex.App;

public partial class FileTreeView : UserControl
{
    public FileTreeView()
    {
        InitializeComponent();
    }

    private void TreeView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is TreeView treeView && treeView.SelectedItem is Model.FileTreeNode selectedNode)
        {
            if (!selectedNode.IsDirectory && DataContext is Model.Project project)
            {
                project.OpenFile(selectedNode.FullPath);
                e.Handled = true;
            }
        }
    }

    private void TreeView_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Border or System.Windows.Controls.ScrollViewer)
        {
            // Clicked empty space. Unselect the active item.
            var treeView = (TreeView)sender;
            if (treeView.SelectedItem is Model.FileTreeNode selectedNode)
            {
                selectedNode.IsExpanded = selectedNode.IsExpanded; // does nothing to selection
                // Actually WPF TreeView doesn't easily let you unselect bound items unless IsSelected is a property on the model.
                // But we can clear it using reflection or finding the TreeViewItem.
                if (treeView.ItemContainerGenerator.ContainerFromItem(selectedNode) is TreeViewItem tvi)
                {
                    tvi.IsSelected = false;
                }
                else
                {
                    // Fallback to clear all by traversing
                    ClearSelection(treeView.Items, treeView);
                }
            }
        }
    }

    private bool ClearSelection(ItemCollection items, ItemsControl parent)
    {
        foreach (var item in items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
            {
                if (tvi.IsSelected)
                {
                    tvi.IsSelected = false;
                    return true;
                }
                if (ClearSelection(tvi.Items, tvi))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
