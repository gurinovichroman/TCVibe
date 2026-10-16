using TCV.Core.PluginSdk;

namespace TCV.Archives.Engines;

/// <summary>
/// Turns the raw entry names of any archive format into the listing the panel shows: forward
/// slashes, no leading <c>./</c> or <c>/</c>, and a directory entry for every implied parent.
/// One implementation for all engines — the ISO and CAB engines used to hand out names with a
/// leading slash and no directories, which left their panels empty.
/// </summary>
internal sealed class EntryListBuilder
{
    private readonly List<ArchiveEntry> _entries = [];
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Slash-normalised entry path; empty for the archive root.</summary>
    public static string NormalizePath(string raw)
    {
        var path = (raw ?? string.Empty).Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path == "." ? string.Empty : path.Trim('/');
    }

    public void AddDirectory(string rawPath, DateTime modified)
    {
        var path = NormalizePath(rawPath);
        if (path.Length == 0)
        {
            return;
        }

        EnsureParents(path, modified);
        if (_directories.Add(path))
        {
            _entries.Add(new ArchiveEntry(path, LastSegment(path), true, 0, modified));
        }
    }

    public void AddFile(string rawPath, long size, DateTime modified)
    {
        var path = NormalizePath(rawPath);
        if (path.Length == 0)
        {
            return;
        }

        EnsureParents(path, modified);
        _entries.Add(new ArchiveEntry(path, LastSegment(path), false, size, modified));
    }

    public IReadOnlyList<ArchiveEntry> Build() => _entries;

    private void EnsureParents(string path, DateTime modified)
    {
        var slash = path.LastIndexOf('/');
        while (slash > 0)
        {
            var parent = path[..slash];
            if (!_directories.Add(parent))
            {
                return;
            }

            _entries.Add(new ArchiveEntry(parent, LastSegment(parent), true, 0, modified));
            slash = parent.LastIndexOf('/');
        }
    }

    private static string LastSegment(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
