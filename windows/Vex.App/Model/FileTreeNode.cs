using System.IO;

namespace Vex.App.Model;

public class FileTreeNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isPopulated;
    private string _name;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }
    public string FullPath { get; }
    public bool IsDirectory { get; }

    public ObservableRangeCollection<FileTreeNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value && !_isPopulated && IsDirectory)
            {
                PopulateChildren();
            }
        }
    }

    public FileTreeNode(string fullPath, bool isDirectory, string? nameOverride = null)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        _name = nameOverride ?? Path.GetFileName(fullPath) ?? fullPath;

        if (IsDirectory)
        {
            // Dummy node to allow expansion for directories
            Children.Add(new FileTreeNode("", false, "Loading..."));
        }
    }

    public async void PopulateChildren()
    {
        if (!IsDirectory) return;
        
        _isPopulated = true;

        try
        {
            var nodes = await Task.Run(() =>
            {
                var dirInfo = new DirectoryInfo(FullPath);
                if (!dirInfo.Exists) return new List<FileTreeNode>();

                var entries = dirInfo.EnumerateFileSystemInfos();
                return entries
                    .Where(e => !e.Name.StartsWith('.') && 
                                e.Name != "bin" && 
                                e.Name != "obj" && 
                                e.Name != "node_modules" &&
                                e.Name != ".git")
                    .OrderByDescending(e => (e.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                    .ThenBy(e => e.Name)
                    .Select(e => new FileTreeNode(e.FullName, (e.Attributes & FileAttributes.Directory) == FileAttributes.Directory))
                    .ToList();
            });

            Children.ReplaceRange(nodes);
        }
        catch (Exception)
        {
            Children.Clear();
        }
    }
}
