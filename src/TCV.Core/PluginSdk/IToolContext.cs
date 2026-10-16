using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Core.PluginSdk;

public interface IToolContext
{
    IReadOnlyList<IFileEntry> SelectedFiles { get; }
    IVirtualFileSystem ActiveFileSystem { get; }
    VfsPath ActivePath { get; }
    IVirtualFileSystem OppositeFileSystem { get; }
    VfsPath OppositePath { get; }
    void Refresh();
}
