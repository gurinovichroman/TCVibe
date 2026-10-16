using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations.Conflict;

public sealed class ConflictInfo
{
    public ConflictInfo(IFileEntry source, IFileEntry existing, VfsPath destination)
    {
        Source = source;
        Existing = existing;
        Destination = destination;
    }

    public IFileEntry Source { get; }
    public IFileEntry Existing { get; }
    public VfsPath Destination { get; }
}
