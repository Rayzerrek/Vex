using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Vex.App;

public partial class FileTreeView : UserControl
{
    public FileTreeView()
    {
        InitializeComponent();
    }

    private void TreeRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Model.FileTreeNode node })
            return;
        SelectNode(node, sender as DependencyObject);
    }

    private void TreeRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Model.FileTreeNode node })
            return;
        SelectNode(node, sender as DependencyObject);
    }

    private void SelectNode(Model.FileTreeNode node, DependencyObject? source)
    {
        var item = FindAncestor<TreeViewItem>(source) ?? FindTreeItem(node);
        if (item is not null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private TreeViewItem? FindTreeItem(Model.FileTreeNode node)
    {
        return FindTreeItem(FileTree.Items, FileTree);

        TreeViewItem? FindTreeItem(ItemCollection items, ItemsControl parent)
        {
            foreach (var item in items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem treeItem)
                    continue;
                if (ReferenceEquals(item, node))
                    return treeItem;
                if (FindTreeItem(treeItem.Items, treeItem) is { } descendant)
                    return descendant;
            }
            return null;
        }
    }

    private void TreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeView { SelectedItem: Model.FileTreeNode selectedNode }
            && !selectedNode.IsDirectory
            && DataContext is Model.Project project)
        {
            project.OpenFile(selectedNode.FullPath);
            e.Handled = true;
        }
    }

    private void FileTreeOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is not { } node)
            return;
        if (node.IsDirectory)
        {
            node.IsExpanded = true;
            return;
        }
        if (DataContext is Model.Project project)
            project.OpenFile(node.FullPath);
    }

    private void FileTreeReveal_Click(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is not { } node || string.IsNullOrWhiteSpace(node.FullPath))
            return;
        var arguments = node.IsDirectory
            ? $"\"{node.FullPath}\""
            : $"/select,\"{node.FullPath}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments)
        {
            UseShellExecute = true,
        });
    }

    private void FileTreeCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { } node)
            Clipboard.SetText(node.FullPath);
    }

    private static Model.FileTreeNode? ContextNode(object sender)
    {
        return (sender as FrameworkElement)?.DataContext as Model.FileTreeNode;
    }

    private void TreeView_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Border or ScrollViewer)
        {
            // Clicked empty space. Unselect the active item.
            if (FileTree.SelectedItem is Model.FileTreeNode selectedNode
                && FindTreeItem(selectedNode) is { } item)
            {
                item.IsSelected = false;
            }
        }
    }
}
