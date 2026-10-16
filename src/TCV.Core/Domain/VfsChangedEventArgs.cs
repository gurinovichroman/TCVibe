using System.IO;
using TCV.Core.Domain;

namespace TCV.Core.Domain;

public sealed class VfsChangedEventArgs : EventArgs
{
    public VfsChangedEventArgs(VfsPath path, WatcherChangeTypes changeType)
    {
        Path = path;
        ChangeType = changeType;
    }

    public VfsPath Path { get; }
    public WatcherChangeTypes ChangeType { get; }
}
