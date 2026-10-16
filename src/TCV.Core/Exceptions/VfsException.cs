using TCV.Core.Domain;

namespace TCV.Core.Exceptions;

public class VfsException : Exception
{
    public VfsPath? Path { get; }

    public VfsException(string message, VfsPath? path = null)
        : base(message)
    {
        Path = path;
    }

    public VfsException(string message, Exception inner, VfsPath? path = null)
        : base(message, inner)
    {
        Path = path;
    }
}
