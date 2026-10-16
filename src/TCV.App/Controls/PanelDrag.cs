using TCV.App.ViewModels;
using TCV.Core.Interfaces;

namespace TCV.App.Controls;

/// <summary>
/// What a drag out of a server panel carries. A server's entries have no Windows path to put in
/// <c>FileDrop</c>, and an entry object cannot cross OLE, so the data object holds only
/// <see cref="Format"/> as a marker and the drag itself waits here. <c>DoDragDrop</c> is a nested loop on
/// the UI thread, so <see cref="Current"/> is set exactly while the drag runs; another window's drag
/// is another process and leaves it empty, which makes its drop a refusal.
/// </summary>
internal sealed record PanelDrag(FileListViewModel Source, IVirtualFileSystem Fs, IReadOnlyList<IFileEntry> Entries)
{
    public const string Format = "TCV.PanelDrag";

    public static PanelDrag? Current { get; set; }
}
