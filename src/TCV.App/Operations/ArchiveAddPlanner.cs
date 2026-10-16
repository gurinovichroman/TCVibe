using System.IO;
using TCV.App.Operations.Conflict;
using TCV.Archives;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.App.Operations;

/// <summary>
/// A file about to go into an archive. <paramref name="Owner"/> is the index of the selected item it
/// belongs to, so a move knows which sources went in whole. An entry name ending in <c>/</c> is an
/// empty folder (<paramref name="LocalPath"/> is then empty).
/// </summary>
public sealed record ArchiveAddFile(string LocalPath, string EntryName, int Owner);

/// <summary>What <see cref="ArchiveAddPlanner"/> settled: what to add, under which names, and what was left out.</summary>
/// <param name="Files">For the engine's <c>AddFiles</c>.</param>
/// <param name="Skipped">How many files were left out by the user's choice or "only if newer".</param>
/// <param name="IncompleteOwners">Selected items with a file left out: a move must keep them.</param>
public sealed record ArchiveAddPlan(
    IReadOnlyList<(string localPath, string entryName)> Files, int Skipped, IReadOnlySet<int> IncompleteOwners);

/// <summary>
/// Settles the names of files about to be added to an archive against the entries already in it, by
/// the copy dialog's rules (<see cref="ConflictPolicy"/>): replace, leave out, take a free name, or
/// only when newer — what F5 does to a folder on the disk, file by file, folders merging. Every
/// question comes before the one rewrite of the archive, so nothing in it has changed yet when the
/// user cancels.
/// </summary>
public static class ArchiveAddPlanner
{
    /// <summary>
    /// A ZIP keeps a file's time to two seconds: rounded down, the entry of an unchanged file looks a
    /// second older than the file, and "only if newer" would add it again every time.
    /// </summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    /// <param name="existing">The archive's listing.</param>
    /// <param name="describeLocal">Size and time of a file on the disk.</param>
    /// <exception cref="OperationCanceledException">The user chose Cancel at a conflict.</exception>
    public static async Task<ArchiveAddPlan> PlanAsync(
        IReadOnlyList<ArchiveAddFile> files,
        IReadOnlyList<ArchiveEntry> existing,
        IConflictStrategy conflicts,
        bool onlyIfNewer,
        Func<string, (long Size, DateTime Modified)> describeLocal,
        CancellationToken ct)
    {
        // Case-insensitive: two names that differ only by case are one file once extracted on Windows.
        var inArchive = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in existing)
        {
            var name = ArchiveFileSystem.NormalizePath(entry.FullPath);
            if (!entry.IsDirectory && name.Length > 0)
            {
                inArchive[name] = entry;
            }
        }

        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string localPath, string entryName)>(files.Count);
        var incomplete = new HashSet<int>();
        var skipped = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var entryName = ArchiveFileSystem.NormalizePath(file.EntryName);
            if (file.EntryName.EndsWith('/'))
            {
                result.Add((file.LocalPath, entryName + "/"));
                continue;
            }

            if (inArchive.TryGetValue(entryName, out var there))
            {
                var (size, modified) = describeLocal(file.LocalPath);
                var source = new FileEntry(new VfsPath(file.LocalPath), Path.GetFileName(entryName), false,
                    size, modified, FileAttributes.Normal);
                var target = new FileEntry(new VfsPath("/" + entryName), there.Name, false,
                    there.Size, there.LastModified, FileAttributes.Normal);

                var resolution = await ConflictPolicy.ResolveAsync(
                    new ConflictInfo(source, target, target.Path), onlyIfNewer, conflicts.ResolveAsync, ct,
                    TimeTolerance);

                switch (resolution)
                {
                    case ConflictResolution.Skip:
                        skipped++;
                        incomplete.Add(file.Owner);
                        continue;
                    case ConflictResolution.Cancel:
                        throw new OperationCanceledException("Cancelled by user at conflict.");
                    case ConflictResolution.RenameAuto:
                        entryName = FreeName(entryName, candidate =>
                            inArchive.ContainsKey(candidate) || planned.Contains(candidate));
                        break;
                }
            }

            planned.Add(entryName);
            result.Add((file.LocalPath, entryName));
        }

        return new ArchiveAddPlan(result, skipped, incomplete);
    }

    /// <summary>«dir/a (1).txt» for «dir/a.txt»: the same folder, the name Explorer would pick.</summary>
    private static string FreeName(string entryName, Func<string, bool> taken)
    {
        var slash = entryName.LastIndexOf('/');
        var folder = slash < 0 ? string.Empty : entryName[..(slash + 1)];
        var name = entryName[(slash + 1)..];
        return folder + RenameAutoStrategy.GenerateNewName(name, candidate => taken(folder + candidate));
    }
}
