using System.IO;
using System.IO.Compression;
using TCV.App.Operations;
using TCV.Core.Domain;
using TCV.Core.PluginSdk;

namespace TCV.App.Services;

/// <summary>
/// What goes into a new archive: every file below the selection, plus an entry (name ending in
/// <c>/</c>) for each folder that would otherwise vanish because it holds no files.
/// </summary>
/// <remarks>
/// A junction or a directory symlink is not entered, and nothing of it is stored: a cycle through one
/// never ends (a junction to an ancestor packed the tree into itself until the path got too long),
/// and what it points to is not part of this tree — a copy and a delete draw the same line
/// (<see cref="DirectoryTree.IsLink"/>). Each one left out is reported. A file link is packed as the
/// file it points to, as a copy reads it.
/// </remarks>
public static class PackEntryCollector
{
    /// <param name="sourcePaths">Selected files and folders, as disk paths.</param>
    /// <param name="exclude">Files that must not be packed into themselves (the archive being written).</param>
    /// <param name="onSkipped">Receives the entry name of each directory link left out.</param>
    public static IEnumerable<(string localPath, string entryName)> Collect(
        IEnumerable<string> sourcePaths, IEnumerable<string>? exclude = null, Action<string>? onSkipped = null)
    {
        var skipped = new HashSet<string>(
            (exclude ?? []).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        foreach (var source in sourcePaths)
        {
            var trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            var directory = new DirectoryInfo(trimmed);

            if (directory.Exists)
            {
                if (DirectoryTree.IsLink(directory))
                {
                    onSkipped?.Invoke(name);
                    continue;
                }

                foreach (var entry in CollectDirectory(directory, name, skipped, onSkipped))
                {
                    yield return entry;
                }
            }
            else if (!skipped.Contains(Path.GetFullPath(trimmed)))
            {
                yield return (trimmed, name);
            }
        }
    }

    /// <summary>
    /// For a packer that walks the tree itself (7z.exe follows a junction, round a loop until the path
    /// is too long): the sources that are not links themselves, and the entry name of every directory
    /// link below them, to be excluded. Each link is reported, as <see cref="Collect"/> reports it.
    /// </summary>
    public static (IReadOnlyList<string> Sources, IReadOnlyList<string> LinkEntries) FindLinks(
        IEnumerable<string> sourcePaths, Action<string>? onSkipped = null)
    {
        var sources = new List<string>();
        var links = new List<string>();

        foreach (var source in sourcePaths)
        {
            var trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            var directory = new DirectoryInfo(trimmed);
            if (directory.Exists && DirectoryTree.IsLink(directory))
            {
                onSkipped?.Invoke(name);
                continue;
            }

            sources.Add(source);
            if (directory.Exists)
            {
                FindLinksBelow(directory, name, links, onSkipped);
            }
        }

        return (sources, links);
    }

    private static void FindLinksBelow(DirectoryInfo directory, string entryPrefix, List<string> links, Action<string>? onSkipped)
    {
        foreach (var subdirectory in directory.EnumerateDirectories("*", ArchiveAddCollector.EveryEntry))
        {
            var name = $"{entryPrefix}/{subdirectory.Name}";
            if (DirectoryTree.IsLink(subdirectory))
            {
                links.Add(name);
                onSkipped?.Invoke(name);
            }
            else
            {
                FindLinksBelow(subdirectory, name, links, onSkipped);
            }
        }
    }

    private static IEnumerable<(string localPath, string entryName)> CollectDirectory(
        DirectoryInfo directory, string entryPrefix, HashSet<string> skipped, Action<string>? onSkipped)
    {
        var hasChildren = false;

        // Hidden and system entries included (.git is hidden), as before.
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            var childName = $"{entryPrefix}/{child.Name}";
            if (child is DirectoryInfo subdirectory)
            {
                if (DirectoryTree.IsLink(subdirectory))
                {
                    onSkipped?.Invoke(childName);
                    continue;
                }

                hasChildren = true;
                foreach (var entry in CollectDirectory(subdirectory, childName, skipped, onSkipped))
                {
                    yield return entry;
                }
            }
            else if (!skipped.Contains(child.FullName))
            {
                hasChildren = true;
                yield return (child.FullName, childName);
            }
        }

        if (!hasChildren)
        {
            // An empty folder has no file to carry it — nor one whose only content was a link left
            // out: give it an entry of its own.
            yield return (string.Empty, entryPrefix + "/");
        }
    }
}

/// <summary>Why <see cref="ArchiveAddCollector"/> left something out.</summary>
public enum ArchiveAddSkip
{
    /// <summary>The archive being written, or a folder holding it: it cannot go into itself.</summary>
    ArchiveItself,

    /// <summary>A junction or a directory symlink, not followed (<see cref="DirectoryTree.IsLink"/>).</summary>
    Link,
}

/// <summary>
/// What adding a folder to an open archive (F5/F6, paste, drop) puts in: its files, an entry for
/// each empty folder, never the archive itself. Directory links and junctions are not followed or
/// stored, and each one is reported, so a move knows the folder did not go in whole and keeps the
/// source. A file link goes in as the file it points to, as a copy reads it; other reparse points
/// (a OneDrive placeholder, a deduplicated file) are this tree's own content and go in as files.
/// An unreadable folder throws rather than being passed over for the same reason.
/// </summary>
public static class ArchiveAddCollector
{
    /// <summary>
    /// Every entry of a folder, hidden and system ones included (.git is hidden). Not
    /// <c>IgnoreInaccessible</c>, the default of a new <see cref="EnumerationOptions"/>: an unreadable
    /// folder would list as empty, go in as an empty folder, and a move would count it as whole.
    /// </summary>
    internal static readonly EnumerationOptions EveryEntry = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    /// <summary>
    /// The files below each source as archive entries under <paramref name="archiveSubDir"/>, and the
    /// indexes of the sources that cannot go in whole: a directory link, a folder holding a link, and
    /// the archive itself or a folder holding it — a move would otherwise delete the archive with it.
    /// </summary>
    /// <param name="sourcePaths">Disk files and folders.</param>
    /// <param name="newName">The name a single source gets in the archive (F5 to "/docs/new.txt").</param>
    /// <param name="onSkipped">Receives why and what (entry or file name) was left out.</param>
    public static (List<ArchiveAddFile> Files, HashSet<int> NotWhole) CollectSources(
        IReadOnlyList<string> sourcePaths, string? newName, string archivePath, string archiveSubDir,
        Action<ArchiveAddSkip, string> onSkipped, CancellationToken ct)
    {
        var files = new List<ArchiveAddFile>();
        var notWhole = new HashSet<int>();
        var archive = new VfsPath(Path.GetFullPath(archivePath));

        for (var i = 0; i < sourcePaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = sourcePaths[i].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = newName is not null && sourcePaths.Count == 1 ? newName : Path.GetFileName(path);

            if (OperationPaths.IsSameOrInside(archive, new VfsPath(Path.GetFullPath(path))))
            {
                onSkipped(ArchiveAddSkip.ArchiveItself, Path.GetFileName(archivePath));
                notWhole.Add(i);
                if (!Directory.Exists(path))
                {
                    continue;
                }
            }

            if (Directory.Exists(path))
            {
                if (DirectoryTree.IsLink(new DirectoryInfo(path)))
                {
                    onSkipped(ArchiveAddSkip.Link, name);
                    notWhole.Add(i);
                    continue;
                }

                var owner = i;
                foreach (var (localPath, relativeName) in CollectFolder(path, name, archivePath, link =>
                         {
                             notWhole.Add(owner);
                             onSkipped(ArchiveAddSkip.Link, link);
                         }))
                {
                    files.Add(new ArchiveAddFile(localPath, JoinEntryPath(archiveSubDir, relativeName), i));
                }
            }
            else
            {
                files.Add(new ArchiveAddFile(path, JoinEntryPath(archiveSubDir, name), i));
            }
        }

        return (files, notWhole);
    }

    /// <param name="directory">The folder on disk.</param>
    /// <param name="entryPrefix">Its entry name inside the archive (no trailing slash).</param>
    /// <param name="exclude">The archive being written, which must not be added into itself.</param>
    /// <param name="onSkipped">Receives the entry name of each link left out.</param>
    public static IEnumerable<(string localPath, string entryName)> CollectFolder(
        string directory, string entryPrefix, string? exclude, Action<string>? onSkipped)
    {
        var excluded = exclude is null ? null : Path.GetFullPath(exclude);
        var any = false;

        foreach (var child in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", EveryEntry))
        {
            any = true;
            var name = $"{entryPrefix}/{child.Name}";

            if (child is DirectoryInfo subdirectory)
            {
                if (DirectoryTree.IsLink(subdirectory))
                {
                    // A cycle through a junction would never end, and a link's target is not part of this folder.
                    onSkipped?.Invoke(name);
                    continue;
                }

                foreach (var entry in CollectFolder(subdirectory.FullName, name, exclude, onSkipped))
                {
                    yield return entry;
                }
            }
            else if (!string.Equals(child.FullName, excluded, StringComparison.OrdinalIgnoreCase))
            {
                yield return (child.FullName, name);
            }
        }

        if (!any)
        {
            yield return (string.Empty, entryPrefix + "/");
        }
    }

    /// <summary><paramref name="name"/> under the archive folder <paramref name="subDir"/> ("" = the root).</summary>
    public static string JoinEntryPath(string subDir, string name) =>
        string.IsNullOrEmpty(subDir) ? name : $"{subDir}/{name}";
}

/// <summary>
/// Creates an archive without ever exposing a half-written one: the work goes to a hidden sibling
/// file and replaces the target only when it is complete. A cancelled or failed run deletes the
/// scratch file and leaves an existing archive untouched.
/// </summary>
public static class PackJob
{
    /// <summary>Scratch name beside <paramref name="archivePath"/>; keeps the extension (TAR picks its compression from it).</summary>
    public static string ScratchPathFor(string archivePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
        return Path.Combine(directory, $".tcv-{Guid.NewGuid():N}"[..14] + "-" + Path.GetFileName(archivePath));
    }

    public static CompressionLevel MapCompressionLevel(ArchiveCompressionLevel level) => level switch
    {
        ArchiveCompressionLevel.Store => CompressionLevel.NoCompression,
        ArchiveCompressionLevel.Fast => CompressionLevel.Fastest,
        ArchiveCompressionLevel.Ultra => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

    /// <exception cref="InvalidOperationException">7-Zip is needed and missing.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled; nothing is left behind.</exception>
    /// <param name="onLinkSkipped">Receives the entry name of each directory link left out (ZIP and TAR; see <see cref="PackEntryCollector"/>).</param>
    public static async Task RunAsync(
        PackDialogResult request,
        IReadOnlyList<string> sourcePaths,
        IArchivePlugin plugin,
        bool sevenZipAvailable,
        IProgress<double>? progress,
        CancellationToken ct,
        Action<string>? onLinkSkipped = null)
    {
        var target = Path.GetFullPath(request.ArchivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var level = MapCompressionLevel(request.CompressionLevel);
        var usesSevenZip = request.Format == ArchiveFormat.SevenZip
                           || (request.Format == ArchiveFormat.Zip && !string.IsNullOrEmpty(request.Password));

        if (usesSevenZip && !sevenZipAvailable)
        {
            throw new InvalidOperationException("7-Zip is required for this archive but was not found.");
        }

        // Adding to an existing ZIP keeps what is there: the engine edits its own scratch copy and
        // swaps it in, so a failed or cancelled run leaves the archive as it was.
        if (request.AddToExisting && request.Format == ArchiveFormat.Zip && !usesSevenZip && File.Exists(target))
        {
            var additions = PackEntryCollector.Collect(sourcePaths, [target], onLinkSkipped).ToList();
            var zip = plugin.EngineFactory.Create("zip") as TCV.Archives.Engines.ZipEngine
                ?? throw new NotSupportedException("The ZIP engine cannot add files to an existing archive.");
            await Task.Run(() => zip.AddFiles(target, additions, level, ct), ct);
            return;
        }

        var scratch = ScratchPathFor(target);
        try
        {
            if (usesSevenZip)
            {
                // 7z.exe walks the tree itself and enters junctions: the links are found first and
                // excluded, so it draws the same line as the ZIP and TAR paths (TRAP-64).
                var (sources, links) = await Task.Run(
                    () => PackEntryCollector.FindLinks(sourcePaths, onLinkSkipped), ct);

                // Every source was a link, reported above. 7z.exe given no names packs "*" of its
                // working directory — the program's own folder — so it is not run at all.
                if (sources.Count == 0)
                {
                    return;
                }

                await WindowsArchiveService.PackWith7ZipAsync(
                    scratch, sources, request.CompressionLevel, request.Password,
                    forceZipFormat: request.Format == ArchiveFormat.Zip, progress, ct, links);
            }
            else
            {
                var format = request.Format == ArchiveFormat.Tar ? "tar" : "zip";
                var entries = PackEntryCollector.Collect(sourcePaths, [target, scratch], onLinkSkipped).ToList();
                var engine = plugin.EngineFactory.Create(format);
                await Task.Run(() => engine.Pack(scratch, Counting(entries, progress), level, ct), ct);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(scratch, target, overwrite: true);
        }
        catch
        {
            TryDelete(scratch);
            throw;
        }
    }

    private static IEnumerable<(string localPath, string entryName)> Counting(
        List<(string localPath, string entryName)> entries, IProgress<double>? progress)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            yield return entries[i];
            progress?.Report((double)(i + 1) / entries.Count);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a stray hidden scratch file must not mask the real error.
        }
    }
}

/// <summary>What unpacking an archive into a folder would do to files already there.</summary>
public static class UnpackPlanner
{
    /// <summary>
    /// Destination paths of existing files the extraction would overwrite. Also validates every
    /// entry name up front (zip-slip): a hostile archive is refused before a single file is written.
    /// </summary>
    /// <exception cref="InvalidDataException">An entry would escape <paramref name="destDir"/>.</exception>
    public static IReadOnlyList<string> FindOverwrites(IEnumerable<ArchiveEntry> entries, string destDir)
    {
        var existing = new List<string>();
        foreach (var entry in entries)
        {
            var target = ArchivePathSafety.ResolveDestination(destDir, entry.FullPath);
            if (!entry.IsDirectory && File.Exists(target))
            {
                existing.Add(target);
            }
        }

        return existing;
    }
}

/// <summary>One archive Alt+F9 unpacks.</summary>
/// <param name="IsZip">A ZIP: Windows itself can extract it.</param>
public sealed record UnpackArchive(string Name, string Path, bool IsZip);

/// <summary>Which of the selected files Alt+F9 unpacks.</summary>
public static class UnpackSelection
{
    /// <summary>
    /// Every selected file that is an archive, in the order given; folders and other files are passed
    /// over, as Total Commander does. The caller hands the selection, or the item under the cursor
    /// when nothing is selected.
    /// </summary>
    /// <param name="isArchive">Tells an archive by its content (reads the file: call off the UI thread).</param>
    /// <param name="isZip">Tells a ZIP among them.</param>
    public static IReadOnlyList<UnpackArchive> Pick(
        IEnumerable<(string Name, string Path, bool IsDirectory)> selected,
        Func<string, bool> isArchive,
        Func<string, bool> isZip) =>
        selected
            .Where(s => !s.IsDirectory && isArchive(s.Path))
            .Select(s => new UnpackArchive(s.Name, s.Path, isZip(s.Path)))
            .ToList();
}
