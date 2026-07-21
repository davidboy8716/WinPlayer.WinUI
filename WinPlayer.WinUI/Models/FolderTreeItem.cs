using System.IO;

namespace WinPlayer.WinUI.Models;

/// <summary>保存在延迟加载 TreeViewNode 中的轻量文件夹元数据。</summary>
public sealed class FolderTreeItem
{
    public FolderTreeItem(string path, bool hasSubfolders = false)
    {
        Path = path;
        HasSubfolders = hasSubfolders;
        Name = new DirectoryInfo(path).Name;
        if (string.IsNullOrWhiteSpace(Name)) Name = path;
    }

    public string Name { get; }
    public string Path { get; }
    public bool HasSubfolders { get; }
    public bool ChildrenLoaded { get; set; }
    public bool ChildrenLoading { get; set; }
}
