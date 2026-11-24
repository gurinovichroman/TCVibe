using TCV.Core.Interfaces;
using TCV.MultiRename;

namespace TCV.App.Operations;

/// <summary>The part of a <see cref="MultiRenameBatch"/> that lies in one folder: one operation and one log line.</summary>
/// <param name="Source">The folder, as the log shows it.</param>
/// <param name="Changes">How many files in it get a new name.</param>
public sealed record MultiRenameFolder(
    string Source, IReadOnlyList<IFileEntry> Files, IReadOnlyList<RenamePreviewItem> Previews, int Changes);

/// <summary>
/// What the Multi-Rename window hands over when it closes: the files and the names the preview showed,
/// exactly as confirmed. The renaming happens in the queue after the window has gone (ADR-0038).
/// </summary>
/// <param name="Changes">How many files get a new name.</param>
/// <param name="Conflicts">How many are left out because their new name cannot be used.</param>
public sealed record MultiRenameBatch(
    IReadOnlyList<IFileEntry> Files, IReadOnlyList<RenamePreviewItem> Previews, int Changes, int Conflicts)
{
    /// <summary>
    /// The batch split by folder. A log line that takes a rename back names one folder, and a batch fed
    /// from search results spans several; the engine's ordering only ever depends on names within one folder,
    /// so nothing is lost by running them apart. The exception is a file system that renames all at
    /// once (an archive): one rewrite, all or nothing, as confirmed.
    /// </summary>
    public IReadOnlyList<MultiRenameFolder> ByFolder(IVirtualFileSystem fileSystem)
    {
        if (fileSystem is IBatchRenameFileSystem { CanRenameAsBatch: true })
        {
            return [new MultiRenameFolder(FolderOf(Files[0]), Files, Previews, Changes)];
        }

        return Enumerable.Range(0, Files.Count)
            .GroupBy(i => FolderOf(Files[i]), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var indexes = group.ToList();
                var previews = indexes.Select(i => Previews[i]).ToList();
                return new MultiRenameFolder(
                    group.Key, indexes.Select(i => Files[i]).ToList(), previews,
                    previews.Count(p => !p.HasConflict && p.OldName != p.NewName));
            })
            .Where(folder => folder.Changes > 0)
            .ToList();
    }

    private static string FolderOf(IFileEntry file) => file.Path.Parent?.Value ?? string.Empty;
}
