using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Navigation;

/// <summary>One indexed entry: the path to act on, plus the relative form the picker matches on.</summary>
/// <param name="FullPath">Path inside the file system that produced it (openable, navigable).</param>
/// <param name="RelativePath">Slash-separated path relative to the index root — what the user reads and searches.</param>
public sealed record FileIndexEntry(string FullPath, string RelativePath, string Name, bool IsDirectory)
{
    /// <summary>Directory part of <see cref="RelativePath"/>, empty at the index root.</summary>
    public string RelativeDirectory
    {
        get
        {
            var slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? string.Empty : RelativePath[..slash];
        }
    }

    /// <summary>Display form: directories get a trailing slash, as in every other dev tool.</summary>
    public string DisplayName => IsDirectory ? Name + "/" : Name;
}

/// <summary>Limits and filters for one index pass.</summary>
public sealed record FileIndexOptions
{
    /// <summary>Honour <c>.gitignore</c> files found during the walk. Off for non-disk sources.</summary>
    public bool RespectGitignore { get; init; } = true;

    /// <summary>Index directories too — they are what Tab drills into.</summary>
    public bool IncludeDirectories { get; init; } = true;

    public bool IncludeHidden { get; init; }

    /// <summary>Hard cap; the walk stops once this many entries were produced.</summary>
    public int MaxEntries { get; init; } = 100_000;

    /// <summary>Directory nesting limit, counted from the root.</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>
    /// Directories never entered. VCS metadata is not listed in <c>.gitignore</c> and holds tens of
    /// thousands of files nobody navigates to by name.
    /// </summary>
    public IReadOnlyCollection<string> SkipDirectories { get; init; } = [".git", ".hg", ".svn"];

    /// <summary>
    /// Do not descend into junctions and symlinks. The semantic indexer sets it: a link inside the
    /// project can lead anywhere, and what it reaches would then be embedded and served to agents
    /// as if it belonged to the project.
    /// </summary>
    public bool SkipReparsePoints { get; init; }
}

/// <summary>
/// Breadth-first index of a directory tree over any <see cref="IVirtualFileSystem"/> (INV-02),
/// yielding entries as they are found so a picker can show the shallow — usually most relevant —
/// files immediately. Gitignore rules are read from the walked tree itself, one rule set per
/// directory, and an ignored directory is never entered.
/// </summary>
public sealed class FileIndexWalker
{
    private readonly IVirtualFileSystem _vfs;
    private readonly FileIndexOptions _options;

    public FileIndexWalker(IVirtualFileSystem vfs, FileIndexOptions? options = null)
    {
        _vfs = vfs;
        _options = options ?? new FileIndexOptions();
    }

    /// <summary>
    /// True when the walk stopped because <see cref="FileIndexOptions.MaxEntries"/> was hit. Read it
    /// after the enumeration finishes: one walker serves one pass.
    /// </summary>
    public bool WasTruncated { get; private set; }

    public async IAsyncEnumerable<FileIndexEntry> EnumerateAsync(
        VfsPath root, [EnumeratorCancellation] CancellationToken ct = default)
    {
        WasTruncated = false;
        var produced = 0;
        var queue = new Queue<Level>();
        queue.Enqueue(new Level(root, string.Empty, 0, GitignoreScope.Empty));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var level = queue.Dequeue();

            IReadOnlyList<IFileEntry> entries;
            try
            {
                entries = await _vfs.ListAsync(level.Path, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }   // unreadable directory (permissions, vanished) — skip it

            var scope = _options.RespectGitignore
                ? await PushGitignoreAsync(level, entries, ct)
                : level.Scope;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (!_options.IncludeHidden && IsHidden(entry)) continue;

                var relative = level.RelativePath.Length == 0
                    ? entry.Name
                    : level.RelativePath + "/" + entry.Name;

                if (_options.RespectGitignore && scope.IsIgnoredEntry(relative, entry.IsDirectory)) continue;

                if (entry.IsDirectory)
                {
                    if (_options.SkipDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                        continue;

                    if (_options.SkipReparsePoints && entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    if (_options.IncludeDirectories)
                    {
                        yield return new FileIndexEntry(entry.Path.Value, relative, entry.Name, true);
                        if (++produced >= _options.MaxEntries) { WasTruncated = true; yield break; }
                    }

                    // A junction or symlink is listed (it can be picked) but not entered: it may point back up
                    // the tree, and the index would fill with copies of it.
                    var followable = !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                    if (followable && level.Depth + 1 <= _options.MaxDepth)
                    {
                        queue.Enqueue(new Level(entry.Path, relative, level.Depth + 1, scope));
                    }
                }
                else
                {
                    yield return new FileIndexEntry(entry.Path.Value, relative, entry.Name, false);
                    if (++produced >= _options.MaxEntries) { WasTruncated = true; yield break; }
                }
            }
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private readonly record struct Level(VfsPath Path, string RelativePath, int Depth, GitignoreScope Scope);

    private const int MaxGitignoreBytes = 256 * 1024;

    private async Task<GitignoreScope> PushGitignoreAsync(
        Level level, IReadOnlyList<IFileEntry> entries, CancellationToken ct)
    {
        var file = entries.FirstOrDefault(e =>
            !e.IsDirectory && string.Equals(e.Name, ".gitignore", StringComparison.OrdinalIgnoreCase));
        if (file is null) return level.Scope;

        try
        {
            await using var stream = await _vfs.OpenReadAsync(file.Path, ct);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxGitignoreBytes];
            var read = await reader.ReadAsync(buffer, ct);
            return level.Scope.Push(level.RelativePath, GitignoreRules.Parse(new string(buffer, 0, read)));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return level.Scope;   // an unreadable .gitignore must not abort the index
        }
    }

    // Only the Windows attributes count as hidden. Dot-files are exactly what a developer looks
    // for by name (.gitignore, .env, .github/workflows/ci.yml), so they stay in the index; the
    // one dot-directory worth skipping is handled by SkipDirectories.
    private static bool IsHidden(IFileEntry entry) =>
        entry.Attributes.HasFlag(FileAttributes.Hidden) ||
        entry.Attributes.HasFlag(FileAttributes.System);
}
