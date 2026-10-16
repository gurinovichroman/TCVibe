using TCV.Core.Interfaces;

namespace TCV.App.ViewModels;

/// <summary>A panel listed a folder on the local disk: the folder (a Windows path) and what it holds.</summary>
public sealed class DiskListingEventArgs : EventArgs
{
    public DiskListingEventArgs(string path, IReadOnlyList<IFileEntry> entries)
    {
        Path = path;
        Entries = entries;
    }

    public string Path { get; }

    public IReadOnlyList<IFileEntry> Entries { get; }
}
