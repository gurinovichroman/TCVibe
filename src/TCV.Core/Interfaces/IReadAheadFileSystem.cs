using TCV.Core.Domain;

namespace TCV.Core.Interfaces;

/// <summary>
/// A file system where reading files one at a time costs far more than reading them together: a solid
/// archive is decoded from its start for every file. A caller that knows what it is about to read says
/// so, and goes on reading through <see cref="IVirtualFileSystem.OpenReadAsync"/> as usual.
/// </summary>
public interface IReadAheadFileSystem
{
    /// <summary>
    /// Announces the files about to be read. The first read of one of them starts a single pass over all
    /// of them, in the background; reading one waits only until that file has come by. Nothing happens
    /// until then, so announcing what may never be read costs nothing. A file not announced, or one the
    /// pass did not bring, is read the ordinary way. Disposing the result stops the pass and gives back
    /// whatever it holds.
    /// </summary>
    IDisposable ReadAhead(IReadOnlyList<VfsPath> files);
}
