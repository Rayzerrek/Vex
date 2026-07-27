using System.Windows.Controls;

namespace Kero.App;

public partial class FileTreeView : UserControl
{
    public FileTreeView()
    {
        InitializeComponent();
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
