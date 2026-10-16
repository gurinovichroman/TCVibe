using TCV.Core.Interfaces;

namespace TCV.App.Viewer;

/// <summary>
/// The files of the panel the viewer was opened from, in the order the panel showed them (its sort and
/// quick filter), which N and P step through like Total Commander's Lister (6.41). Folders and ".." are
/// left out. Pure, so the stepping rules are tested without a window.
/// </summary>
/// <remarks>
/// A snapshot taken when F3 is pressed rather than a live view of the panel: the viewer holds a lease on
/// the file system that listing came from (TRAP-37), while the panel may since have moved to another
/// folder, tab or mount whose files the viewer holds nothing for.
/// </remarks>
public sealed class ViewerFileList
{
    private readonly List<IFileEntry> _files;

    private ViewerFileList(List<IFileEntry> files, int position)
    {
        _files = files;
        Position = position;
    }

    /// <summary>
    /// The files among <paramref name="shown"/> (the panel's rows, in order) with <paramref name="current"/>
    /// as the one in view. When the panel did not list it, the list is that file alone: stepping from a
    /// file the panel does not show would land somewhere arbitrary.
    /// </summary>
    public static ViewerFileList Create(IEnumerable<IFileEntry> shown, IFileEntry current)
    {
        var files = shown.Where(e => !e.IsDirectory && e.Name != "..").ToList();
        var position = files.FindIndex(e => e.Path.Equals(current.Path));
        return position >= 0 ? new ViewerFileList(files, position) : new ViewerFileList([current], 0);
    }

    public IReadOnlyList<IFileEntry> Files => _files;

    /// <summary>Index of the file in view.</summary>
    public int Position { get; private set; }

    /// <summary>The file in view.</summary>
    public IFileEntry Current => _files[Position];

    /// <summary>
    /// The file <paramref name="step"/> places after the one in view (before it when negative), or null
    /// past either end: N on the last file does not wrap to the first.
    /// </summary>
    public IFileEntry? Neighbour(int step)
    {
        var index = Position + step;
        return index >= 0 && index < _files.Count ? _files[index] : null;
    }

    /// <summary>Makes <paramref name="entry"/>, one of <see cref="Files"/>, the file in view.</summary>
    public void MoveTo(IFileEntry entry)
    {
        var index = _files.IndexOf(entry);
        if (index >= 0)
        {
            Position = index;
        }
    }
}

/// <summary>
/// What N and P need besides the list: how to open one of the files, and the lease on the file system
/// they live in (TRAP-05, INV-09). The lease is held for as long as the viewer window is open, whichever
/// of the files it shows, so the sources opened for them hold none of their own.
/// </summary>
public sealed class ViewerSiblings(ViewerFileList files, Func<IFileEntry, IViewerSource> open, IDisposable? lease = null)
    : IDisposable
{
    public ViewerFileList Files { get; } = files;

    /// <summary>A source over <paramref name="entry"/>; the caller owns it.</summary>
    public IViewerSource Open(IFileEntry entry) => open(entry);

    /// <summary>Releases the lease. Called on the UI thread, where leases are taken and let go.</summary>
    public void Dispose() => lease?.Dispose();
}
